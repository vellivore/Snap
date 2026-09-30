using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snap.Helpers;
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

    /// <summary>The four panes in a fixed order (top-left, top-right, bottom-left, bottom-right).</summary>
    public IReadOnlyList<TabPaneViewModel> AllPanes { get; }

    /// <summary>The pane that commands, the tree and the sidebar act on. The only source of truth:
    /// views set it (mouse down / keyboard focus) and never track it themselves.</summary>
    [ObservableProperty]
    private TabPaneViewModel? _activePane;

    /// <summary>The selected tab of <see cref="ActivePane"/> (follows tab switches too).</summary>
    [ObservableProperty]
    private FilePaneViewModel? _activeTab;

    /// <summary>Status bar text: the active tab's status, or a user-facing error.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    // True after InitializeAsync: from then on tab changes sync the tree and feed Today.
    private bool _initialized;

    // Delay timer: only add to Today after staying 2s in the same folder
    private readonly DispatcherTimer _todayTimer;
    private string? _pendingTodayPath;

    /// <summary>Confirmations and the rename dialog, handed to every tab (#15).</summary>
    public IDialogService Dialogs { get; }

    /// <summary>Pane names used by the palette (/copy to 左上 ...), in <see cref="AllPanes"/> order.</summary>
    private static readonly string[] PaneNames = ["左上", "右上", "左下", "右下"];

    public MainViewModel(IDialogService dialogs)
    {
        Dialogs = dialogs;
        AllPanes = [TopLeftPane, TopRightPane, BottomLeftPane, BottomRightPane];

        // Every tab that joins a pane (startup, new tab, moved tab) gets the dialogs and reports
        // the folders its file operations changed, so all panes showing them refresh (#15).
        foreach (var pane in AllPanes)
        {
            foreach (var tab in pane.Tabs) AttachTab(tab);
            pane.Tabs.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (FilePaneViewModel tab in e.NewItems) AttachTab(tab);
            };
        }

        CommandPalette.NavigateAction = NavigateActiveTabAsync;
        CommandPalette.Commands = BuildPaletteCommands();

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
        _initialized = true;
        if (ActiveTab != null)
            FolderTree.SyncToPathAsync(ActiveTab.CurrentPath).SafeFireAndForget("Main.TreeSync", "ツリーを同期できません");
        WatchPersistedState();
    }

    private void AttachTab(FilePaneViewModel tab)
    {
        tab.Dialogs = Dialogs;
        tab.FoldersChanged = folders => RefreshPanesShowing(folders);
    }

    // ==================== Settings persistence (#12) ====================

    /// <summary>
    /// Calls <see cref="SettingsStore.MarkDirty"/> whenever something that goes into
    /// settings.json changes: tabs (open/close/move/select, folder, custom name),
    /// bookmarks (add/remove/reorder/rename) and the Today list.
    /// </summary>
    private void WatchPersistedState()
    {
        foreach (var pane in AllPanes)
        {
            pane.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TabPaneViewModel.SelectedTab)) SettingsStore.MarkDirty();
            };
            WatchCollection(pane.Tabs,
                nameof(FilePaneViewModel.CurrentPath), nameof(FilePaneViewModel.TabHeader));
        }
        WatchCollection(FolderTree.Bookmarks, nameof(BookmarkItem.Name), nameof(BookmarkItem.FullPath));
        WatchCollection(Sidebar.TodayItems);
    }

    /// <summary>Marks settings dirty on any change to <paramref name="items"/> and, for the
    /// listed property names, on property changes of the items they contain.</summary>
    private static void WatchCollection<T>(ObservableCollection<T> items, params string[] itemProperties)
        where T : INotifyPropertyChanged
    {
        void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (itemProperties.Contains(e.PropertyName)) SettingsStore.MarkDirty();
        }

        if (itemProperties.Length > 0)
            foreach (var item in items) item.PropertyChanged += OnItemChanged;

        items.CollectionChanged += (_, e) =>
        {
            if (itemProperties.Length > 0)
            {
                if (e.OldItems != null)
                    foreach (T item in e.OldItems) item.PropertyChanged -= OnItemChanged;
                if (e.NewItems != null)
                    foreach (T item in e.NewItems) item.PropertyChanged += OnItemChanged;
            }
            SettingsStore.MarkDirty();
        };
    }

    /// <summary>Collects current pane state for persistence.</summary>
    public PanesSettings GetPanesState()
    {
        static PaneSettings Capture(TabPaneViewModel pane)
        {
            var (tabs, index) = pane.GetTabState();
            return new PaneSettings { Tabs = tabs, ActiveTabIndex = index };
        }

        return new PanesSettings
        {
            TopLeft = Capture(TopLeftPane),
            TopRight = Capture(TopRightPane),
            BottomLeft = Capture(BottomLeftPane),
            BottomRight = Capture(BottomRightPane),
        };
    }

    // ==================== Active pane / tab (#13) ====================

    partial void OnActivePaneChanged(TabPaneViewModel? oldValue, TabPaneViewModel? newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= OnActivePanePropertyChanged;
            oldValue.IsActive = false;
        }
        if (newValue != null)
        {
            newValue.PropertyChanged += OnActivePanePropertyChanged;
            newValue.IsActive = true;
        }
        ActiveTab = newValue?.SelectedTab;
    }

    private void OnActivePanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabPaneViewModel.SelectedTab))
            ActiveTab = ActivePane?.SelectedTab;
    }

    partial void OnActiveTabChanged(FilePaneViewModel? oldValue, FilePaneViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.PropertyChanged -= OnActiveTabPropertyChanged;

        if (newValue == null) return;
        newValue.PropertyChanged += OnActiveTabPropertyChanged;
        StatusText = newValue.StatusMessage;
        if (_initialized)
            FolderTree.SyncToPathAsync(newValue.CurrentPath).SafeFireAndForget("Main.TreeSync", "ツリーを同期できません");
    }

    private async void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // async void event handler: an unhandled exception here would crash the app,
        // so swallow at the top level (SyncToPathAsync already logs/handles internally).
        try
        {
            if (sender is not FilePaneViewModel tab) return;
            if (e.PropertyName == nameof(FilePaneViewModel.StatusMessage))
            {
                StatusText = tab.StatusMessage;
            }
            else if (e.PropertyName == nameof(FilePaneViewModel.CurrentPath) && _initialized)
            {
                await FolderTree.SyncToPathAsync(tab.CurrentPath);
                // Start 2s timer — only add to Today if user stays in this folder
                _todayTimer.Stop();
                _pendingTodayPath = tab.CurrentPath;
                _todayTimer.Start();
            }
        }
        catch (Exception ex) { Log.Warn("Main.TrackedTabChanged", "tree sync / today timer failed", ex); }
    }

    /// <summary>Shows a message in the status bar (until the active tab reports a new status).</summary>
    public void ShowStatus(string message) => StatusText = message;

    /// <summary>The pane commands act on: the active one, or top-left before any is chosen.</summary>
    private TabPaneViewModel CurrentPane => ActivePane ?? TopLeftPane;

    /// <summary>The pane whose tab list contains <paramref name="tab"/>, or null.</summary>
    public TabPaneViewModel? FindPaneOf(FilePaneViewModel tab) =>
        AllPanes.FirstOrDefault(p => p.Tabs.Contains(tab));

    /// <summary>
    /// Moves <paramref name="tab"/> from its pane to <paramref name="target"/> and selects it there.
    /// Refused (false) when the tab is already in the target or is the source pane's last tab.
    /// </summary>
    public bool MoveTab(FilePaneViewModel tab, TabPaneViewModel target)
    {
        if (target.Tabs.Contains(tab)) return false;

        var source = FindPaneOf(tab);
        if (source == null) return false;

        // The source pane's last tab is not moved (the pane would be left empty).
        if (source.Tabs.Count <= 1) return false;

        // Removing from an ObservableCollection does not auto-null SelectedTab, so if the
        // moved tab was selected we must repoint it — otherwise the source pane keeps a
        // SelectedTab that is no longer in its Tabs list.
        var wasSelected = source.SelectedTab == tab;
        source.Tabs.Remove(tab);
        if ((wasSelected || source.SelectedTab == null) && source.Tabs.Count > 0)
            source.SelectedTab = source.Tabs[0];

        target.Tabs.Add(tab);
        target.SelectedTab = tab;
        return true;
    }

    /// <summary>Refreshes every pane whose selected tab shows one of <paramref name="folders"/>
    /// (except <paramref name="except"/>), e.g. the source folders after a move.</summary>
    public async Task RefreshPanesShowing(IEnumerable<string> folders, FilePaneViewModel? except = null)
    {
        var list = folders.Where(f => !string.IsNullOrEmpty(f)).ToList();
        if (list.Count == 0) return;

        foreach (var pane in AllPanes)
        {
            var tab = pane.SelectedTab;
            if (tab == null || tab == except) continue;
            if (list.Any(f => FileSystemService.SamePath(f, tab.CurrentPath)))
                await tab.Refresh();
        }
    }

    /// <inheritdoc cref="RefreshPanesShowing(IEnumerable{string}, FilePaneViewModel?)"/>
    public Task RefreshPanesShowing(string folder, FilePaneViewModel? except = null) =>
        RefreshPanesShowing([folder], except);

    // ==================== Keyboard commands (#14) ====================
    // Bound from MainWindow.InputBindings. Work that needs a view (focus, the terminal's
    // HwndHost) is asked for through the *Requested events and done by MainWindow.

    /// <summary>The view should move keyboard focus into this pane's file list.</summary>
    public event Action<TabPaneViewModel>? PaneFocusRequested;

    /// <summary>Ctrl+1..4: makes pane 1..4 (TL, TR, BL, BR) active and focuses its list.</summary>
    [RelayCommand]
    private void ActivatePane(string? number)
    {
        if (!int.TryParse(number, out var n) || n < 1 || n > AllPanes.Count) return;
        var pane = AllPanes[n - 1];
        ActivePane = pane;
        PaneFocusRequested?.Invoke(pane);
    }

    /// <summary>Ctrl+N: a new tab in the active pane, opened at the current folder.</summary>
    [RelayCommand]
    private Task NewTab() => CurrentPane.AddTab(null);

    /// <summary>Ctrl+W: closes the active tab (a pane's last tab stays).</summary>
    [RelayCommand]
    private void CloseActiveTab()
    {
        var pane = CurrentPane;
        if (pane.SelectedTab != null)
            pane.CloseTab(pane.SelectedTab);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: next / previous tab of the active pane.</summary>
    [RelayCommand]
    private void NextTab() => CurrentPane.SelectRelativeTab(1);

    [RelayCommand]
    private void PreviousTab() => CurrentPane.SelectRelativeTab(-1);

    /// <summary>Alt+Left / Alt+Right / Alt+Up on the active tab.</summary>
    [RelayCommand]
    private Task GoBack() => ActiveTab?.GoBack() ?? Task.CompletedTask;

    [RelayCommand]
    private Task GoForward() => ActiveTab?.GoForward() ?? Task.CompletedTask;

    [RelayCommand]
    private Task GoUp() => ActiveTab?.GoUpAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Opens <paramref name="path"/> in a new tab of the pane holding <paramref name="from"/>,
    /// right after it (folder middle-click / Ctrl+double-click, #14).
    /// </summary>
    public Task OpenInNewTab(FilePaneViewModel from, string path, bool select)
    {
        var pane = FindPaneOf(from) ?? CurrentPane;
        return pane.OpenTabAsync(path, select, insertAfter: from);
    }

    // ==================== Command palette (#13) ====================

    /// <summary>The palette's app commands. Adding a command = adding one entry here.</summary>
    private IReadOnlyList<PaletteCommand> BuildPaletteCommands() =>
    [
        .. BuildBaseCommands(),
        // /copy to <pane>, /move to <pane> (#15): the active tab's selection into another pane's folder.
        .. PaneNames.Select((name, i) => new PaletteCommand(
            $"copy to {name}", $"Copy to {name}", "\uE8C8", () => SendSelectionToAsync(AllPanes[i], copy: true))),
        .. PaneNames.Select((name, i) => new PaletteCommand(
            $"move to {name}", $"Move to {name}", "\uE8DE", () => SendSelectionToAsync(AllPanes[i], copy: false))),
    ];

    /// <summary>
    /// Copies / moves the active tab's selected items into the folder shown by
    /// <paramref name="target"/>'s selected tab (#15).
    /// </summary>
    private async Task SendSelectionToAsync(TabPaneViewModel target, bool copy)
    {
        var source = ActiveTab;
        if (source == null) return;

        if (target == CurrentPane)
        {
            source.StatusMessage = "送り先に同じペインは選べません";
            return;
        }
        var targetDir = target.SelectedTab?.CurrentPath;
        if (string.IsNullOrEmpty(targetDir))
            return;

        var paths = source.SelectedItems.Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
        {
            source.StatusMessage = "項目が選択されていません";
            return;
        }
        await source.DropFilesAsync(paths, targetDir, copy);
    }

    private IReadOnlyList<PaletteCommand> BuildBaseCommands() =>
    [
        new("new tab", "New Tab", "\uE710", () => CurrentPane.AddTab(null)),
        new("close tab", "Close Tab", "\uE711", () =>
        {
            var pane = CurrentPane;
            if (pane.SelectedTab != null)
                pane.CloseTab(pane.SelectedTab);
            return Task.CompletedTask;
        }),
        new("refresh", "Refresh", "\uE72C", () => CurrentPane.SelectedTab?.Refresh() ?? Task.CompletedTask),
        new("settings", "Open settings.json", "\uE713", () =>
        {
            OpenSettingsFile();
            return Task.CompletedTask;
        }),
        new("terminal", "Open Terminal Here", "\uE756", () =>
        {
            OpenExternalTerminal();
            return Task.CompletedTask;
        }),
    ];

    private Task NavigateActiveTabAsync(string path) =>
        CurrentPane.SelectedTab?.NavigateToAsync(path) ?? Task.CompletedTask;

    private static void OpenSettingsFile()
    {
        var settingsPath = SettingsStore.SettingsPath;
        try
        {
            Process.Start(new ProcessStartInfo(settingsPath) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.UserError("MainWindow.OpenSettings", $"設定ファイルを開けません（{settingsPath}）", ex); }
    }

    private void OpenExternalTerminal()
    {
        var dir = ActivePane?.SelectedTab?.CurrentPath ?? @"C:\";
        try
        {
            Process.Start(new ProcessStartInfo("pwsh.exe")
            {
                WorkingDirectory = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.UserError("MainWindow.OpenTerminal", $"pwsh を起動できません（{dir}）", ex); }
    }

    /// <summary>Opens / closes the palette, searching from the active tab's folder.</summary>
    public void ToggleCommandPalette()
    {
        if (ActiveTab != null)
            CommandPalette.CurrentDirectory = ActiveTab.CurrentPath;
        CommandPalette.Toggle();
    }

    // ==================== Tree / sidebar navigation ====================

    private async void OnTreeFolderSelected(string path)
    {
        try
        {
            var pane = CurrentPane;
            if (pane.SelectedTab != null)
            {
                await pane.SelectedTab.NavigateToAsync(path);
            }
        }
        catch (Exception ex) { Log.UserError("Main.TreeNavigate", $"開けません（{path}）", ex); }
    }

    private async void OnSidebarNavigate(string path)
    {
        try
        {
            var pane = CurrentPane;
            if (pane.SelectedTab != null)
            {
                await pane.SelectedTab.NavigateToAsync(path);
            }
        }
        catch (Exception ex) { Log.UserError("Main.SidebarNavigate", $"開けません（{path}）", ex); }
    }

}
