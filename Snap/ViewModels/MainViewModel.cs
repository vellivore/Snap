using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Snap.Models;
using Snap.Services;

namespace Snap.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public FolderTreeViewModel FolderTree { get; } = new();
    public TabPaneViewModel TopLeftPane { get; } = new();
    public TabPaneViewModel TopRightPane { get; } = new();
    public TabPaneViewModel BottomLeftPane { get; } = new();
    public TabPaneViewModel BottomRightPane { get; } = new();

    public UsageTracker UsageTracker { get; } = new();
    public CommandPaletteViewModel CommandPalette { get; } = new();
    public FloatingTerminalViewModel Terminal { get; } = new();
    public SidebarViewModel Sidebar { get; } = new();

    [ObservableProperty]
    private TabPaneViewModel? _activePane;

    private FilePaneViewModel? _trackedTab;

    // Delay timer: only add to Today after staying 2s in the same folder
    private readonly DispatcherTimer _todayTimer;
    private string? _pendingTodayPath;

    public MainViewModel()
    {
        _todayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _todayTimer.Tick += (s, e) =>
        {
            _todayTimer.Stop();
            if (_pendingTodayPath != null)
                Sidebar.AddToday(_pendingTodayPath);
            _pendingTodayPath = null;
        };
    }

    public async Task InitializeAsync()
    {
        await InitializeAsync(new AppSettings());
    }

    public async Task InitializeAsync(AppSettings settings)
    {
        UsageTracker.Load();
        ActivePane = TopLeftPane;

        // Pass usage tracker to all panes
        TopLeftPane.SetUsageTracker(UsageTracker);
        TopRightPane.SetUsageTracker(UsageTracker);
        BottomLeftPane.SetUsageTracker(UsageTracker);
        BottomRightPane.SetUsageTracker(UsageTracker);

        // Sidebar: share bookmarks and load persisted data
        Sidebar.FolderTree = FolderTree;
        Sidebar.PinnedItems = FolderTree.Bookmarks;
        Sidebar.LoadToday(settings.TodayFolders);
        Sidebar.NavigateRequested += OnSidebarNavigate;

        // Wire tab closed events → add to Today
        foreach (var pane in AllPanes)
            pane.TabClosed += path => Sidebar.AddToday(path);

        var panes = settings.Panes;
        await Task.WhenAll(
            FolderTree.InitializeAsync(),
            TopLeftPane.InitializeAsync(panes.TopLeft.Tabs, panes.TopLeft.ActiveTabIndex),
            TopRightPane.InitializeAsync(panes.TopRight.Tabs, panes.TopRight.ActiveTabIndex),
            BottomLeftPane.InitializeAsync(panes.BottomLeft.Tabs, panes.BottomLeft.ActiveTabIndex),
            BottomRightPane.InitializeAsync(panes.BottomRight.Tabs, panes.BottomRight.ActiveTabIndex)
        );

        FolderTree.FolderSelected += OnTreeFolderSelected;
        TrackActivePane();
    }

    private TabPaneViewModel[] AllPanes => [TopLeftPane, TopRightPane, BottomLeftPane, BottomRightPane];

    /// <summary>Collects current pane state for persistence.</summary>
    public PanesSettings GetPanesState()
    {
        static PaneSettings Capture(TabPaneViewModel pane)
        {
            var (paths, index) = pane.GetTabState();
            return new PaneSettings { Tabs = paths, ActiveTabIndex = index };
        }

        return new PanesSettings
        {
            TopLeft = Capture(TopLeftPane),
            TopRight = Capture(TopRightPane),
            BottomLeft = Capture(BottomLeftPane),
            BottomRight = Capture(BottomRightPane),
        };
    }

    partial void OnActivePaneChanged(TabPaneViewModel? value)
    {
        TrackActivePane();
    }

    private void TrackActivePane()
    {
        if (_trackedTab != null)
            _trackedTab.PropertyChanged -= OnTrackedTabPropertyChanged;

        if (ActivePane != null)
        {
            ActivePane.PropertyChanged -= OnActivePanePropertyChanged;
            ActivePane.PropertyChanged += OnActivePanePropertyChanged;
            WatchSelectedTab();
        }
    }

    private void OnActivePanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabPaneViewModel.SelectedTab))
            WatchSelectedTab();
    }

    private void WatchSelectedTab()
    {
        if (_trackedTab != null)
            _trackedTab.PropertyChanged -= OnTrackedTabPropertyChanged;

        _trackedTab = ActivePane?.SelectedTab;

        if (_trackedTab != null)
        {
            _trackedTab.PropertyChanged += OnTrackedTabPropertyChanged;
            _ = FolderTree.SyncToPathAsync(_trackedTab.CurrentPath);
        }
    }

    private async void OnTrackedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePaneViewModel.CurrentPath) && sender is FilePaneViewModel tab)
        {
            await FolderTree.SyncToPathAsync(tab.CurrentPath);
            // Start 2s timer — only add to Today if user stays in this folder
            _todayTimer.Stop();
            _pendingTodayPath = tab.CurrentPath;
            _todayTimer.Start();
        }
    }

    private async void OnTreeFolderSelected(string path)
    {
        var pane = ActivePane ?? TopLeftPane;
        if (pane.SelectedTab != null)
        {
            await pane.SelectedTab.NavigateToAsync(path);
        }
    }

    private async void OnSidebarNavigate(string path)
    {
        var pane = ActivePane ?? TopLeftPane;
        if (pane.SelectedTab != null)
        {
            await pane.SelectedTab.NavigateToAsync(path);
        }
    }

}
