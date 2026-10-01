using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Snap.Helpers;
using Snap.Models;
using Snap.Services;

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

    /// <summary>Builds the tree roots (Desktop / Documents / Downloads / PC). A failure is logged
    /// and leaves the tree empty; the rest of the app keeps starting (#13).</summary>
    public async Task InitializeAsync()
    {
        List<TreeNode> roots;
        try
        {
            roots = await Task.Run(BuildRootNodes);
        }
        catch (Exception ex)
        {
            Log.Error("FolderTree.Initialize", "folder tree roots could not be built", ex);
            return;
        }

        foreach (var node in roots)
        {
            if (node.FullPath == FilePaneViewModel.PcViewPath)
                node.IsExpanded = true;
            RootNodes.Add(node);
        }
    }

    /// <summary>Icon of a root / drive node, read on the thread pool through the icon worker (#16).
    /// The PC node itself has none.</summary>
    private static void SetRootIcon(TreeNode node)
    {
        try { node.Icon = IconHelper.GetIconAndType(node.FullPath, true).icon; }
        catch (Exception ex) { Log.Warn("FolderTree.Icon", node.FullPath, ex); }
    }

    /// <summary>
    /// Drives were added or removed (WM_DEVICECHANGE, #16): rebuilds the PC node's drive list.
    /// Nodes of drives still present are kept (with their expanded state); new drives get a node.
    /// </summary>
    public async Task RefreshDrivesAsync()
    {
        var pcNode = RootNodes.FirstOrDefault(n => n.FullPath == FilePaneViewModel.PcViewPath);
        if (pcNode == null) return;

        var existing = pcNode.Children.ToList();
        var (nodes, labels) = await Task.Run(() =>
        {
            var list = new List<TreeNode>();
            var names = new Dictionary<TreeNode, string>();
            foreach (var drive in FileSystemService.GetDrives(out _))
            {
                var node = existing.FirstOrDefault(n => FileSystemService.SamePath(n.FullPath, drive.RootPath));
                if (node == null)
                {
                    node = CreateNode(drive.Label, drive.RootPath);
                    SetRootIcon(node);
                }
                names[node] = drive.Label;
                list.Add(node);
            }
            return (list, names);
        });

        // A label can change (another medium under the same drive letter).
        foreach (var node in nodes)
            if (labels.TryGetValue(node, out var label) && node.Name != label)
                node.Name = label;

        // One Remove / Insert per drive, never a Reset: a Reset re-creates every drive's tree item,
        // and the re-created item of the selected folder raises Selected again, which navigates
        // the active pane (TreeViewItem_Selected → OnNodeSelected).
        var children = pcNode.Children;
        for (int i = children.Count - 1; i >= 0; i--)
            if (!nodes.Contains(children[i])) children.RemoveAt(i);
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i < children.Count && ReferenceEquals(children[i], nodes[i])) continue;
            var at = children.IndexOf(nodes[i]);
            if (at >= 0) children.Move(at, i);
            else children.Insert(i, nodes[i]);
        }
        Log.Info("FolderTree.Drives", $"drives now: {string.Join(" ", nodes.Select(n => n.FullPath))}");
    }

    private List<TreeNode> BuildRootNodes()
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

        // PC ノード（ドライブ一覧を子に持つ）
        var pcNode = new TreeNode { Name = "PC", FullPath = FilePaneViewModel.PcViewPath };
        pcNode.RemoveDummyChild(); // ダミー子を除去、直接ドライブを追加
        foreach (var drive in FileSystemService.GetDrives(out _))
            pcNode.Children.Add(CreateNode(drive.Label, drive.RootPath));
        roots.Add(pcNode);

        // Few nodes: their icons are read here (thread pool, via the icon worker), not on the UI thread.
        foreach (var node in roots)
        {
            if (node == pcNode)
                foreach (var drive in node.Children) SetRootIcon(drive);
            else
                SetRootIcon(node);
        }

        return roots;
    }

    public async Task ExpandNodeAsync(TreeNode node)
    {
        if (!node.HasDummyChild) return;

        node.RemoveDummyChild();

        var showHidden = ViewOptions.ShowHidden;
        var children = await Task.Run(() =>
            ListChildFolders(node.FullPath, showHidden).Select(c => CreateNode(c.Name, c.Path)).ToList());

        // Text first, in one step (#16): every child shows the generic folder icon and the real
        // icons come from the icon worker (not for network paths: no network access for icons).
        var folderIcon = IconHelper.GetDefaultFolderIcon().icon;
        foreach (var child in children)
            child.Icon = folderIcon;
        node.Children.ReplaceAll(children);
        if (!FileSystemService.IsNetworkPath(node.FullPath))
            IconHelper.QueuePathIcons(children, c => c.FullPath, (c, icon) => c.Icon = icon);
    }

    /// <summary>
    /// The subfolders of <paramref name="path"/> shown in the tree (thread pool): the shares of
    /// \\server, else the folders, without hidden ones unless <paramref name="showHidden"/> (#17).
    /// Failures are logged and give what could be read.
    /// </summary>
    private static List<(string Name, string Path)> ListChildFolders(string path, bool showHidden)
    {
        var list = new List<(string Name, string Path)>();

        // UNC server path → enumerate shares
        if (FileSystemService.IsUncServerPath(path))
        {
            try
            {
                foreach (var share in FileSystemService.GetShares(path))
                    list.Add((share.Name, share.FullPath));
            }
            catch (Exception ex) { Log.Warn("FolderTree.Expand", $"share enumeration failed: {path}", ex); }
            return list;
        }

        try
        {
            // ネットワークパスでは属性チェックをスキップ（遅い+失敗しやすい）。
            // Hidden only, as in the file list (#17); before, System-only folders were hidden too.
            var isNetwork = path.StartsWith(@"\\");
            foreach (var dir in new DirectoryInfo(path).EnumerateDirectories())
            {
                try
                {
                    if (!showHidden && !isNetwork && (dir.Attributes & FileAttributes.Hidden) != 0)
                        continue;
                    list.Add((dir.Name, dir.FullName));
                }
                catch (Exception ex)
                {
                    // アクセス拒否等は無視
                    Log.Warn("FolderTree.Expand", $"skip: {dir.FullName}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            // 親ディレクトリのアクセス拒否等
            Log.Warn("FolderTree.Expand", $"enumeration failed: {path}", ex);
        }
        return list;
    }

    /// <summary>
    /// Hidden files were switched on / off (Ctrl+H, #17): every folder whose children are loaded
    /// gets them again. Nodes still listed are kept (expanded state, selection); hidden ones are
    /// added or removed one by one, never a Reset (a re-created selected item would raise
    /// Selected and move the active pane, see <see cref="RefreshDrivesAsync"/>).
    /// </summary>
    public async Task ApplyShowHiddenAsync()
    {
        // Adding / removing nodes re-prepares the item containers around them, and a re-prepared
        // container of the selected node raises Selected again (from its IsSelected binding), which
        // would move the active pane to that folder. Selections are ignored until the tree has been
        // laid out after the change.
        _ignoreSelectionDepth++;
        try { await ApplyShowHiddenCoreAsync(); }
        finally
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle, () => _ignoreSelectionDepth--);
        }
    }

    // > 0 while ApplyShowHiddenAsync changes the nodes (until the layout after it).
    private int _ignoreSelectionDepth;

    private async Task ApplyShowHiddenCoreAsync()
    {
        var showHidden = ViewOptions.ShowHidden;
        var loaded = new List<TreeNode>();
        void Collect(IEnumerable<TreeNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.FullPath == "__dummy__" || n.HasDummyChild) continue;
                if (n.FullPath != FilePaneViewModel.PcViewPath && !FileSystemService.IsUncServerPath(n.FullPath))
                    loaded.Add(n);
                Collect(n.Children);
            }
        }
        Collect(RootNodes);

        var paths = loaded.Select(n => n.FullPath).ToList();
        var lists = await Task.Run(() => paths.Select(p => ListChildFolders(p, showHidden)).ToList());

        var folderIcon = IconHelper.GetDefaultFolderIcon().icon;
        var added = 0;
        var removed = 0;
        for (int n = 0; n < loaded.Count; n++)
        {
            var node = loaded[n];
            var wanted = lists[n];
            var children = node.Children;
            var wantedPaths = new HashSet<string>(wanted.Select(w => w.Path), StringComparer.OrdinalIgnoreCase);
            for (int i = children.Count - 1; i >= 0; i--)
                if (!wantedPaths.Contains(children[i].FullPath)) { children.RemoveAt(i); removed++; }

            var newNodes = new List<TreeNode>();
            for (int i = 0; i < wanted.Count; i++)
            {
                var at = -1;
                for (int j = i; j < children.Count; j++)
                    if (FileSystemService.SamePath(children[j].FullPath, wanted[i].Path)) { at = j; break; }
                if (at == i) continue;
                if (at > i) { children.Move(at, i); continue; }
                var created = CreateNode(wanted[i].Name, wanted[i].Path);
                created.Icon = folderIcon;
                children.Insert(Math.Min(i, children.Count), created);
                newNodes.Add(created);
                added++;
            }
            if (newNodes.Count > 0 && !FileSystemService.IsNetworkPath(node.FullPath))
                IconHelper.QueuePathIcons(newNodes, c => c.FullPath, (c, icon) => c.Icon = icon);
        }
        Log.Info("FolderTree.Hidden", $"show hidden = {showHidden}: {loaded.Count} loaded folders, {added} added, {removed} removed");
    }

    /// <summary>
    /// ツリーからユーザーがクリックして選択した場合
    /// </summary>
    public void OnNodeSelected(TreeNode node)
    {
        if (_ignoreSelectionDepth > 0)
        {
            Log.Info("FolderTree.Select", $"ignored while hidden files are re-applied: {node.FullPath}");
            return;
        }
        if (node.FullPath != "__dummy__")
        {
            // The pane navigation triggered by FolderSelected completes asynchronously,
            // so the old "IsSyncing=true/false around the synchronous Invoke" guard was
            // already reset before SyncToPathAsync ran and never actually suppressed the
            // redundant re-expand. Remember the path instead and skip the next sync for it.
            _suppressSyncForPath = node.FullPath;
            FolderSelected?.Invoke(node.FullPath);
        }
    }

    /// <summary>Path whose next SyncToPathAsync should be skipped (set by tree-originated selection).</summary>
    private string? _suppressSyncForPath;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="fullPath"/> is the root itself or sits under it,
    /// respecting directory-separator boundaries (so C:\Foo does not match C:\FooBar).</summary>
    private static bool IsPathWithinRoot(string fullPath, string rootPath)
    {
        if (string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase))
            return true;
        return fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ペインのナビゲーションに追従してツリーを展開・選択する
    /// </summary>
    public bool IsSyncing { get; private set; }

    public async Task SyncToPathAsync(string path)
    {
        if (IsSyncing) return; // ツリー起点のナビゲーション中は再帰しない

        // Tree-originated navigation: the tree node is already selected/expanded, so skip the
        // redundant re-expand for exactly that path. Any other path invalidates the pending skip.
        if (_suppressSyncForPath != null)
        {
            var skip = PathsEqual(_suppressSyncForPath, path);
            _suppressSyncForPath = null;
            if (skip) return;
        }

        IsSyncing = true;
        try
        {
            // PC ビューの場合は PC ノードを選択
            if (path == FilePaneViewModel.PcViewPath)
            {
                DeselectAll(RootNodes);
                var pcNode = RootNodes.FirstOrDefault(n => n.FullPath == FilePaneViewModel.PcViewPath);
                if (pcNode != null)
                {
                    pcNode.IsExpanded = true;
                    pcNode.IsSelected = true;
                }
                return;
            }

            // UNC パスの場合はサーバーノードを動的追加
            if (path.StartsWith(@"\\"))
            {
                await SyncToUncPathAsync(path);
                return;
            }

            // パスをルートから分解
            var fullPath = Path.GetFullPath(path);

            // ルートノードを探す（クイックアクセスノードを優先、次に PC 配下のドライブ）
            TreeNode? current = null;
            foreach (var root in RootNodes)
            {
                if (root.FullPath == FilePaneViewModel.PcViewPath) continue; // PC ノード自体はスキップ
                var rootPath = Path.GetFullPath(root.FullPath).TrimEnd(Path.DirectorySeparatorChar);
                if (IsPathWithinRoot(fullPath, rootPath))
                {
                    current = root;
                    break;
                }
            }

            // クイックアクセスで見つからなければ PC ノード配下のドライブを探す
            if (current == null)
            {
                var pcNode = RootNodes.FirstOrDefault(n => n.FullPath == FilePaneViewModel.PcViewPath);
                if (pcNode != null)
                {
                    foreach (var driveNode in pcNode.Children)
                    {
                        var drivePath = Path.GetFullPath(driveNode.FullPath).TrimEnd(Path.DirectorySeparatorChar);
                        if (IsPathWithinRoot(fullPath, drivePath))
                        {
                            pcNode.IsExpanded = true;
                            current = driveNode;
                            break;
                        }
                    }
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
        catch (Exception ex)
        {
            // パス追跡失敗は無視
            Log.Warn("FolderTree.Sync", path, ex);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task SyncToUncPathAsync(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('\\');
            // Extract server: \\server or \\server\share\sub\path
            var parts = trimmed[2..].Split('\\');
            var serverName = @"\\" + parts[0];

            // Find or create server root node
            var serverNode = RootNodes.FirstOrDefault(n =>
                n.FullPath.TrimEnd('\\').Equals(serverName, StringComparison.OrdinalIgnoreCase));

            if (serverNode == null)
            {
                serverNode = CreateNode(serverName, serverName);
                serverNode.Icon = IconHelper.GetIconAndType(serverName, true).icon;
                RootNodes.Add(serverNode);
            }

            // Expand and traverse
            if (serverNode.HasDummyChild)
                await ExpandNodeAsync(serverNode);
            serverNode.IsExpanded = true;

            var current = serverNode;

            // Traverse remaining parts (share, subfolder, subfolder...)
            for (int i = 1; i < parts.Length; i++)
            {
                TreeNode? next = null;
                foreach (var child in current.Children)
                {
                    if (child.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase))
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

            DeselectAll(RootNodes);
            current.IsSelected = true;
        }
        catch (Exception ex) { Log.Warn("FolderTree.SyncUnc", path, ex); }
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
    /// settings.json から読み込んだブックマーク（パス＋表示名）を復元する。
    /// 表示名が無い（v1.4.2 までの文字列形式）ときはフォルダ名から作る。
    /// UIスレッドで呼ぶこと（アイコン取得のため）。
    /// </summary>
    public void LoadBookmarks(List<PathEntry> entries)
    {
        Bookmarks.Clear();
        foreach (var entry in entries)
        {
            var path = entry?.Path;
            // Drop sentinel entries saved by older versions ("::PC" / "::\PC").
            if (!string.IsNullOrWhiteSpace(path) && !path.StartsWith("::"))
            {
                var name = string.IsNullOrWhiteSpace(entry!.Name) ? DefaultBookmarkName(path) : entry.Name;
                var (icon, _) = IconHelper.GetIconAndType(path, true);
                Bookmarks.Add(new BookmarkItem { Name = name, FullPath = path, Icon = icon });
            }
        }
    }

    private static string DefaultBookmarkName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name; // ドライブルートの場合はパスそのもの
    }

    /// <summary>
    /// ブックマークを追加する。重複は無視。
    /// </summary>
    public void AddBookmark(string path)
    {
        // Only real directories: rejects the "::PC" sentinel and paths that no longer exist.
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Log.Info("FolderTree.AddBookmark", $"not a directory, skipped: {path}");
            return;
        }

        var normalized = Path.GetFullPath(path);
        foreach (var bm in Bookmarks)
        {
            if (string.Equals(bm.FullPath, normalized, StringComparison.OrdinalIgnoreCase))
                return; // 既に登録済み
        }

        var name = DefaultBookmarkName(normalized);
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
    /// 保存用にブックマーク（パス＋表示名）のリストを返す。
    /// </summary>
    public List<PathEntry> GetBookmarks() =>
        Bookmarks.Select(bm => new PathEntry(bm.FullPath, bm.Name)).ToList();
}
