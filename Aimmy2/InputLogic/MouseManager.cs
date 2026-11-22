using Whales2.Class;
using Class;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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

        public static void InitArduino(string comPort)
        {
            try
            {
                arduinoInput?.Close();
                arduinoInput = null;
                arduinoInput = new ArduinoInput(comPort);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to connect to {comPort}:\n{ex.Message}", "Arduino Error");
            }
        }

        private static readonly Random MouseRandom = new();

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

        // A gap longer than this means aim was released or the target was lost; stale
        // velocity / EMA / remainder from the previous engagement must not kick the
        // first move of the next one.
        private const double AIM_STATE_RESET_SECONDS = 0.1;
        // Fraction of the target's per-frame screen motion added as lead. Cancels the
        // 1–2 frame lag of proportional aim without lowering sensitivity.
        private const double LEAD_STRENGTH = 0.5;
        private const double LEAD_MAX_PIXELS = 25;
        // Bounding-box centers wobble by about a pixel frame to frame; errors this small
        // are noise, and chasing them is what makes the crosshair chatter on target.
        private const double AIM_DEADZONE_PIXELS = 1.0;
        private const double MAX_MOVE_PIXELS = 150;

        private static long _lastAimTick;
        private static double _lastRawX;
        private static double _lastRawY;
        private static double _smoothedVelX;
        private static double _smoothedVelY;
        // Sub-pixel carry: the device only takes whole pixels, so the fraction of each
        // move is kept and added to the next one instead of being truncated away.
        private static double _remainderX;
        private static double _remainderY;

        private static void ResetAimState(int detectedX, int detectedY)
        {
            _lastRawX = detectedX;
            _lastRawY = detectedY;
            _smoothedVelX = 0;
            _smoothedVelY = 0;
            _remainderX = 0;
            _remainderY = 0;
            previousX = 0;
            previousY = 0;
        }

        private static PointF ComputePathMove(PointF end, double sensitivity)
        {
            PointF start = PointF.Empty;
            double t = 1 - sensitivity;

            switch (Dictionary.dropdownState["Movement Path"])
            {
                case "Cubic Bezier":
                    PointF control1 = new PointF(end.X / 3f, end.Y / 3f);
                    PointF control2 = new PointF(2f * end.X / 3f, 2f * end.Y / 3f);
                    return MovementPaths.CubicBezier(start, end, control1, control2, t);
                case "Exponential":
                    return MovementPaths.Exponential(start, end, 1 - (sensitivity - 0.2), 3.0);
                case "Adaptive":
                    return MovementPaths.Adaptive(start, end, t);
                case "Perlin Noise":
                    return MovementPaths.PerlinNoise(start, end, t, 20, 0.5);
                case "Sine Wave":
                    return MovementPaths.SineWave(start, end, t);
                case "Ease In-Out":
                    return MovementPaths.EaseInOut(start, end, t);
                case "Overshoot":
                    return MovementPaths.Overshoot(start, end, t);
                case "Catmull-Rom":
                    return MovementPaths.CatmullRom(start, end, t);
                case "Smooth Step":
                    return MovementPaths.SmoothStep(start, end, t);
                case "Spiral":
                    return MovementPaths.Spiral(start, end, t);
                case "Linear":
                default:
                    return MovementPaths.Lerp(start, end, t);
            }
        }

        public static void MoveCrosshair(int detectedX, int detectedY)
        {
            long nowTick = Stopwatch.GetTimestamp();
            double dt = _lastAimTick == 0
                ? double.MaxValue
                : (nowTick - _lastAimTick) / (double)Stopwatch.Frequency;
            _lastAimTick = nowTick;

            double leadX = 0;
            double leadY = 0;
            if (dt <= 0 || dt > AIM_STATE_RESET_SECONDS)
            {
                ResetAimState(detectedX, detectedY);
            }
            else
            {
                // Velocity comes from raw detections only; feeding the leaded point back
                // in would make the lead amplify its own noise.
                double frameDeltaX = detectedX - _lastRawX;
                double frameDeltaY = detectedY - _lastRawY;
                _smoothedVelX = _smoothedVelX * 0.6 + (frameDeltaX / dt) * 0.4;
                _smoothedVelY = _smoothedVelY * 0.6 + (frameDeltaY / dt) * 0.4;

                leadX = Math.Clamp(_smoothedVelX * dt * LEAD_STRENGTH, -LEAD_MAX_PIXELS, LEAD_MAX_PIXELS);
                leadY = Math.Clamp(_smoothedVelY * dt * LEAD_STRENGTH, -LEAD_MAX_PIXELS, LEAD_MAX_PIXELS);
            }
            _lastRawX = detectedX;
            _lastRawY = detectedY;

            double errorX = detectedX + leadX - ScreenWidth / 2.0;
            double errorY = detectedY + leadY - ScreenHeight / 2.0;
            if (Math.Abs(errorX) < AIM_DEADZONE_PIXELS) errorX = 0;
            if (Math.Abs(errorY) < AIM_DEADZONE_PIXELS) errorY = 0;

            PointF pathMove = ComputePathMove(
                new PointF((float)errorX, (float)errorY),
                Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);

            double moveX = pathMove.X;
            double moveY = pathMove.Y;

            if (IsEMASmoothingEnabled)
            {
                moveX = EmaSmoothing(previousX, moveX, smoothingFactor);
                moveY = EmaSmoothing(previousY, moveY, smoothingFactor);
            }
            // EMA state is the smoothed path output itself, before clamping, aspect
            // scaling and jitter, so each frame blends like-for-like values.
            previousX = moveX;
            previousY = moveY;

            moveX = Math.Clamp(moveX, -MAX_MOVE_PIXELS, MAX_MOVE_PIXELS);
            moveY = Math.Clamp(moveY, -MAX_MOVE_PIXELS, MAX_MOVE_PIXELS);
            moveY /= ScreenWidth / ScreenHeight;

            double totalX = moveX + _remainderX;
            double totalY = moveY + _remainderY;
            int sendX = (int)Math.Truncate(totalX);
            int sendY = (int)Math.Truncate(totalY);
            _remainderX = totalX - sendX;
            _remainderY = totalY - sendY;

            int mouseJitter = (int)Dictionary.sliderSettings["Mouse Jitter"];
            if (mouseJitter > 0)
            {
                sendX += MouseRandom.Next(-mouseJitter, mouseJitter + 1);
                sendY += MouseRandom.Next(-mouseJitter, mouseJitter + 1);
            }

            if (sendX != 0 || sendY != 0)
            {
                switch (Dictionary.dropdownState["Mouse Movement Method"])
                {
                    case "RP2350":
                        HidDevice?.SendMove(sendX, sendY);
                        break;

                    case "Arduino":
                    default:
                        arduinoInput?.SendMouseMove(sendX, sendY);
                        break;
                }
            }

            if (!Dictionary.toggleState["Auto Trigger"])
            {
                ResetSprayState();
            }
        }
    }
}