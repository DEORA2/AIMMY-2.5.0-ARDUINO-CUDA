using Whales2.Class;
using Class;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using MouseMovementLibraries.ArduinoSupport;
using Whales2.MouseMovementLibraries.HidDeviceSupport;

namespace InputLogic
{
    internal class MouseManager
    {
        private static readonly double ScreenWidth = WinAPICaller.ScreenWidth;
        private static readonly double ScreenHeight = WinAPICaller.ScreenHeight;

        private static ArduinoInput? arduinoInput;


        private static DateTime LastClickTime = DateTime.MinValue;
        private static bool isSpraying = false;

        private static double previousX = 0;
        private static double previousY = 0;
        public static double smoothingFactor = 0.5;
        public static bool IsEMASmoothingEnabled = false;
        public static HidMouseDevice? HidDevice = null;

        // Non-blocking mouse sender: AI loop drops the latest desired (dx, dy, method)
        // into _pendingMove and signals; the sender thread reads it and writes to the
        // device off the hot path. Intermediate moves between slow writes are coalesced
        // (only the newest target matters). Keeps the AI loop from stalling on serial / HID I/O.
        private static int _pendingX;
        private static int _pendingY;
        private static int _pendingHasMove;
        private static string _pendingMethod = "Arduino";
        private static readonly AutoResetEvent _moveSignal = new AutoResetEvent(false);
        private static Thread? _senderThread;
        private static volatile bool _senderRunning;

        static MouseManager()
        {
            StartSenderThread();
        }

        private static void StartSenderThread()
        {
            if (_senderThread != null && _senderThread.IsAlive) return;
            _senderRunning = true;
            _senderThread = new Thread(SenderLoop)
            {
                IsBackground = true,
                Name = "MouseMoveSender",
                Priority = ThreadPriority.AboveNormal
            };
            _senderThread.Start();
        }

        private static void SenderLoop()
        {
            while (_senderRunning)
            {
                _moveSignal.WaitOne();

                if (Interlocked.Exchange(ref _pendingHasMove, 0) == 0) continue;

                int x = Interlocked.Exchange(ref _pendingX, 0);
                int y = Interlocked.Exchange(ref _pendingY, 0);
                string method = _pendingMethod;

                try
                {
                    switch (method)
                    {
                        case "RP2350":
                            HidDevice?.SendMove(x, y);
                            break;
                        case "Arduino":
                        default:
                            arduinoInput?.SendMouseCommand(x, y, 0);
                            break;
                    }
                }
                catch { /* device errors are reported by the device itself */ }
            }
        }

        private static void EnqueueMove(int dx, int dy, string method)
        {
            // Latest-wins: accumulate deltas so a skipped frame still steers the cursor,
            // but never block the AI loop. If the sender hasn't consumed the previous
            // move yet, we add our delta on top — the next sender pass flushes the total.
            Interlocked.Add(ref _pendingX, dx);
            Interlocked.Add(ref _pendingY, dy);
            _pendingMethod = method;
            Interlocked.Exchange(ref _pendingHasMove, 1);
            _moveSignal.Set();
        }

        public static void InitArduino(string comPort)
        {
            try
            {
                arduinoInput = new ArduinoInput(comPort);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to connect to {comPort}:\n{ex.Message}", "Arduino Error");
            }
        }

        private static Random MouseRandom = new();

        private static double EmaSmoothing(double previousValue, double currentValue, double smoothingFactor) => (currentValue * smoothingFactor) + (previousValue * (1 - smoothingFactor));

        // Cleanup
        private static (Action down, Action up) GetMouseActions()
        {
            string mouseMovementMethod = Dictionary.dropdownState["Mouse Movement Method"];
            Action mouseDownAction;
            Action mouseUpAction;

            switch (mouseMovementMethod)
            {
                default:
                    mouseDownAction = () => arduinoInput?.SendMouseCommand(0,0, 1);
                    mouseUpAction = () => arduinoInput?.SendMouseCommand(0,0, 0);
                    break;
            }

            return (mouseDownAction, mouseUpAction);
        }

