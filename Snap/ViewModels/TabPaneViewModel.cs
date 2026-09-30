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

    [RelayCommand]
    public async Task AddTab()
    {
        var tab = new FilePaneViewModel();
        if (_usageTracker != null) tab.SetUsageTracker(_usageTracker);
        Tabs.Add(tab);
        SelectedTab = tab;
        await tab.InitializeAsync();
    }

    [RelayCommand]
    public void CloseTab(FilePaneViewModel tab)
    {
        if (Tabs.Count <= 1)
            return;

        var closedPath = tab.CurrentPath;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        TabClosed?.Invoke(closedPath);

        if (SelectedTab == tab || SelectedTab == null)
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }
}
