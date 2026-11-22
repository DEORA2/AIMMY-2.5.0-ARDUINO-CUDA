using System;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Windows;

namespace MouseMovementLibraries.ArduinoSupport
{
    internal class ArduinoInput
    {
        // Arduino Mouse.move() takes signed chars; anything past ±127 in one command
        // wraps into a large move the opposite way, so bigger moves are split.
        private const int MaxStep = 127;
        // A stalled board must not freeze the AI loop; a dropped frame is corrected
        // by the next one.
        private const int WriteTimeoutMs = 50;
        // "-127,-127,1\n" is 12 bytes.
        private const int MaxCommandBytes = 16;

        private readonly SerialPort serialPort;
        // Trigger clicks resume on a thread-pool thread after their delay and can hit
        // the port at the same moment as an aim move; unsynchronized writes interleave
        // bytes and the board parses a garbage coordinate.
        private readonly object writeLock = new();
        private readonly byte[] buffer = new byte[MaxCommandBytes * 8];

        public ArduinoInput(string comPortNumber)
        {
            string portNumber = Regex.Match(comPortNumber ?? "", @"\d+").Value;

            if (string.IsNullOrEmpty(portNumber))
                throw new ArgumentException("Invalid COM port input.");

            string comPort = $"COM{portNumber}";
            serialPort = new SerialPort(comPort, 115200)
            {
                WriteTimeout = WriteTimeoutMs
            };
            // Unread replies from the board fill its TX buffer and make it stall, so
            // drain anything it sends back.
            serialPort.DataReceived += (_, _) =>
            {
                try { serialPort.DiscardInBuffer(); } catch { }
            };

            try
            {
                serialPort.Open();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open {comPort}: {ex.Message}", "COM Port Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        public void SendMouseCommand(int x, int y, int click)
        {
            lock (writeLock)
            {
                int length = AppendCommand(0, x, y, click);
                Write(length);
            }
        }

        public void SendMouseMove(int x, int y)
        {
            lock (writeLock)
            {
                int length = 0;
                while (x != 0 || y != 0)
                {
                    if (length + MaxCommandBytes > buffer.Length)
                    {
                        Write(length);
                        length = 0;
                    }

                    int stepX = Math.Clamp(x, -MaxStep, MaxStep);
                    int stepY = Math.Clamp(y, -MaxStep, MaxStep);
                    length = AppendCommand(length, stepX, stepY, 0);
                    x -= stepX;
                    y -= stepY;
                }

                if (length > 0)
                    Write(length);
            }
        }

        private int AppendCommand(int offset, int x, int y, int click)
        {
            Span<byte> span = buffer.AsSpan(offset);
            int written = 0;

            x.TryFormat(span, out int n, default, CultureInfo.InvariantCulture);
            written += n;
            span[written++] = (byte)',';

            y.TryFormat(span[written..], out n, default, CultureInfo.InvariantCulture);
            written += n;
            span[written++] = (byte)',';

            click.TryFormat(span[written..], out n, default, CultureInfo.InvariantCulture);
            written += n;
            span[written++] = (byte)'\n';

            return offset + written;
        }

        private void Write(int length)
        {
            if (!serialPort.IsOpen)
                return;

            try
            {
                serialPort.Write(buffer, 0, length);
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }

        public void Close()
        {
            lock (writeLock)
            {
                if (serialPort.IsOpen)
                    serialPort.Close();
            }
        }
    }
}
