namespace Whales2.UILibrary
{
    /// <summary>
    /// Option row that runs an action when clicked.
    /// </summary>
    public partial class APButton : System.Windows.Controls.UserControl
    {
        public APButton(string Text, string? description = null)
        {
            InitializeComponent();
            ButtonTitle.Content = Text;
            RowText.SetDescription(RowDescription, description);
        }
    }
}
