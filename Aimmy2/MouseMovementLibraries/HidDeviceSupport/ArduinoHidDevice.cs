using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Visuality;

namespace Whales2.MouseMovementLibraries.HidDeviceSupport
{
    /// <summary>
    /// Talks to an Arduino Leonardo + USB Host Shield running the mouse_bridge_rawhid
    /// firmware over its Raw-HID endpoint. Same role as <see cref="HidMouseDevice"/>
    /// but for the Leonardo instead of the RP2350 — different VID/PID, 64-byte reports
    /// without an explicit report ID, different command set.
    ///
    /// A board only counts as connected after it answers PING: other firmware can also expose
    /// a vendor-defined HID interface, and that must never receive these commands.
    ///
    /// Protocol (host → device, payload inside a 64-byte report; Windows prepends a
    /// zero byte because the firmware does not declare a report ID):
    ///   0x01 MOVE       int16 dx (LE), int16 dy (LE), int8 wheel
    ///   0x02 BUTTONS    uint8 mask (bit0 L, bit1 R, bit2 M, bit3 X1, bit4 X2)
    ///   0x03 CLICK      uint8 button idx (0..4), uint8 hold ms (0 = firmware default)
    ///   0x05 RESET      releases all buttons
    /// </summary>
    public class ArduinoHidDevice : IDisposable
    {
        // Accepted VID/PID pairs for the board.
        //   0xCAFE / 0x4002 — firmware built with custom boards.txt that renames the USB device
        //   0x2341 / 0x8036 — stock Arduino Leonardo IDs (what most people get when they just
        //                     flash the generated sketch without changing the core's USB IDs)
        // In both cases we only accept the HID collection that actually exposes an output
        // report — the plain mouse HID won't, so we won't try to write to it by accident.
        private static readonly (int vid, int pid)[] AcceptedIds =
        {
            (0xCAFE, 0x4002),
            (0x2341, 0x8036),
            (0x2341, 0x0036),  // Leonardo in bootloader mode — skipped at runtime by the output-report check
        };

        private const byte CMD_MOVE = 0x01;
        private const byte CMD_BUTTONS = 0x02;
        private const byte CMD_CLICK = 0x03;
        private const byte CMD_RESET = 0x05;
        private const byte CMD_PING = 0x10;   // firmware replies with 0x10, version major, minor

        // How long to wait for the PING reply before deciding the board isn't running this firmware
        private const int PingTimeoutMs = 400;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        // Button-mask bits — match the firmware's applyButtonMask().
        public const byte BTN_LEFT   = 0x01;
        public const byte BTN_RIGHT  = 0x02;
        public const byte BTN_MIDDLE = 0x04;
        public const byte BTN_X1     = 0x08;
        public const byte BTN_X2     = 0x10;

        private FileStream? _stream;
        private SafeFileHandle? _handle;
        private int _outputReportBytes;   // includes the leading report-id byte
        private byte[] _buffer = Array.Empty<byte>();
        private readonly object _writeLock = new();
        private bool _isConnected;
        private bool _disposed;

        public bool IsConnected => _isConnected;

        /// <summary>Why the last Connect() failed, for the caller's message.</summary>
        public string? LastError { get; private set; }

        /// <summary>Firmware version reported by PING, e.g. "2.0".</summary>
        public string? FirmwareVersion { get; private set; }

        // ─── P/Invoke ───────────────────────────────────────────────────

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint GENERIC_READ  = 0x80000000;
        private const uint FILE_SHARE_READ  = 0x01;
        private const uint FILE_SHARE_WRITE = 0x02;
        private const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage; public ushort UsagePage;
            public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
        }

        // ─── Public API ─────────────────────────────────────────────────

