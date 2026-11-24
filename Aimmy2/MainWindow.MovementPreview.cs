using Whales2.Class;
using Class;
using InputLogic;
using Other;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Whales2
{
    /// <summary>
    /// Aim page: animated preview of how the crosshair travels to a target with the selected movement path.
    /// It replays the same per-frame maths as MouseManager.MoveCrosshair (path step at t = 1 - sensitivity,
    /// smoothing, the 150 px cap, the vertical aspect scaling, whole-pixel sends and jitter) on a still target.
    /// </summary>
    public partial class MainWindow
    {
        private const double PathViewWidth = 420;
        private const double PathViewHeight = 170;
        private const double ChartHeight = 64;
        private const int MaxSimulatedFrames = 400;
        private const int HoldTicksAtEnd = 36;
        // Playback aims for about this many ticks (~2.5 s) however many frames the move takes
        private const int PlaybackTicks = 75;

        // The preview pretends detection runs at the live FPS, or 144 when nothing is running
        private double PreviewFps
        {
            get
            {
                double fps = FileManager.AIManager?.CurrentFps ?? 0;
                return fps > 1 ? fps : 144;
            }
        }

        // Start (crosshair) and target positions in preview pixels; 1 unit = 1 mouse count = 1 screen pixel
        private static readonly Point PathStart = new(50, 112);
        private static readonly Point PathTarget = new(330, 62);

        private Canvas? _pathCanvas;
        private Canvas? _pathChart;
        private TextBlock? _pathCaption;
        private DispatcherTimer? _pathTimer;
        private readonly List<Point> _pathFrames = new();
        private int _pathSettledFrame = -1;
        private int _pathTick;

        private static readonly Brush PathGrid = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush PathGhost = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xC3, 0xCD, 0xD3)));
        private static readonly Brush PathTargetBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x5D)));
        private static readonly Brush PathBackground = Frozen(new SolidColorBrush(Color.FromRgb(0x0E, 0x13, 0x17)));

        private FrameworkElement BuildMovementPreview()
        {
            _pathCanvas = new Canvas { Width = PathViewWidth, Height = PathViewHeight, ClipToBounds = true };
            _pathChart = new Canvas { Width = PathViewWidth, Height = ChartHeight, ClipToBounds = true };

            var inner = new StackPanel();
            inner.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _pathCanvas });
            var chartLabel = MakeSmall("Distance to target, frame by frame", 11);
            chartLabel.Margin = new Thickness(2, 8, 0, 4);
            inner.Children.Add(chartLabel);
            inner.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _pathChart });

            var frame = new Border
            {
                Background = PathBackground,
                BorderBrush = Res<Brush>("UI.Box"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Child = inner
            };

            _pathCaption = MakeSmall("");
            _pathCaption.Margin = new Thickness(2, 8, 2, 0);

            var outer = new StackPanel { Margin = new Thickness(14, 2, 14, 10) };
            outer.Children.Add(frame);
            outer.Children.Add(_pathCaption);

            // ~30 ticks a second, one simulated frame per tick (slowed down so each step is visible)
            _pathTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _pathTimer.Tick += (_, _) => AdvanceMovementPreview();
            outer.IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                {
                    SimulateMovementPath();
                    _pathTimer.Start();
                }
                else
                {
                    _pathTimer.Stop();
                }
            };

            return outer;
        }

        private string SelectedMovementPath =>
            SelectedText(uiManager.D_MovementPath) ?? (string)(Dictionary.dropdownState["Movement Path"]?.ToString() ?? "Linear");

        /// <summary>Re-runs the per-frame simulation with the current path, sensitivity, smoothing and jitter.</summary>
        internal void SimulateMovementPath()
        {
            if (_pathCanvas == null) return;

            string path = SelectedMovementPath;
            double sensitivity = SliderValue("Mouse Sensitivity (+/-)", 0.8);
            bool ema = IsOn("EMA Smoothening");
            double emaFactor = SliderValue("EMA Smoothening", 0.5);
            int jitter = (int)SliderValue("Mouse Jitter", 0);
            double aspect = WinAPICaller.ScreenHeight > 0 ? WinAPICaller.ScreenWidth / (double)WinAPICaller.ScreenHeight : 16.0 / 9.0;
            var random = new Random(7); // fixed seeds: the same settings always draw the same path
            var controller = new AimPathController(7);
            double frameSeconds = 1.0 / PreviewFps;

            _pathFrames.Clear();
            _pathSettledFrame = -1;

            double x = PathStart.X, y = PathStart.Y;
            double previousX = 0, previousY = 0, remainderX = 0, remainderY = 0;
            _pathFrames.Add(new Point(x, y));

            for (int frame = 1; frame <= MaxSimulatedFrames; frame++)
            {
                double errorX = PathTarget.X - x;
                double errorY = PathTarget.Y - y;
                if (Math.Abs(errorX) < 1) errorX = 0;
                if (Math.Abs(errorY) < 1) errorY = 0;

                var (moveX, moveY) = controller.NextMove(errorX, errorY, frameSeconds, sensitivity, path);

                if (ema)
                {
                    moveX = moveX * emaFactor + previousX * (1 - emaFactor);
                    moveY = moveY * emaFactor + previousY * (1 - emaFactor);
                }
                previousX = moveX;
                previousY = moveY;

                moveX = Math.Clamp(moveX, -150, 150);
                moveY = Math.Clamp(moveY, -150, 150) / aspect;

                double totalX = moveX + remainderX, totalY = moveY + remainderY;
                int sendX = (int)Math.Truncate(totalX), sendY = (int)Math.Truncate(totalY);
                remainderX = totalX - sendX;
                remainderY = totalY - sendY;

                if (jitter > 0)
                {
                    sendX += random.Next(-jitter, jitter + 1);
                    sendY += random.Next(-jitter, jitter + 1);
                }

                x += sendX;
                y += sendY;
                _pathFrames.Add(new Point(x, y));

                bool close = Distance(new Point(x, y), PathTarget) <= 2;
                if (close && _pathSettledFrame < 0)
                    _pathSettledFrame = frame;
                else if (!close)
                    _pathSettledFrame = -1;

                // Stop a little after it has settled (jitter keeps it moving, so cap at a few extra frames)
                if (_pathSettledFrame > 0 && frame - _pathSettledFrame >= 8)
                    break;
            }

            _pathTick = 0;
            UpdateMovementCaption(path, sensitivity);
            DrawMovementPreview(0);
        }

        private void UpdateMovementCaption(string path, double sensitivity)
        {
            if (_pathCaption == null) return;

            double fps = FileManager.AIManager?.CurrentFps ?? 0;
            string fpsText = fps > 1 ? $"your current {Math.Round(fps)} FPS" : "144 FPS";
            double fpsValue = PreviewFps;
            string what = PathShapes.Descriptions.TryGetValue(path, out var description) ? description + "\n" : "";

            if (_pathSettledFrame > 0)
            {
                _pathCaption.Text = what + $"At sensitivity {sensitivity:0.##} it is on target after {_pathSettledFrame} frames " +
                                    $"(about {_pathSettledFrame / fpsValue * 1000:0} ms at {fpsText}), then tracks it the same way for every path. " +
                                    "Played back slowed down, on a target that isn't moving.";
            }
            else
            {
                // Tell apart: still closing in slowly / parked beside the target / moving around it forever
                var window = _pathFrames.Skip(Math.Max(0, _pathFrames.Count - 30)).ToList();
                double firstGap = Distance(window[0], PathTarget);
                double lastGap = Distance(window[^1], PathTarget);
                // How far the crosshair wanders (whole-pixel rounding alone makes it wobble by ~1 px)
                var centre = new Point(window.Average(p => p.X), window.Average(p => p.Y));
                double wander = window.Max(p => Distance(p, centre));
                double furthest = window.Max(p => Distance(p, PathTarget));
                string prefix = what + $"At sensitivity {sensitivity:0.##} it is ";

                _pathCaption.Text = prefix + (lastGap < firstGap * 0.85
                    ? $"still closing in, {lastGap:0} px away after {MaxSimulatedFrames} frames. A lower sensitivity gets there faster."
                    : wander < 3
                        ? $"stopped about {lastGap:0} px from the target instead of on it."
                        : $"still moving around within {furthest:0} px of the target after {MaxSimulatedFrames} frames.")
                    + " Played back slowed down, on a target that isn't moving.";
            }
        }

        private void AdvanceMovementPreview()
        {
            if (_pathFrames.Count == 0) return;

            _pathTick++;
            int last = _pathFrames.Count - 1;
            int framesPerTick = Math.Max(1, (int)Math.Ceiling(last / (double)PlaybackTicks));
            int playTicks = (int)Math.Ceiling(last / (double)framesPerTick);
            if (_pathTick > playTicks + HoldTicksAtEnd)
                _pathTick = 0;

            DrawMovementPreview(Math.Min(_pathTick * framesPerTick, last));
        }

        private void DrawMovementPreview(int upToFrame)
        {
            if (_pathCanvas == null || _pathChart == null || _pathFrames.Count == 0) return;

            var accent = (Brush)FindResource("ThemeColor");
            var canvas = _pathCanvas;
            canvas.Children.Clear();

            // Grid
            for (double gx = 20; gx < PathViewWidth; gx += 40)
                canvas.Children.Add(new Line { X1 = gx, Y1 = 0, X2 = gx, Y2 = PathViewHeight, Stroke = PathGrid, StrokeThickness = 1 });
            for (double gy = 10; gy < PathViewHeight; gy += 40)
                canvas.Children.Add(new Line { X1 = 0, Y1 = gy, X2 = PathViewWidth, Y2 = gy, Stroke = PathGrid, StrokeThickness = 1 });

            // Target
            AddAt(canvas, new Ellipse { Width = 18, Height = 18, Stroke = PathTargetBrush, StrokeThickness = 1.5 }, PathTarget.X - 9, PathTarget.Y - 9);
            AddAt(canvas, new Ellipse { Width = 5, Height = 5, Fill = PathTargetBrush }, PathTarget.X - 2.5, PathTarget.Y - 2.5);

            // Whole route faintly, then the part travelled so far in the accent colour with a dot per frame
            canvas.Children.Add(new Polyline { Points = new PointCollection(_pathFrames), Stroke = PathGhost, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            canvas.Children.Add(new Polyline { Points = new PointCollection(_pathFrames.Take(upToFrame + 1)), Stroke = accent, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
            for (int i = 1; i <= upToFrame; i++)
                AddAt(canvas, new Ellipse { Width = 4, Height = 4, Fill = accent, Opacity = 0.55 }, _pathFrames[i].X - 2, _pathFrames[i].Y - 2);

            // Start marker and the moving crosshair
            AddAt(canvas, new Ellipse { Width = 6, Height = 6, Fill = PathGhost }, PathStart.X - 3, PathStart.Y - 3);
            var current = _pathFrames[upToFrame];
            var crossBrush = (Brush)FindResource("UI.Text");
            canvas.Children.Add(new Line { X1 = current.X - 8, Y1 = current.Y, X2 = current.X - 3, Y2 = current.Y, Stroke = crossBrush, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = current.X + 3, Y1 = current.Y, X2 = current.X + 8, Y2 = current.Y, Stroke = crossBrush, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = current.X, Y1 = current.Y - 8, X2 = current.X, Y2 = current.Y - 3, Stroke = crossBrush, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = current.X, Y1 = current.Y + 3, X2 = current.X, Y2 = current.Y + 8, Stroke = crossBrush, StrokeThickness = 1.5 });

            DrawDistanceChart(upToFrame, accent);
        }

        private void DrawDistanceChart(int upToFrame, Brush accent)
        {
            var chart = _pathChart!;
            chart.Children.Clear();

            double start = Math.Max(Distance(PathStart, PathTarget), 1);
            int frames = Math.Max(_pathFrames.Count - 1, 1);
            double top = 4, bottom = ChartHeight - 4;

            // Highest value drawn: the starting distance, or more if the path swings further out
            double peak = Math.Max(start, _pathFrames.Max(p => Distance(p, PathTarget)));

            Point Map(int i) => new(
                4 + (PathViewWidth - 8) * i / frames,
                bottom - (bottom - top) * Distance(_pathFrames[i], PathTarget) / peak);

            chart.Children.Add(new Line { X1 = 4, Y1 = bottom, X2 = PathViewWidth - 4, Y2 = bottom, Stroke = PathGhost, StrokeThickness = 1 });

            var all = new PointCollection(Enumerable.Range(0, _pathFrames.Count).Select(Map));
            chart.Children.Add(new Polyline { Points = all, Stroke = PathGhost, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });

            var done = new PointCollection(Enumerable.Range(0, upToFrame + 1).Select(Map));
            chart.Children.Add(new Polyline { Points = done, Stroke = accent, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });

            var head = Map(upToFrame);
            AddAt(chart, new Ellipse { Width = 6, Height = 6, Fill = accent }, head.X - 3, head.Y - 3);

            if (_pathSettledFrame > 0)
            {
                double settledX = 4 + (PathViewWidth - 8) * _pathSettledFrame / frames;
                chart.Children.Add(new Line { X1 = settledX, Y1 = top, X2 = settledX, Y2 = bottom, Stroke = PathGhost, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 } });
            }
        }

        private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }
}