        public static async Task DoTriggerClick(RectangleF? detectionBox = null)
        {
            // there was a toggle for this, but i realized if it was off, it would never stop spraying. - T
            if (!(InputBindingManager.IsHoldingBinding("Aim Keybind") || InputBindingManager.IsHoldingBinding("Second Aim Keybind")))
            {
                ResetSprayState();
                return;
            }


            if (Dictionary.toggleState["Spray Mode"])
            {
                if (Dictionary.toggleState["Cursor Check"])
                {
                    Point mousePos = WinAPICaller.GetCursorPosition();

                    if (detectionBox.HasValue && !detectionBox.Value.Contains(mousePos.X, mousePos.Y))
                    {
                        if (isSpraying) ReleaseMouseButton();
                        return;
                    }
                }

                if (!isSpraying) HoldMouseButton();
                return;
            }

            // Single click logic if spray mode off
            int timeSinceLastClick = (int)(DateTime.UtcNow - LastClickTime).TotalMilliseconds;
            int triggerDelayMilliseconds = (int)(Dictionary.sliderSettings["Auto Trigger Delay"] * 1000);
            const int clickDelayMilliseconds = 20;

            if (timeSinceLastClick < triggerDelayMilliseconds && LastClickTime != DateTime.MinValue)
            {
                return;
            }

            var (mouseDown, mouseUp) = GetMouseActions();

            mouseDown.Invoke();
            await Task.Delay(clickDelayMilliseconds);
            mouseUp.Invoke();

            LastClickTime = DateTime.UtcNow;
        }

        #region Spray Mode Methods
        public static void HoldMouseButton()
        {
            if (isSpraying) return;

            var (mouseDown, _) = GetMouseActions();
            mouseDown.Invoke();
            isSpraying = true;
        }

        public static void ReleaseMouseButton()
        {
            if (!isSpraying) return;

            var (_, mouseUp) = GetMouseActions();
            mouseUp.Invoke();
            isSpraying = false;
        }

        public static void ResetSprayState()
        {
            if (isSpraying)
            {
                ReleaseMouseButton();
            }
        }
        #endregion

        public static void MoveCrosshair(int detectedX, int detectedY)
        {
            int halfScreenWidth = (int)ScreenWidth / 2;
            int halfScreenHeight = (int)ScreenHeight / 2;

            int targetX = detectedX - halfScreenWidth;
            int targetY = detectedY - halfScreenHeight;

            double aspectRatioCorrection = ScreenWidth / ScreenHeight;

            int MouseJitter = (int)Dictionary.sliderSettings["Mouse Jitter"];
            int jitterX = MouseRandom.Next(-MouseJitter, MouseJitter);
            int jitterY = MouseRandom.Next(-MouseJitter, MouseJitter);

            Point start = new(0, 0);
            Point end = new(targetX, targetY);
            Point newPosition = new Point(0, 0);

            switch (Dictionary.dropdownState["Movement Path"])
            {
                case "Cubic Bezier":
                    Point control1 = new Point(start.X + (end.X - start.X) / 3, start.Y + (end.Y - start.Y) / 3);
                    Point control2 = new Point(start.X + 2 * (end.X - start.X) / 3, start.Y + 2 * (end.Y - start.Y) / 3);
                    newPosition = MovementPaths.CubicBezier(start, end, control1, control2, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Linear":
                    newPosition = MovementPaths.Lerp(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Exponential":
                    newPosition = MovementPaths.Exponential(start, end, 1 - (Dictionary.sliderSettings["Mouse Sensitivity (+/-)"] - 0.2), 3.0);
                    break;
                case "Adaptive":
                    newPosition = MovementPaths.Adaptive(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Perlin Noise":
                    newPosition = MovementPaths.PerlinNoise(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"], 20, 0.5);
                    break;
                case "Sine Wave":
                    newPosition = MovementPaths.SineWave(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Ease In-Out":
                    newPosition = MovementPaths.EaseInOut(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Overshoot":
                    newPosition = MovementPaths.Overshoot(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Catmull-Rom":
                    newPosition = MovementPaths.CatmullRom(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Smooth Step":
                    newPosition = MovementPaths.SmoothStep(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Spiral":
                    newPosition = MovementPaths.Spiral(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                default:
                    newPosition = MovementPaths.Lerp(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
            }

            if (IsEMASmoothingEnabled)
            {
                newPosition.X = (int)EmaSmoothing(previousX, newPosition.X, smoothingFactor);
                newPosition.Y = (int)EmaSmoothing(previousY, newPosition.Y, smoothingFactor);
            }

            newPosition.X = Math.Clamp(newPosition.X, -150, 150);
            newPosition.Y = Math.Clamp(newPosition.Y, -150, 150);

            newPosition.Y = (int)(newPosition.Y / aspectRatioCorrection);

            newPosition.X += jitterX;
            newPosition.Y += jitterY;

            string method = Dictionary.dropdownState["Mouse Movement Method"];
            if (method == "Arduino")
            {
                newPosition.X += jitterX;
                newPosition.Y += jitterY;
            }
            EnqueueMove(newPosition.X, newPosition.Y, method);

            previousX = newPosition.X;
            previousY = newPosition.Y;

            if (!Dictionary.toggleState["Auto Trigger"])
            {
                ResetSprayState();
            }
        }
    }
}