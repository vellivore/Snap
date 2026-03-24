using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Snap.Helpers;
using Snap.Models;

namespace Snap.ViewModels;

public partial class FolderTreeViewModel : ObservableObject
{
    public ObservableCollection<TreeNode> RootNodes { get; } = new();
    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();

    /// <summary>
    /// ツリーでフォルダが選択された時に発火するイベント。
    /// アクティブペインがこのパスにナビゲートする。
    /// </summary>
    public event Action<string>? FolderSelected;

    public async Task InitializeAsync()
    {
        await Task.Run(() =>
        {
            var roots = new List<TreeNode>();

            // デスクトップ
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (Directory.Exists(desktop))
            {
                var node = CreateNode(Path.GetFileName(desktop), desktop);
                roots.Add(node);
            }

            // ドキュメント
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(docs))
            {
                var node = CreateNode(Path.GetFileName(docs), docs);
                roots.Add(node);
            }

            // ダウンロード
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloads))
            {
                var node = CreateNode("Downloads", downloads);
                roots.Add(node);
            }

            // ドライブ一覧
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    var label = drive.IsReady
                        ? $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})"
                        : drive.Name.TrimEnd('\\');
                    var node = CreateNode(label, drive.Name);
                    roots.Add(node);
                }
                catch
                {
                    // ドライブ情報取得失敗は無視
                }
            }

            return roots;
        }).ContinueWith(t =>
        {
            if (t.Result != null)
            {
                foreach (var node in t.Result)
                {
                    node.Icon = IconHelper.GetIconAndType(node.FullPath, true).icon;
                    RootNodes.Add(node);
                }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public async Task ExpandNodeAsync(TreeNode node)
    {
        if (!node.HasDummyChild) return;

        node.RemoveDummyChild();

        var children = await Task.Run(() =>
        {
            var list = new List<TreeNode>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(node.FullPath))
                {
                    try
                    {
                        var name = Path.GetFileName(dir);
                        // 隠しフォルダやシステムフォルダはスキップ
                        var attrs = File.GetAttributes(dir);
                        if ((attrs & FileAttributes.Hidden) != 0 || (attrs & FileAttributes.System) != 0)
                            continue;

                        list.Add(CreateNode(name, dir));
                    }
                    catch
                    {
                        // アクセス拒否等は無視
                    }
                }
            }
            catch
            {
                // 親ディレクトリのアクセス拒否等
            }
            return list;
        });

        foreach (var child in children)
        {
            child.Icon = IconHelper.GetIconAndType(child.FullPath, true).icon;
            node.Children.Add(child);
        }
    }

    /// <summary>
    /// ツリーからユーザーがクリックして選択した場合
    /// </summary>
    public void OnNodeSelected(TreeNode node)
    {
        if (node.FullPath != "__dummy__")
        {
            IsSyncing = true;
            FolderSelected?.Invoke(node.FullPath);
            IsSyncing = false;
        }
    }

    /// <summary>
    /// ペインのナビゲーションに追従してツリーを展開・選択する
    /// </summary>
    public bool IsSyncing { get; private set; }

    public async Task SyncToPathAsync(string path)
    {
        if (IsSyncing) return; // ツリー起点のナビゲーション中は再帰しない
        IsSyncing = true;
        try
        {
            // パスをルートから分解
            var fullPath = Path.GetFullPath(path);

            // ルートノードを探す
            TreeNode? current = null;
            foreach (var root in RootNodes)
            {
                var rootPath = Path.GetFullPath(root.FullPath).TrimEnd(Path.DirectorySeparatorChar);
                if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    current = root;
                    break;
                }
            }

            if (current == null)
            {
                IsSyncing = false;
                return;
            }

            // ルートを展開
            if (current.HasDummyChild)
                await ExpandNodeAsync(current);
            current.IsExpanded = true;

            // パスを1階層ずつ辿る
            var rootFullPath = Path.GetFullPath(current.FullPath).TrimEnd(Path.DirectorySeparatorChar);
            var remaining = fullPath.Substring(rootFullPath.Length).TrimStart(Path.DirectorySeparatorChar);

            if (!string.IsNullOrEmpty(remaining))
            {
                var parts = remaining.Split(Path.DirectorySeparatorChar);
                foreach (var part in parts)
                {
                    TreeNode? next = null;
                    foreach (var child in current.Children)
                    {
                        if (string.Equals(Path.GetFileName(child.FullPath), part, StringComparison.OrdinalIgnoreCase))
                        {
                            next = child;
                            break;
                        }
                    }

                    if (next == null) break;

                    if (next.HasDummyChild)
                        await ExpandNodeAsync(next);
                    next.IsExpanded = true;
                    current = next;
                }
            }

            // 前の選択を解除して新しいノードを選択
            DeselectAll(RootNodes);
            current.IsSelected = true;
        }
        catch
        {
            // パス追跡失敗は無視
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private static void DeselectAll(IEnumerable<TreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsSelected = false;
            DeselectAll(node.Children);
        }
    }

    private static TreeNode CreateNode(string name, string fullPath)
    {
        var node = new TreeNode { Name = name, FullPath = fullPath };
        // サブフォルダがあるかチェックせずダミーを追加（展開時にロード）
        node.AddDummyChild();
        return node;
    }

    // --- ブックマーク管理 ---

    /// <summary>
    /// settings.json から読み込んだブックマークパスを復元する。
    /// UIスレッドで呼ぶこと（アイコン取得のため）。
    /// </summary>
    public void LoadBookmarks(List<string> paths)
    {
        Bookmarks.Clear();
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = path; // ドライブルートの場合
                var (icon, _) = IconHelper.GetIconAndType(path, true);
                Bookmarks.Add(new BookmarkItem { Name = name, FullPath = path, Icon = icon });
            }
        }
    }

    /// <summary>
    /// ブックマークを追加する。重複は無視。
    /// </summary>
    public void AddBookmark(string path)
    {
        var normalized = Path.GetFullPath(path);
        foreach (var bm in Bookmarks)
        {
            if (string.Equals(bm.FullPath, normalized, StringComparison.OrdinalIgnoreCase))
                return; // 既に登録済み
        }

        var name = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = normalized;
        var (icon, _) = IconHelper.GetIconAndType(normalized, true);
        Bookmarks.Add(new BookmarkItem { Name = name, FullPath = normalized, Icon = icon });
    }

    /// <summary>
    /// ブックマークを削除する。
    /// </summary>
    public void RemoveBookmark(BookmarkItem item)
    {
        Bookmarks.Remove(item);
    }

    /// <summary>
    /// ブックマークがクリックされた時のナビゲーション。
    /// </summary>
    public void OnBookmarkSelected(BookmarkItem item)
    {
        FolderSelected?.Invoke(item.FullPath);
    }

    /// <summary>
    /// 保存用にブックマークパスのリストを返す。
    /// </summary>
    public List<string> GetBookmarkPaths()
    {
        var list = new List<string>();
        foreach (var bm in Bookmarks)
            list.Add(bm.FullPath);
        return list;
    }
}
