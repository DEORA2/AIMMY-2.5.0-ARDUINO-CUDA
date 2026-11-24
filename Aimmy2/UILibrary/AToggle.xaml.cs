using Whales2.Theme;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Whales2.UILibrary
{
    /// <summary>
    /// Option row with a pill switch.
    /// </summary>
    public partial class AToggle : System.Windows.Controls.UserControl
    {
        private static readonly Color OffTrackColor = Color.FromRgb(0x2A, 0x34, 0x3A);
        private static readonly Color OffKnobColor = Color.FromRgb(0xC3, 0xCD, 0xD3);
        private static readonly Color OnKnobColor = Color.FromRgb(0x0B, 0x0E, 0x10);
        private const double KnobTravel = 18;
        private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(160));
        private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        private bool _isEnabled = false;

        public bool IsOn => _isEnabled;

        public AToggle(string Text, string? description = null)
        {
            InitializeComponent();
            ToggleTitle.Content = Text;
            RowText.SetDescription(RowDescription, description);

            ThemeManager.ThemeChanged += OnThemeChanged;
            Loaded += (s, e) => RefreshThemeColors();
            Unloaded += (s, e) => ThemeManager.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged(object sender, Color newThemeColor)
        {
            Application.Current.Dispatcher.BeginInvoke(RefreshThemeColors);
        }

        private void RefreshThemeColors()
        {
            SwitchBorder.Background = new SolidColorBrush(_isEnabled ? ThemeManager.ThemeColor : OffTrackColor);
        }

        public void EnableSwitch()
        {
            _isEnabled = true;
            Animate(KnobTravel, ThemeManager.ThemeColor, OnKnobColor);
        }

        public void DisableSwitch()
        {
            _isEnabled = false;
            Animate(0, OffTrackColor, OffKnobColor);
        }

        private void Animate(double knobX, Color track, Color knob)
        {
            KnobOffset.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(knobX, AnimationDuration) { EasingFunction = Ease });
            AnimateBrush(SwitchBorder, track);
            AnimateBrush(SwitchMoving, knob);
        }

        private static void AnimateBrush(System.Windows.Controls.Border border, Color to)
        {
            var from = border.Background is SolidColorBrush b ? b.Color : to;
            var brush = new SolidColorBrush(from);
            border.Background = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, AnimationDuration));
        }
    }

    internal static class RowText
    {
        /// <summary>Shows a one-line explanation under the row label (hidden when empty).</summary>
        public static void SetDescription(System.Windows.Controls.TextBlock target, string? text)
        {
            target.Text = text ?? "";
            target.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
