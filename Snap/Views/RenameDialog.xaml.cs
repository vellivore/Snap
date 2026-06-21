using System.IO;
using System.Windows;

namespace Snap.Views;

public partial class RenameDialog : Window
{
    public string NewName { get; private set; } = string.Empty;

    public RenameDialog(string currentName, bool isFile = true)
    {
        InitializeComponent();
        NameTextBox.Text = currentName;
        NewName = currentName;

        Loaded += (_, _) =>
        {
            NameTextBox.Focus();

            // Select the name without its extension — only for files. Folder and tab names
            // may legitimately contain dots (e.g. "my.folder"), so select the whole name.
            var dotIndex = isFile ? currentName.LastIndexOf('.') : -1;
            if (dotIndex > 0)
            {
                NameTextBox.Select(0, dotIndex);
            }
            else
            {
                NameTextBox.SelectAll();
            }
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("名前を入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Check for invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalidChars) >= 0)
        {
            MessageBox.Show("ファイル名に使用できない文字が含まれています。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NewName = name;
        DialogResult = true;
    }
}
