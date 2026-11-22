using Whales2.Theme;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Whales2.UILibrary
{
    /// <summary>
    /// Interaction logic for AToggle.xaml
    /// </summary>
    public partial class AToggle : System.Windows.Controls.UserControl
    {
        private static readonly Color OffBorderColor = Color.FromArgb(0x99, 0xA0, 0x94, 0x98);
        private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(160));
        private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        private bool _isEnabled = false;

        public bool IsOn => _isEnabled;

        public AToggle(string Text, string? tooltip = null)
        {
            InitializeComponent();
            ToggleTitle.Content = Text;

            if (!string.IsNullOrEmpty(tooltip))
            {
                var tt = new System.Windows.Controls.ToolTip { Content = tooltip };
                if (TryFindResource("Tooltip") is Style style)
                    tt.Style = style;
                ToolTip = tt;
            }

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
            SwitchMoving.Background = new SolidColorBrush(ThemeManager.ThemeColor);
            AnimateBorder(_isEnabled ? ThemeManager.ThemeColor : OffBorderColor);
        }

        public void EnableSwitch()
        {
            _isEnabled = true;
            AnimateCheck(1);
            AnimateBorder(ThemeManager.ThemeColor);
        }

        public void DisableSwitch()
        {
            _isEnabled = false;
            AnimateCheck(0);
            AnimateBorder(OffBorderColor);
        }

        private void AnimateCheck(double to)
        {
            var animation = new DoubleAnimation(to, AnimationDuration) { EasingFunction = Ease };
            CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        private void AnimateBorder(Color to)
        {
            var from = SwitchBorder.BorderBrush is SolidColorBrush b ? b.Color : OffBorderColor;
            var brush = new SolidColorBrush(from);
            SwitchBorder.BorderBrush = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, AnimationDuration));
        }
    }
}
