using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;

namespace Whales2.MouseMovementLibraries.SketchBuilder
{
    /// <summary>
    /// Reads the report logger's output over the COM port: each line of hex bytes ("01 00 05 00 ...")
    /// becomes one report. Other lines (e.g. "USB Host Shield ready...") are passed on as status text.
    /// Events are raised on the serial port's thread.
    /// </summary>
    internal sealed class ReportCapture : IDisposable
    {
        private readonly SerialPort _port;
        private readonly object _lock = new();
        private string _pending = "";

        public event Action<byte[]>? ReportReceived;
        public event Action<string>? StatusLine;

        public string PortName => _port.PortName;

        public ReportCapture(string portName)
        {
            _port = new SerialPort(portName, 115200)
            {
                // The logger waits for the PC to open the port (DTR) before it starts
                DtrEnable = true,
                RtsEnable = true,
                NewLine = "\n",
                ReadTimeout = 500
            };
            _port.DataReceived += OnData;
        }

        public void Open() => _port.Open();

        private void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            string chunk;
            try { chunk = _port.ReadExisting(); }
            catch { return; }

            List<string> lines = new();
            lock (_lock)
            {
                _pending += chunk;
                int nl;
                while ((nl = _pending.IndexOf('\n')) >= 0)
                {
                    lines.Add(_pending[..nl].Trim());
                    _pending = _pending[(nl + 1)..];
                }
                if (_pending.Length > 4096) _pending = ""; // not our logger
            }

            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                var report = ParseHex(line);
                if (report != null) ReportReceived?.Invoke(report);
                else StatusLine?.Invoke(line);
            }
        }

        /// <summary>"01 0A FF" -> {0x01, 0x0A, 0xFF}; null if the line isn't only hex byte pairs.</summary>
        internal static byte[]? ParseHex(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 64) return null;
            var bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length != 2 || !byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    return null;
            }
            return bytes;
        }

        public void Dispose()
        {
            _port.DataReceived -= OnData;
            try { if (_port.IsOpen) _port.Close(); } catch { }
            _port.Dispose();
        }
    }
}
