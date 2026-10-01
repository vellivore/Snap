using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snap.Models;
using Snap.Services;

namespace Snap.ViewModels;

public partial class TabPaneViewModel : ObservableObject
{
    [ObservableProperty]
    private FilePaneViewModel? _selectedTab;

    public ObservableCollection<FilePaneViewModel> Tabs { get; } = new();

    /// <summary>タブが閉じられた時にパスを通知</summary>
    public event Action<string>? TabClosed;

    private UsageTracker? _usageTracker;

    public void SetUsageTracker(UsageTracker tracker)
    {
        _usageTracker = tracker;
    }

    public async Task InitializeAsync()
    {
        await InitializeAsync([new PathEntry(@"C:\")], 0);
    }

    public async Task InitializeAsync(List<PathEntry> entries, int activeIndex)
    {
        entries = entries.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Path)).ToList();
        if (entries.Count == 0)
            entries = [new PathEntry(@"C:\")];

        var tasks = new List<Task>();
        foreach (var entry in entries)
        {
            var tab = new FilePaneViewModel(entry.Path);
            // Restore a user-chosen tab name, but only if the tab really opens that folder
            // (a vanished folder falls back to C:\ and must not carry the old name).
            if (!string.IsNullOrWhiteSpace(entry.Name)
                && string.Equals(tab.CurrentPath, entry.Path, StringComparison.OrdinalIgnoreCase))
                tab.SetCustomTabHeader(entry.Name);
            if (_usageTracker != null) tab.SetUsageTracker(_usageTracker);
            Tabs.Add(tab);
            tasks.Add(tab.InitializeAsync());
        }

        // Clamp active index
        activeIndex = Math.Clamp(activeIndex, 0, Tabs.Count - 1);
        SelectedTab = Tabs[activeIndex];

        await Task.WhenAll(tasks);
    }

    /// <summary>Returns tabs ({path, custom name}) and active index for settings persistence.</summary>
    public (List<PathEntry> Tabs, int ActiveIndex) GetTabState()
    {
        var tabs = Tabs
            .Select(t => new PathEntry(t.CurrentPath, t.HasCustomTabHeader ? t.TabHeader : null))
            .ToList();
        var index = SelectedTab != null ? Tabs.IndexOf(SelectedTab) : 0;
        return (tabs, Math.Max(index, 0));
    }

    /// <summary>True while this is MainViewModel.ActivePane (drives the pane's frame colour, #14).
    /// Set only by MainViewModel.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// Opens a new tab showing <paramref name="path"/>, or the selected tab's folder when null
    /// (#14: a new tab opens where you are, not at C:\). "+" button, Ctrl+N and the palette.
    /// </summary>
    [RelayCommand]
    public Task AddTab(string? path) => OpenTabAsync(path, select: true);

    /// <summary>Opens a tab at <paramref name="path"/> (null = the selected tab's folder).</summary>
    /// <param name="select">Make it the selected tab (false = open in the background).</param>
    /// <param name="insertAfter">Put it right after this tab; appended when null.</param>
    public async Task<FilePaneViewModel> OpenTabAsync(string? path, bool select, FilePaneViewModel? insertAfter = null)
    {
        path ??= SelectedTab?.CurrentPath;
        var tab = new FilePaneViewModel(path ?? @"C:\");
        if (_usageTracker != null) tab.SetUsageTracker(_usageTracker);

        var at = insertAfter != null ? Tabs.IndexOf(insertAfter) : -1;
        if (at >= 0) Tabs.Insert(at + 1, tab);
        else Tabs.Add(tab);

        if (select) SelectedTab = tab;
        await tab.InitializeAsync();
        return tab;
    }

    [RelayCommand]
    public void CloseTab(FilePaneViewModel tab)
    {
        if (Tabs.Count <= 1)
            return;

        var closedPath = tab.CurrentPath;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        // Closing ends the tab's folder watcher and icon work (#16). A tab moved to another pane
        // (MainViewModel.MoveTab) is removed without this and keeps them.
        tab.Dispose();
        TabClosed?.Invoke(closedPath);

        if (SelectedTab == tab || SelectedTab == null)
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    /// <summary>Closes every tab except <paramref name="keep"/>, which becomes selected.</summary>
    public void CloseOtherTabs(FilePaneViewModel keep)
    {
        if (!Tabs.Contains(keep)) return;
        SelectedTab = keep;
        foreach (var tab in Tabs.Where(t => t != keep).ToList())
            CloseTab(tab);
    }

    /// <summary>Selects the next (<paramref name="delta"/> = 1) or previous (-1) tab, wrapping around.</summary>
    public void SelectRelativeTab(int delta)
    {
        if (Tabs.Count == 0) return;
        var index = SelectedTab != null ? Tabs.IndexOf(SelectedTab) : 0;
        index = ((index + delta) % Tabs.Count + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }
}