        /// <param name="showNotices">Show connected / failed notices. Startup passes false unless this device is selected.</param>
        public bool Connect(bool showNotices = true)
        {
            if (_isConnected) return true;
            LastError = null;
            try
            {
                var candidates = FindCandidates(AcceptedIds);
                if (candidates.Count == 0)
                {
                    LastError = "Arduino (Raw HID) not found. Flash the mouse_bridge_rawhid firmware to your Leonardo and plug it in.";
                    if (showNotices) new NoticeBar(LastError, 5000).Show();
                    return false;
                }

                // Only a board that answers PING is running the Raw HID firmware
                foreach (var candidate in candidates)
                {
                    if (TryOpen(candidate))
                    {
                        _isConnected = true;
                        if (showNotices) new NoticeBar($"Arduino (Raw HID) connected (firmware {FirmwareVersion}).", 4000).Show();
                        return true;
                    }
                }

                LastError = "Found an Arduino Leonardo, but it didn't answer the Raw HID handshake, so it isn't running " +
                            "the mouse_bridge_rawhid firmware. Flash that firmware, or pick \"Arduino\" (COM port) instead.";
                if (showNotices) new NoticeBar(LastError, 6000).Show();
                return false;
            }
            catch (Exception ex)
            {
                LastError = $"Arduino (Raw HID) connection failed: {ex.Message}";
                if (showNotices) new NoticeBar(LastError, 5000).Show();
                return false;
            }
        }

