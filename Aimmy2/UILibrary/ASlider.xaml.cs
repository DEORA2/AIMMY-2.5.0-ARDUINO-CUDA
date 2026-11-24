using System.Windows.Controls;

namespace Whales2.UILibrary
{
    /// <summary>
    /// Option row with a slider, its current value and -/+ step buttons.
    /// </summary>
    public partial class ASlider : UserControl
    {
        private readonly string _unit;

        /// <summary>Unit shown after the value (e.g. "px").</summary>
        public string Unit => _unit;

        /// <summary>Amount the -/+ buttons change the value by.</summary>
        public double Step { get; }

        public ASlider(string Text, string NotifierText, double ButtonSteps, string? description = null)
        {
            InitializeComponent();

            SliderTitle.Content = Text;
            _unit = NotifierText;
            Step = ButtonSteps;
            RowText.SetDescription(RowDescription, description);

            Slider.ValueChanged += (s, e) => RefreshValueText();

            SubtractOne.Click += (s, e) => UpdateSliderValue(-ButtonSteps);
            AddOne.Click += (s, e) => UpdateSliderValue(ButtonSteps);
        }

        public void RefreshValueText()
        {
            AdjustNotifier.Content = $"{Slider.Value:0.##} {_unit}".TrimEnd();
        }

        private void UpdateSliderValue(double change)
        {
            Slider.Value = Math.Round(Slider.Value + change, 2);
        }
    }
}
