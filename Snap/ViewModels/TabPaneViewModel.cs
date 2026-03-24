using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        await InitializeAsync([@"C:\"], 0);
    }

    public async Task InitializeAsync(List<string> paths, int activeIndex)
    {
        if (paths.Count == 0)
            paths = [@"C:\"];

        var tasks = new List<Task>();
        foreach (var path in paths)
        {
            var tab = new FilePaneViewModel(path);
            if (_usageTracker != null) tab.SetUsageTracker(_usageTracker);
            Tabs.Add(tab);
            tasks.Add(tab.InitializeAsync());
        }

        // Clamp active index
        activeIndex = Math.Clamp(activeIndex, 0, Tabs.Count - 1);
        SelectedTab = Tabs[activeIndex];

        await Task.WhenAll(tasks);
    }

    /// <summary>Returns tab paths and active index for settings persistence.</summary>
    public (List<string> Paths, int ActiveIndex) GetTabState()
    {
        var paths = Tabs.Select(t => t.CurrentPath).ToList();
        var index = SelectedTab != null ? Tabs.IndexOf(SelectedTab) : 0;
        return (paths, Math.Max(index, 0));
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
