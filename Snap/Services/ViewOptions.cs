namespace Snap.Services;

/// <summary>
/// View settings shared by every pane (#17): hidden files on / off and the file list's column
/// widths. Loaded from settings.json by MainWindow, captured back into it by CaptureSettings.
/// UI thread only.
/// </summary>
public static class ViewOptions
{
    /// <summary>Show entries with the Hidden attribute in the lists and the tree (Ctrl+H).</summary>
    public static bool ShowHidden { get; private set; }

    /// <summary>Raised after <see cref="ShowHidden"/> changed.</summary>
    public static event Action? ShowHiddenChanged;

    private static readonly Dictionary<string, double> _columnWidths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Column widths by column key (Name / LastModified / Size / Type).</summary>
    public static IReadOnlyDictionary<string, double> ColumnWidths => _columnWidths;

    /// <summary>Raised once the saved widths are loaded: every file list applies them.</summary>
    public static event Action? ColumnWidthsLoaded;

    /// <summary>Takes the saved state (startup). Does not raise <see cref="ShowHiddenChanged"/>:
    /// nothing is listed yet.</summary>
    public static void Load(bool showHidden, IReadOnlyDictionary<string, double>? columnWidths)
    {
        ShowHidden = showHidden;
        _columnWidths.Clear();
        if (columnWidths != null)
            foreach (var (key, width) in columnWidths)
                if (width > 0 && !double.IsNaN(width) && !double.IsInfinity(width))
                    _columnWidths[key] = width;
        ColumnWidthsLoaded?.Invoke();
    }

    public static void SetShowHidden(bool value)
    {
        if (ShowHidden == value) return;
        ShowHidden = value;
        SettingsStore.MarkDirty();
        ShowHiddenChanged?.Invoke();
    }

    /// <summary>A column of some pane was resized: that width becomes the saved one.</summary>
    public static void SetColumnWidth(string key, double width)
    {
        if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width)) return;
        if (_columnWidths.TryGetValue(key, out var old) && Math.Abs(old - width) < 0.5) return;
        _columnWidths[key] = width;
        SettingsStore.MarkDirty();
    }

    /// <summary>The widths to save.</summary>
    public static Dictionary<string, double> CaptureColumnWidths() => new(_columnWidths);
}
