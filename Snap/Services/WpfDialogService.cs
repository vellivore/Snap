using System.Windows;
using Snap.Views;

namespace Snap.Services;

/// <summary><see cref="IDialogService"/> with a MessageBox and <see cref="RenameDialog"/>,
/// owned by the active Snap window.</summary>
public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string message, string title)
    {
        var owner = Owner();
        var result = owner != null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    public string? AskName(string currentName, bool isFile)
    {
        var dialog = new RenameDialog(currentName, isFile) { Owner = Owner() };
        return dialog.ShowDialog() == true ? dialog.NewName : null;
    }

    private static Window? Owner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;
}
