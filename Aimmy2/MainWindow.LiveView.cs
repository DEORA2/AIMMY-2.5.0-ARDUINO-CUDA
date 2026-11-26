using Whales2.AILogic;
using Whales2.Class;
using Class;
using Other;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Whales2
{
    /// <summary>
    /// Home page live view: the square the model looks at, with its detections drawn on top. While it is on screen
    /// the AI loop keeps running the model (detection only) so there is something to show even when you're not aiming.
    /// </summary>
    public partial class MainWindow
    {
        private Grid? _liveSurface;
        private Image? _liveImage;
        private Canvas? _liveOverlay;
        private TextBlock? _livePlaceholder, _liveStatus;
        private Button? _livePauseButton;
        private WriteableBitmap? _liveBitmap;
        private byte[] _livePixels = Array.Empty<byte>();
        private long _liveFrameId = -1;
        private int _liveTargets;
        private bool _livePaused;
        private DispatcherTimer? _liveTimer;

        private FrameworkElement BuildLiveView()
        {
            var root = new StackPanel();

            _liveImage = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_liveImage, BitmapScalingMode.LowQuality);
            _liveOverlay = new Canvas { IsHitTestVisible = false };
            _livePlaceholder = new TextBlock
            {
                FontFamily = Res<FontFamily>("UIFont"),
                FontSize = 26,
                Foreground = Res<Brush>("UI.TextDim"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(60),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _liveSurface = new Grid { Width = 640, Height = 640, Background = Res<Brush>("UI.KeyBg"), ClipToBounds = true };
            _liveSurface.Children.Add(_liveImage);
            _liveSurface.Children.Add(_liveOverlay);
            _liveSurface.Children.Add(_livePlaceholder);

            root.Children.Add(new Viewbox
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 360,
                Margin = new Thickness(14, 4, 14, 6),
                Child = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = Res<Brush>("UI.Box"),
                    BorderThickness = new Thickness(2),
                    ClipToBounds = true,
                    Child = _liveSurface
                }
            });

            var statusRow = new Grid { Margin = new Thickness(14, 2, 14, 2) };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition());
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _liveStatus = MakeSmall("", 12.5);
            _liveStatus.VerticalAlignment = VerticalAlignment.Center;
            statusRow.Children.Add(_liveStatus);
            _livePauseButton = MakeButton("Pause", () =>
            {
                _livePaused = !_livePaused;
                _livePauseButton!.Content = _livePaused ? "Resume" : "Pause";
                LiveTick();
            });
            _livePauseButton.Height = 28;
            Grid.SetColumn(_livePauseButton, 1);
            statusRow.Children.Add(_livePauseButton);
            root.Children.Add(statusRow);

            var note = MakeSmall("If this window covers the middle of your screen, the model sees the preview too.", 11.5);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(14, 2, 14, 10);
            root.Children.Add(note);

            // ~15 redraws a second: the view is for looking at, and every redraw of this window costs CPU
            _liveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Render, (_, _) => LiveTick(), Dispatcher);
            return root;
        }

        /// <summary>Starts or stops the live view as Home is shown or left.</summary>
        private void SetLiveViewVisible(bool visible)
        {
            if (_liveTimer == null) return;
            if (visible)
            {
                _liveTimer.Start();
                LiveTick();
            }
            else
            {
                _liveTimer.Stop();
                LivePreview.Active = false;
            }
        }

        private void LiveTick()
        {
            if (_liveSurface == null || _liveImage == null || _liveOverlay == null || _livePlaceholder == null || _liveStatus == null) return;

            var ai = FileManager.AIManager;
            bool modelRunning = HasModel && ai?.IsRunning == true;
            bool watching = _currentPage == "Home" && IsVisible && WindowState != WindowState.Minimized;
            // Only while Whales is the focused window: when you're in a game, nobody is looking at it
            LivePreview.Active = watching && IsActive && modelRunning && !_livePaused;

            string? reason = !HasModel ? "Load a model to see what it detects"
                : !modelRunning ? "Starting the model…"
                : _livePaused ? "Paused"
                : !IsActive ? "Paused while Whales isn't the focused window"
                : null;

            if (reason != null)
            {
                if (!HasModel)
                {
                    _liveImage.Source = null;
                    _liveOverlay.Children.Clear();
                    _liveFrameId = -1;
                    LivePreview.Clear();
                }
                _livePlaceholder.Text = reason;
                // Keep showing the last picture when merely paused; the status line says why it isn't moving
                _livePlaceholder.Visibility = HasModel && _liveImage.Source != null ? Visibility.Collapsed : Visibility.Visible;
                _liveStatus.Text = _livePaused ? "Paused. The model isn't running for the preview." : reason;
                return;
            }

            if (LivePreview.TryCopy(ref _liveFrameId, ref _livePixels, out int size, out var boxes, out float fov))
            {
                if (_liveBitmap == null || _liveBitmap.PixelWidth != size)
                {
                    _liveBitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgr32, null);
                    _liveImage.Source = _liveBitmap;
                    _liveSurface.Width = _liveSurface.Height = size;
                }
                _liveBitmap.WritePixels(new Int32Rect(0, 0, size, size), _livePixels, size * 4, 0);
                DrawLiveOverlay(size, boxes, fov);
                _liveTargets = boxes.Count(b => b.Confidence > 0);
            }

            bool hasFrame = _liveImage.Source != null && size > 0;
            _livePlaceholder.Text = "Waiting for the screen to change…";
            _livePlaceholder.Visibility = hasFrame ? Visibility.Collapsed : Visibility.Visible;

            double fps = ai?.CurrentFps ?? 0;
            _liveStatus.Text = hasFrame
                ? $"{_liveTargets} target{(_liveTargets == 1 ? "" : "s")} in view  ·  {Math.Round(fps)} fps"
                : "Waiting for the screen to change…";
        }

        private void DrawLiveOverlay(int size, IReadOnlyList<LivePreview.Box> boxes, float fov)
        {
            var overlay = _liveOverlay!;
            overlay.Children.Clear();
            overlay.Width = overlay.Height = size;

            // The FOV: detections outside it are ignored
            if (fov > 0 && fov < size)
            {
                Shape area = Dictionary.dropdownState["FOV Style"]?.ToString() == "Rectangle"
                    ? new Rectangle()
                    : new Ellipse();
                area.Width = area.Height = fov;
                area.Stroke = Res<Brush>("UI.TextDim");
                area.StrokeThickness = 2;
                area.StrokeDashArray = new DoubleCollection { 4, 4 };
                Canvas.SetLeft(area, (size - fov) / 2);
                Canvas.SetTop(area, (size - fov) / 2);
                overlay.Children.Add(area);
            }

            var theme = (Brush)FindResource("ThemeColor");
            var other = new SolidColorBrush(Color.FromArgb(230, 235, 240, 245));
            foreach (var box in boxes)
            {
                Brush colour = box.IsTarget ? theme : other;
                var rect = new Rectangle
                {
                    Width = Math.Max(1, box.Rect.Width),
                    Height = Math.Max(1, box.Rect.Height),
                    Stroke = colour,
                    StrokeThickness = box.IsTarget ? 3 : 2,
                    StrokeDashArray = box.Confidence <= 0 ? new DoubleCollection { 3, 3 } : null
                };
                Canvas.SetLeft(rect, box.Rect.X);
                Canvas.SetTop(rect, box.Rect.Y);
                overlay.Children.Add(rect);

                string text = box.Confidence > 0 ? $"{box.Label} {box.Confidence * 100:0}%" : "held";
                var label = new Border
                {
                    Background = colour,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 1, 5, 1),
                    Child = new TextBlock
                    {
                        Text = text,
                        FontFamily = Res<FontFamily>("UIFont"),
                        FontSize = 15,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Res<Brush>("UI.Bg")
                    }
                };
                Canvas.SetLeft(label, Math.Max(0, box.Rect.X));
                Canvas.SetTop(label, Math.Max(0, box.Rect.Y - 24));
                overlay.Children.Add(label);
            }
        }
    }
}