        /// <summary>Opens one candidate interface and keeps it only if the firmware answers PING.</summary>
        private bool TryOpen(Found candidate)
        {
            if (candidate.inputReportBytes < 2) return false; // no input report: it can't answer

            var handle = CreateFile(candidate.path,
                GENERIC_WRITE | GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return false;
            }

            var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
            try
            {
                // OutputReportByteLength already includes the leading report-id byte
                int outBytes = Math.Max(2, candidate.outputReportBytes);
                var ping = new byte[outBytes];
                ping[1] = CMD_PING;
                using var timeout = new CancellationTokenSource(PingTimeoutMs);
                stream.WriteAsync(ping, 0, ping.Length, timeout.Token).GetAwaiter().GetResult();

                var reply = new byte[candidate.inputReportBytes];
                while (!timeout.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = stream.ReadAsync(reply, 0, reply.Length, timeout.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // reply[0] is the report ID (0); the firmware also sends 0x20 mouse events, skip those
                    if (read >= 4 && reply[1] == CMD_PING)
                    {
                        FirmwareVersion = $"{reply[2]}.{reply[3]}";
                        _outputReportBytes = outBytes;
                        _buffer = new byte[outBytes];
                        _handle = handle;
                        _stream = stream;
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) { } // the PING write itself timed out
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            stream.Dispose(); // also closes the handle
            return false;
        }

        public void Disconnect()
        {
            // Let go of any held button (e.g. spray mode) before closing
            if (_isConnected) SendReset();
            _isConnected = false;
            _stream?.Close(); _stream = null;
            _handle?.Close(); _handle = null;
        }

        /// <summary>Relative move. Dx / Dy are int16 LE; wheel is int8.</summary>
        public void SendMove(int dx, int dy, int wheel = 0)
        {
            short sx = (short)Math.Clamp(dx, short.MinValue, short.MaxValue);
            short sy = (short)Math.Clamp(dy, short.MinValue, short.MaxValue);
            sbyte sw = (sbyte)Math.Clamp(wheel, sbyte.MinValue, sbyte.MaxValue);
            SendReport(CMD_MOVE, (b, off) =>
            {
                b[off + 0] = (byte)(sx & 0xff);
                b[off + 1] = (byte)((sx >> 8) & 0xff);
                b[off + 2] = (byte)(sy & 0xff);
                b[off + 3] = (byte)((sy >> 8) & 0xff);
                b[off + 4] = (byte)sw;
            });
        }

        /// <summary>Set absolute button mask (bit0 L, 1 R, 2 M, 3 X1, 4 X2).</summary>
        public void SendButtons(byte mask)
        {
            SendReport(CMD_BUTTONS, (b, off) => { b[off + 0] = mask; });
        }

        /// <summary>Press-release a single button on the firmware side.</summary>
        public void SendClick(byte buttonIdx, byte holdMs = 0)
        {
            SendReport(CMD_CLICK, (b, off) =>
            {
                b[off + 0] = buttonIdx;
                b[off + 1] = holdMs;
            });
        }

        public void SendReset() => SendReport(CMD_RESET, null);

        // ─── Internal ───────────────────────────────────────────────────

        private void SendReport(byte cmd, Action<byte[], int>? fill)
        {
            if (!_isConnected || _stream == null) return;
            lock (_writeLock)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                // _buffer[0] = report ID (0 when the firmware declares none).
                // Command and payload start at _buffer[1].
                _buffer[1] = cmd;
                fill?.Invoke(_buffer, 2);
                try
                {
                    _stream.Write(_buffer, 0, _buffer.Length);
                }
                catch (IOException)
                {
                    // The firmware only takes a new report once it has read the previous one, so a write can be
                    // refused while it's busy (e.g. holding a click). Try once more before giving up on the board.
                    Thread.Sleep(2);
                    try
                    {
                        _stream.Write(_buffer, 0, _buffer.Length);
                    }
                    catch (IOException)
                    {
                        _isConnected = false;
                        // Writes come from the AI thread; windows must be created on the UI thread
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                            new NoticeBar("Arduino (Raw HID) disconnected (write error).", 5000).Show());
                    }
                }
            }
        }

        private readonly record struct Found(string path, int outputReportBytes, int inputReportBytes);

        /// <summary>Every HID interface with an accepted VID/PID that looks like a Raw HID channel
        /// (vendor-defined usage page with an output report). Connect() then checks each with PING.</summary>
        private static List<Found> FindCandidates((int vid, int pid)[] accepted)
        {
            var found = new List<Found>();
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return found;

            try
            {
                SP_DEVICE_INTERFACE_DATA iface = new();
                iface.cbSize = Marshal.SizeOf(iface);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
                {
                    string? path = GetDeviceInterfacePath(set, ref iface);
                    if (path == null) continue;

                    SafeFileHandle handle = CreateFile(path, GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;

                    try
                    {
                        HIDD_ATTRIBUTES attrs = new(); attrs.Size = Marshal.SizeOf(attrs);
                        if (!HidD_GetAttributes(handle, ref attrs)) continue;
                        bool match = false;
                        foreach (var (v, p) in accepted)
                            if (attrs.VendorID == v && attrs.ProductID == p) { match = true; break; }
                        if (!match) continue;

                        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) continue;
                        try
                        {
                            HIDP_CAPS caps = new();
                            HidP_GetCaps(preparsed, ref caps);
                            // The firmware exposes several HID collections (the mouse the PC
                            // sees, and the Raw HID control channel). Only accept the Raw HID
                            // one: a vendor-defined usage page (0xFF00-0xFFFF) with an output
                            // report. A keyboard collection also has an output report (its
                            // LEDs), so the output report alone isn't enough.
                            if (caps.UsagePage >= 0xFF00 && caps.OutputReportByteLength > 1)
                                found.Add(new Found(path, caps.OutputReportByteLength, caps.InputReportByteLength));
                        }
                        finally { HidD_FreePreparsedData(preparsed); }
                    }
                    finally { handle.Close(); }
                }
                return found;
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static string? GetDeviceInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
        {
            int required = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, ref required, IntPtr.Zero);
            if (required <= 0) return null;
            IntPtr detail = Marshal.AllocHGlobal(required);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                return SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, ref required, IntPtr.Zero)
                    ? Marshal.PtrToStringAuto(detail + 4)
                    : null;
            }
            finally { Marshal.FreeHGlobal(detail); }
        }

        // ─── IDisposable ────────────────────────────────────────────────

        public void Dispose()
        {
            if (!_disposed) { Disconnect(); _disposed = true; }
        }
    }
}
