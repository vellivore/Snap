namespace Snap.Models;

public class AppSettings
{
    public WindowSettings Window { get; set; } = new();
    public double TreeWidth { get; set; } = 220;
    public double[] HorizontalSplit { get; set; } = [1, 1];
    public double[] VerticalSplit { get; set; } = [1, 1];
    public PanesSettings Panes { get; set; } = new();
    public List<string> Bookmarks { get; set; } = new();
    public List<string> TodayFolders { get; set; } = new();
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
    public List<string> Tabs { get; set; } = [@"C:\"];
    public int ActiveTabIndex { get; set; }
}
