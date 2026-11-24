namespace Whales2.UILibrary
{
    /// <summary>
    /// Option row with a colour swatch; clicking it opens a colour picker.
    /// </summary>
    public partial class AColorChanger : System.Windows.Controls.UserControl
    {
        public AColorChanger(string title, string? description = null)
        {
            InitializeComponent();
            ColorChangerTitle.Content = title;
            RowText.SetDescription(RowDescription, description);
        }
    }
}
