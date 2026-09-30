namespace Snap.Models;

/// <summary>
/// An app command shown in the command palette ("/" mode). The list is built in one place,
/// <see cref="Snap.ViewModels.MainViewModel"/>; adding a command means adding one entry there (#13).
/// </summary>
/// <param name="Name">Lower-case key matched against the typed filter (e.g. "new tab").</param>
/// <param name="Label">Text shown in the palette.</param>
/// <param name="Icon">Segoe MDL2 Assets glyph.</param>
/// <param name="Execute">What the command does.</param>
public sealed record PaletteCommand(string Name, string Label, string Icon, Func<Task> Execute);
