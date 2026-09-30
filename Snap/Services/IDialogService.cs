namespace Snap.Services;

/// <summary>
/// Dialogs the view models ask for (#15), so they never create windows or message boxes
/// themselves. The WPF implementation is <see cref="WpfDialogService"/>.
/// </summary>
public interface IDialogService
{
    /// <summary>A Yes/No warning. True only for Yes.</summary>
    bool Confirm(string message, string title);

    /// <summary>
    /// Asks for a new name, starting from <paramref name="currentName"/>. For a file only the part
    /// before the extension is selected. Null when cancelled.
    /// </summary>
    string? AskName(string currentName, bool isFile);
}
