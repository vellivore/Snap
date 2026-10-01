namespace Snap.Models;

public class AppSettings
{
    public WindowSettings Window { get; set; } = new();
    public double TreeWidth { get; set; } = 220;
    public double[] HorizontalSplit { get; set; } = [1, 1];
    public double[] VerticalSplit { get; set; } = [1, 1];
    public PanesSettings Panes { get; set; } = new();
    /// <summary>Pinned folders. Name is the display name (renamable in the sidebar).</summary>
    public List<PathEntry> Bookmarks { get; set; } = new();
    public List<string> TodayFolders { get; set; } = new();
    /// <summary>Show hidden files and folders in the lists and the tree (Ctrl+H, #17). Off by default.</summary>
    public bool ShowHidden { get; set; }
    /// <summary>File list column widths by column key (Name / LastModified / Size / Type), one set for all panes (#17).</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();
    /// <summary>Floating terminal size and behavior (#18).</summary>
    public TerminalSettings Terminal { get; set; } = new();
}

public class TerminalSettings
{
    /// <summary>Frame size, resized by dragging its right / bottom edge. Minimum 400 x 200.</summary>
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 450;
    /// <summary>On opening, cd the shell to the active pane's folder (when it moved since). Off by default.</summary>
    public bool FollowActivePane { get; set; }
}

public class WindowSettings
{
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 800;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool IsMaximized { get; set; }
}

public class PanesSettings
{
    public PaneSettings TopLeft { get; set; } = new();
    public PaneSettings TopRight { get; set; } = new();
    public PaneSettings BottomLeft { get; set; } = new();
    public PaneSettings BottomRight { get; set; } = new();
}

public class PaneSettings
{
    /// <summary>Open tabs. Name is set only for a user-renamed tab.</summary>
    public List<PathEntry> Tabs { get; set; } = [new PathEntry(@"C:\")];
    public int ActiveTabIndex { get; set; }
}
