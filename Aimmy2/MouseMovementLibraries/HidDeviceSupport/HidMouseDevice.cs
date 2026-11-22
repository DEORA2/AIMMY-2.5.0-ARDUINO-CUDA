using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Other;
using Visuality;

namespace Whales2.MouseMovementLibraries.HidDeviceSupport
{
    /// <summary>
    /// Communicates with the RP2350-USB-A HID device to inject mouse movements,
    /// clicks, and scrolls via vendor-defined HID output reports.
    /// Uses raw Windows HID API (P/Invoke) — no external dependencies.
    /// </summary>
    public class HidMouseDevice : IDisposable
    {
        // Must match firmware VID/PID (spoofed Logitech mouse)
        private const int VENDOR_ID = 0x046D;
        private const int PRODUCT_ID = 0xC077;

        // Vendor output report (matches firmware hid_inject.h)
        private const byte REPORT_ID = 0xAA;
        private const byte CMD_MOVE = 0x01;
        private const byte CMD_CLICK = 0x02;
        private const byte CMD_SCROLL = 0x03;

        private FileStream? _deviceStream;
        private SafeFileHandle? _deviceHandle;
        private bool _isConnected;
        private bool _disposed;

        public bool IsConnected => _isConnected;

        // ─── P/Invoke declarations ──────────────────────────────────────

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, int memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            int deviceInterfaceDetailDataSize,
            ref int requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(
            SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(
            SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(
            IntPtr preparsedData, ref HIDP_CAPS capabilities);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x01;
        private const uint FILE_SHARE_WRITE = 0x02;
        private const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        // ─── Public API ─────────────────────────────────────────────────

        /// <summary>
        /// Discover and connect to the RP2350 HID device.
        /// Returns true on success.
        /// </summary>
        public bool Connect()
        {
            if (_isConnected) return true;

            try
            {
                string? path = FindDevicePath(VENDOR_ID, PRODUCT_ID);
                if (path == null)
                {
                    new NoticeBar($"RP2350 HID Device not found. Ensure it is plugged in", 5000).Show();
                    return false;
                }

                _deviceHandle = CreateFile(
                    path,
                    GENERIC_WRITE | GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (_deviceHandle.IsInvalid)
                {
                    new NoticeBar($"Failed to open RP2350 HID Device (error {Marshal.GetLastWin32Error()}).", 5000).Show();
                    return false;
                }

                _deviceStream = new FileStream(_deviceHandle, FileAccess.ReadWrite, 8, false);
                _isConnected = true;

                new NoticeBar($"RP2350 HID Device connected successfully.", 5000).Show();
                return true;


            }
            catch (Exception ex)
            {
                new NoticeBar($"RP2350 HID Device connection failed: {ex.Message}", 5000).Show();
                return false;
            }
        }

        /// <summary>
        /// Disconnect from the RP2350 HID device.
        /// </summary>
        public void Disconnect()
        {
            _isConnected = false;
            _deviceStream?.Close();
            _deviceStream = null;
            _deviceHandle?.Close();
            _deviceHandle = null;
        }

        /// <summary>
        /// Send a relative mouse move command.
        /// </summary>
        public void SendMove(int dx, int dy)
        {
            SendCommand(CMD_MOVE, (short)dx, (short)dy);
        }

        /// <summary>
        /// Send a mouse click command.
        /// buttonMask: bit 0 = left, bit 1 = right, bit 2 = middle
        /// state: 1 = press, 0 = release
        /// </summary>
        public void SendClick(byte buttonMask, byte state)
        {
            SendCommand(CMD_CLICK, buttonMask, state);
        }

        /// <summary>
        /// Send a scroll command.
        /// </summary>
        public void SendScroll(int vscroll, int hscroll = 0)
        {
            SendCommand(CMD_SCROLL, (short)vscroll, (short)hscroll);
        }

        // ─── Internal helpers ───────────────────────────────────────────

        private void SendCommand(byte cmd, short param1, short param2)
        {
            if (!_isConnected || _deviceStream == null) return;

            try
            {
                // Report format: [ReportID][Cmd][P1lo][P1hi][P2lo][P2hi][0][0]
                byte[] report = new byte[8];
                report[0] = REPORT_ID;
                report[1] = cmd;
                report[2] = (byte)(param1 & 0xFF);
                report[3] = (byte)((param1 >> 8) & 0xFF);
                report[4] = (byte)(param2 & 0xFF);
                report[5] = (byte)((param2 >> 8) & 0xFF);
                report[6] = 0;
                report[7] = 0;

                _deviceStream.Write(report, 0, report.Length);
            }
            catch (IOException)
            {
                // Device disconnected
                _isConnected = false;
                new NoticeBar($"RP2350 HID Device disconnected (write error).", 5000).Show();
            }
        }

        /// <summary>
        /// Enumerate HID devices and find the one matching the given VID/PID
        /// that has an output report (the config interface with our vendor report).
        /// </summary>
        private static string? FindDevicePath(int vid, int pid)
        {
            HidD_GetHidGuid(out Guid hidGuid);

            IntPtr deviceInfoSet = SetupDiGetClassDevs(
                ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
                return null;

            try
            {
                SP_DEVICE_INTERFACE_DATA interfaceData = new();
                interfaceData.cbSize = Marshal.SizeOf(interfaceData);

                for (int i = 0; SetupDiEnumDeviceInterfaces(
                    deviceInfoSet, IntPtr.Zero, ref hidGuid, i, ref interfaceData); i++)
                {
                    string? path = GetDeviceInterfacePath(deviceInfoSet, ref interfaceData);
                    if (path == null) continue;

                    SafeFileHandle handle = CreateFile(
                        path,
                        GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        0,
                        IntPtr.Zero);

                    if (handle.IsInvalid) continue;

                    try
                    {
                        HIDD_ATTRIBUTES attrs = new();
                        attrs.Size = Marshal.SizeOf(attrs);

                        if (HidD_GetAttributes(handle, ref attrs) &&
                            attrs.VendorID == vid &&
                            attrs.ProductID == pid)
                        {
                            // Check if this interface has an output report
                            // (our vendor command interface has OutputReportByteLength > 0)
                            if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
                            {
                                try
                                {
                                    HIDP_CAPS caps = new();
                                    HidP_GetCaps(preparsed, ref caps);

                                    if (caps.OutputReportByteLength > 0)
                                    {
                                        // This is the config interface with our vendor output report
                                        return path;
                                    }
                                }
                                finally
                                {
                                    HidD_FreePreparsedData(preparsed);
                                }
                            }
                        }
                    }
                    finally
                    {
                        handle.Close();
                    }
                }

                return null;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        private static string? GetDeviceInterfacePath(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
        {
            int requiredSize = 0;
            SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet, ref interfaceData,
                IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);

            if (requiredSize <= 0) return null;

            IntPtr detailData = Marshal.AllocHGlobal(requiredSize);
            try
            {
                // Set cbSize to the size of the fixed part of the struct
                // On 64-bit: 8, on 32-bit: 6 (4 + 2 padding for TCHAR)
                Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);

                if (SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData,
                    detailData, requiredSize, ref requiredSize, IntPtr.Zero))
                {
                    // Path starts at offset 4 (after cbSize)
                    return Marshal.PtrToStringAuto(detailData + 4);
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(detailData);
            }
        }

        // ─── IDisposable ────────────────────────────────────────────────

        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~HidMouseDevice()
        {
            Dispose();
        }
    }
}