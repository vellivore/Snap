using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Snap.Helpers;
using Snap.Models;

namespace Snap.ViewModels;

public partial class SidebarViewModel : ObservableObject
{
    private const int MaxToday = 20;

    /// <summary>ブックマーク（FolderTreeViewModel.Bookmarks と同一インスタンス）</summary>
    [ObservableProperty]
    private ObservableCollection<BookmarkItem> _pinnedItems = new();

    /// <summary>ブックマーク操作用の FolderTreeViewModel 参照</summary>
    public FolderTreeViewModel? FolderTree { get; set; }

    /// <summary>最近開いた/閉じたフォルダ（自動記録）</summary>
    public ObservableCollection<BookmarkItem> TodayItems { get; } = new();

    /// <summary>Pinned/Today アイテムクリック時にナビゲーションを要求</summary>
    public event Action<string>? NavigateRequested;

    public void OnNavigate(string path) => NavigateRequested?.Invoke(path);

    public void AddToday(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Pinned に既にある場合は Today に追加しない
        if (PinnedItems.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        // 重複を除去（先頭に移動）
        var existing = TodayItems.FirstOrDefault(i =>
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) TodayItems.Remove(existing);

        TodayItems.Insert(0, CreateItem(path));

        while (TodayItems.Count > MaxToday)
            TodayItems.RemoveAt(TodayItems.Count - 1);
    }

    /// <summary>Today → Pinned に移動</summary>
    public void PinItem(BookmarkItem item)
    {
        TodayItems.Remove(item);
        FolderTree?.AddBookmark(item.FullPath);
    }

    /// <summary>Pinned → Today に移動</summary>
    public void UnpinItem(BookmarkItem item)
    {
        FolderTree?.RemoveBookmark(item);
        if (!TodayItems.Any(i => string.Equals(i.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
            TodayItems.Insert(0, item);
    }

    public void RemovePinned(BookmarkItem item) => FolderTree?.RemoveBookmark(item);

    public void MovePinned(BookmarkItem item, int targetIndex)
    {
        var currentIndex = PinnedItems.IndexOf(item);
        if (currentIndex < 0) return;
        if (targetIndex > currentIndex) targetIndex--;
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex >= PinnedItems.Count) targetIndex = PinnedItems.Count - 1;
        if (currentIndex == targetIndex) return;
        PinnedItems.Move(currentIndex, targetIndex);
    }

    public void RemoveToday(BookmarkItem item) => TodayItems.Remove(item);

    public List<string> GetTodayPaths() => TodayItems.Select(i => i.FullPath).ToList();

    public void LoadToday(List<string> paths)
    {
        TodayItems.Clear();
        foreach (var path in paths)
            TodayItems.Add(CreateItem(path));
    }

    private static BookmarkItem CreateItem(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = path;

        var item = new BookmarkItem { Name = name, FullPath = Path.GetFullPath(path) };
        try { item.Icon = IconHelper.GetIconAndType(path, true).icon; } catch { }
        return item;
    }
}
