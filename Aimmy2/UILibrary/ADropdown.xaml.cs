using Whales2.Class;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace UILibrary
{
    /// <summary>
    /// Option row with a drop-down list. The selected item's text is stored under <see cref="DictionaryKey"/>.
    /// </summary>
    public partial class ADropdown : UserControl
    {
        private string main_dictionary_path { get; set; }

        /// <summary>Key in Dictionary.dropdownState that this dropdown reads and writes.</summary>
        public string DictionaryKey => main_dictionary_path;

        public ADropdown(string title, string dictionary_path, string? description = null)
        {
            InitializeComponent();
            DropdownTitle.Content = title;
            main_dictionary_path = dictionary_path;
            Whales2.UILibrary.RowText.SetDescription(RowDescription, description);
        }

        private void DropdownBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedItemContent = ((ComboBoxItem)DropdownBox.SelectedItem)?.Content?.ToString();
            if (selectedItemContent != null)
            {
                Dictionary.dropdownState[main_dictionary_path] = selectedItemContent;
            }
        }
    }
}
