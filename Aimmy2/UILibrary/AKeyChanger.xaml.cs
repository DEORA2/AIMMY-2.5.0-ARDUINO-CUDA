using Other;

namespace Whales2.UILibrary
{
    /// <summary>
    /// Option row showing a bound key; clicking it starts a rebind.
    /// </summary>
    public partial class AKeyChanger : System.Windows.Controls.UserControl
    {
        public AKeyChanger(string Text, string Keybind, string? description = null)
        {
            InitializeComponent();
            KeyChangerTitle.Content = Text;
            RowText.SetDescription(RowDescription, description);
            KeyNotifier.Content = KeybindNameManager.ConvertToRegularKey(Keybind);
        }
    }
}
