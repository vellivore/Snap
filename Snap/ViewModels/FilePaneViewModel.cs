using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snap.Helpers;
using Snap.Models;
using Snap.Services;

namespace Snap.ViewModels;

public partial class FilePaneViewModel : ObservableObject
{
    [ObservableProperty]
    private string _currentPath = string.Empty;

    /// <summary>
    /// Text in the address bar while editing. Separate from <see cref="CurrentPath"/> so a
    /// half-typed path never becomes the paste/drop/F5 target, gets saved, or enters Today.
    /// Committed only by <see cref="OnAddressBarEnter"/>.
    /// </summary>
    [ObservableProperty]
    private string _addressText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _tabHeader = "新しいタブ";

    /// <summary>True when user has set a custom tab name (not auto-generated from path).</summary>
    public bool HasCustomTabHeader { get; private set; }

    /// <summary>The automatic tab name for <paramref name="path"/> (folder name, drive root, or "PC").</summary>
    public static string DefaultTabHeader(string path)
    {
        if (string.Equals(path, PcViewPath, StringComparison.OrdinalIgnoreCase))
            return "PC";
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            is { Length: > 0 } name ? name : path;
    }

    /// <summary>Gives the tab a user-chosen name that survives refreshes (until the folder changes).</summary>
    public void SetCustomTabHeader(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        HasCustomTabHeader = true;
        TabHeader = name;
    }

    /// <summary>Drops the user-chosen name and shows the automatic one again.</summary>
    public void ResetTabHeader()
    {
        HasCustomTabHeader = false;
        TabHeader = DefaultTabHeader(CurrentPath);
    }

    [ObservableProperty]
    private FileItem? _selectedItem;

    /// <summary>Every selected entry in list order (the list view reports it through
    /// <see cref="SetSelection"/>). Used by commands that do not come from the list, e.g. the palette.</summary>
    public IReadOnlyList<FileItem> SelectedItems { get; private set; } = [];

    /// <summary>Called by the view whenever the list's selection changes.</summary>
    public void SetSelection(System.Collections.IList selectedItems) =>
        SelectedItems = SelectedInListOrder(selectedItems);

    // ==================== Host (#15) ====================
    // Set by MainViewModel when the tab joins a pane.

    /// <summary>Confirmations and the rename dialog.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Called after a file operation with the folders it changed (source and target);
    /// refreshes every pane showing one of them (MainViewModel.RefreshPanesShowing).</summary>
    public Func<IReadOnlyList<string>, Task>? FoldersChanged { get; set; }

    /// <summary>The view should select and scroll to this entry (after rename / new folder).</summary>
    public event Action<FileItem>? RevealRequested;


    public ObservableCollection<FileItem> Items { get; } = new();

    // All items before filtering
    private List<FileItem> _allItems = new();

    /// <summary>Filter bar text (Ctrl+Shift+F, #14): <see cref="Items"/> shows only the entries of
    /// the folder whose name contains it. Cleared by Esc and by moving to another folder.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>True while the filter bar is shown.</summary>
    [ObservableProperty]
    private bool _isFilterVisible;

    // Set while navigation clears the filter itself (it re-sorts right after).
    private bool _suppressFilterApply;

    partial void OnFilterTextChanged(string value)
    {
        if (_suppressFilterApply) return;
        ApplySortToItems();
        UpdateItemCountStatus(skipped: 0);
    }

    /// <summary>Ctrl+Shift+F: shows the filter bar (the view focuses it).</summary>
    public void ShowFilter() => IsFilterVisible = true;

    /// <summary>Esc in the filter bar: hides it and shows every entry again.</summary>
    public void ClearFilter()
    {
        IsFilterVisible = false;
        FilterText = string.Empty;
    }

    private void UpdateItemCountStatus(int skipped)
    {
        var count = string.IsNullOrEmpty(FilterText)
            ? $"{Items.Count} 項目"
            : $"{Items.Count} / {_allItems.Count} 項目（絞り込み: {FilterText}）";
        StatusMessage = skipped > 0 ? $"{count}（読めない項目 {skipped} 件を省略）" : count;
    }

    // Sorting state
    private string _sortColumn = "Name";
    private bool _sortAscending = true;

    public string SortColumn => _sortColumn;
    public bool SortAscending => _sortAscending;

    // Navigation history
    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    // Navigation generation: every navigation takes a new number; a load that finishes after a
    // newer one started is discarded (it must not touch Items / CurrentPath / history / IsLoading).
    private int _navGeneration;

    // Windows clipboard format that marks cut (Move) vs copy, shared with Explorer.
    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    // Usage tracking
    private UsageTracker? _usageTracker;

    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex < _history.Count - 1;

    public FilePaneViewModel() : this(@"C:\") { }

    public FilePaneViewModel(string initialPath)
    {
        // Validate the path; fall back to C:\ if it doesn't exist
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
            initialPath = @"C:\";
        CurrentPath = initialPath;
        AddressText = initialPath;
    }

    partial void OnCurrentPathChanged(string value) => AddressText = value;

    /// <summary>Discard address-bar edits (Esc / focus loss).</summary>
    public void ResetAddressText() => AddressText = CurrentPath;

    public void SetUsageTracker(UsageTracker tracker)
    {
        _usageTracker = tracker;
    }


    public async Task InitializeAsync()
    {
        await NavigateToAsync(CurrentPath);
    }

    /// <summary>Special path representing the "PC" (drive list) view.</summary>
    public const string PcViewPath = "::PC";

    [RelayCommand]
    public Task NavigateToAsync(string path) => NavigateCoreAsync(path, addToHistory: true, historyIndex: null);

    /// <param name="addToHistory">Push onto the history on success (normal navigation).</param>
    /// <param name="historyIndex">Back/Forward: the history slot to move to, applied only on success.</param>
    private async Task NavigateCoreAsync(string path, bool addToHistory, int? historyIndex)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        path = path.Trim();

        // "PC" view — drive list
        bool isPcView = string.Equals(path, PcViewPath, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(path, "PC", StringComparison.OrdinalIgnoreCase);

        if (!isPcView)
        {
            // Normalize path
            if (!path.StartsWith(@"\\"))
            {
                try
                {
                    path = Path.GetFullPath(path);
                }
                catch (Exception ex)
                {
                    Log.Warn("FilePane.Navigate", $"invalid path: {path}", ex);
                    StatusMessage = $"パスが無効です: {ex.Message}";
                    return;
                }
            }
        }
        else
        {
            path = PcViewPath;
        }

        var gen = Interlocked.Increment(ref _navGeneration);
        bool IsCurrent() => gen == Volatile.Read(ref _navGeneration);

        IsLoading = true;
        StatusMessage = "読み込み中...";

        try
        {
            var tracker = _usageTracker;
            var load = await Task.Run(() => isPcView ? LoadDrives() : LoadDirectory(path));

            // A newer navigation started while this one was loading: drop this result.
            if (!IsCurrent()) return;

            var items = load.Items;

            // Set frequency levels
            if (tracker != null)
            {
                foreach (var item in items)
                {
                    item.FrequencyLevel = tracker.GetFrequencyLevel(item.FullPath);
                }
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                // A custom tab name is kept across refreshes / paste / delete (same folder)
                // and reset only when the tab moves to a different folder (#12).
                var pathChanged = !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase);
                _allItems = items;
                // The filter belongs to the folder it was typed in: moving clears it, a refresh keeps it (#14).
                if (pathChanged)
                {
                    _suppressFilterApply = true;
                    try { IsFilterVisible = false; FilterText = string.Empty; }
                    finally { _suppressFilterApply = false; }
                }
                ApplySortToItems();
                CurrentPath = path;
                if (pathChanged || !HasCustomTabHeader)
                    ResetTabHeader();
                UpdateItemCountStatus(load.Skipped);
            });

            // Update navigation history (only after a successful load)
            if (historyIndex is int hi)
            {
                _historyIndex = hi;
            }
            else if (addToHistory)
            {
                // 同じパスなら履歴に追加しない
                if (!(_historyIndex >= 0 && _historyIndex < _history.Count
                      && string.Equals(_history[_historyIndex], path, StringComparison.OrdinalIgnoreCase)))
                {
                    // Remove forward history
                    if (_historyIndex < _history.Count - 1)
                    {
                        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                    }
                    _history.Add(path);
                    _historyIndex = _history.Count - 1;
                }
            }

            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            GoBackCommand.NotifyCanExecuteChanged();
            GoForwardCommand.NotifyCanExecuteChanged();
        }
        catch (UnauthorizedAccessException ex)
        {
            if (!IsCurrent()) return;
            Log.Warn("FilePane.Navigate", $"access denied: {path}", ex);
            StatusMessage = $"アクセスが拒否されました: {path}";
        }
        catch (DirectoryNotFoundException ex)
        {
            if (!IsCurrent()) return;
            Log.Warn("FilePane.Navigate", $"not found: {path}", ex);
            StatusMessage = $"ディレクトリが見つかりません: {path} ({ex.Message})";
        }
        catch (IOException ex)
        {
            if (!IsCurrent()) return;
            Log.Warn("FilePane.Navigate", $"io error: {path}", ex);
            StatusMessage = $"IOエラー: {ex.Message}";
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            Log.Error("FilePane.Navigate", path, ex);
            StatusMessage = $"エラー: {ex.Message}";
        }
        finally
        {
            // Only the latest navigation clears the loading state.
            if (IsCurrent())
                IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    public async Task GoBack()
    {
        if (!CanGoBack) return;
        var target = _historyIndex - 1;
        await NavigateCoreAsync(_history[target], addToHistory: false, historyIndex: target);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    public async Task GoForward()
    {
        if (!CanGoForward) return;
        var target = _historyIndex + 1;
        await NavigateCoreAsync(_history[target], addToHistory: false, historyIndex: target);
    }

    /// <summary>
    /// The folder above <paramref name="path"/>: the parent directory; above a drive root is the
    /// PC view; above \\server\share is \\server. Null at the PC view and at \\server.
    /// </summary>
    public static string? GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == PcViewPath) return null;
        if (path.StartsWith(@"\\"))
        {
            if (FileSystemService.IsUncServerPath(path)) return null;
            var trimmed = path.TrimEnd('\\');
            var lastSep = trimmed.LastIndexOf('\\');
            return lastSep > 1 ? trimmed[..lastSep] : null;
        }
        return Directory.GetParent(path)?.FullName ?? PcViewPath;
    }

    /// <summary>Backspace / Alt+Up / double-click on empty space: go to the folder above.</summary>
    [RelayCommand]
    public async Task GoUpAsync()
    {
        var parent = GetParentPath(CurrentPath);
        if (parent != null)
            await NavigateToAsync(parent);
    }

    [RelayCommand]
    public async Task Refresh()
    {
        await NavigateCoreAsync(CurrentPath, addToHistory: false, historyIndex: null);
    }

    [RelayCommand]
    public async Task OpenItem(FileItem? item)
    {
        if (item == null) return;

        if (item.IsDirectory)
        {
            await NavigateToAsync(item.FullPath);
        }
        else
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = item.FullPath,
                    UseShellExecute = true
                };
                Process.Start(psi);

                // Record file access and update frequency level
                _usageTracker?.RecordAccess(item.FullPath);
                if (_usageTracker != null)
                {
                    item.FrequencyLevel = _usageTracker.GetFrequencyLevel(item.FullPath);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FilePane.OpenItem", item.FullPath, ex);
                StatusMessage = $"ファイルを開けません: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public void CopyItems(System.Collections.IList? selectedItems) => PutOnClipboard(selectedItems, cut: false);

    [RelayCommand]
    public void CutItems(System.Collections.IList? selectedItems) => PutOnClipboard(selectedItems, cut: true);

    /// <summary>
    /// Puts the selection on the Windows clipboard (the only source of truth) as FileDrop plus
    /// "Preferred DropEffect" (Move for cut, Copy for copy), the same way Explorer does, so a
    /// cut in Snap is a move when pasted in Explorer and vice versa.
    /// </summary>
    private void PutOnClipboard(System.Collections.IList? selectedItems, bool cut)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var paths = selectedItems.OfType<FileItem>()
            .Where(f => f.Name != "..")
            .Select(f => f.FullPath)
            .ToList();

        if (paths.Count == 0) return;

        try
        {
            var fileDropList = new StringCollection();
            fileDropList.AddRange(paths.ToArray());

            var data = new DataObject();
            data.SetFileDropList(fileDropList);
            var effect = cut ? DragDropEffects.Move : DragDropEffects.Copy;
            data.SetData(PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes((int)effect)));

            Application.Current.Dispatcher.Invoke(() => Clipboard.SetDataObject(data, copy: true));
        }
        catch (Exception ex)
        {
            Log.Warn("FilePane.Clipboard", cut ? "cut failed" : "copy failed", ex);
            StatusMessage = $"クリップボードに書き込めません: {ex.Message}";
            return;
        }

        StatusMessage = cut ? $"{paths.Count} 項目を切り取りました" : $"{paths.Count} 項目をコピーしました";
    }

    /// <summary>Reads FileDrop and Preferred DropEffect from the Windows clipboard.</summary>
    private static (List<string> Paths, bool IsCut) ReadClipboard()
    {
        var data = Clipboard.GetDataObject();
        if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
            return (new List<string>(), false);

        var paths = (data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        var effect = DragDropEffects.Copy;
        if (data.GetDataPresent(PreferredDropEffectFormat))
        {
            var raw = data.GetData(PreferredDropEffectFormat);
            byte[]? bytes = raw switch
            {
                byte[] b => b,
                Stream st => ReadAll(st),
                _ => null,
            };
            if (bytes is { Length: >= 4 })
                effect = (DragDropEffects)BitConverter.ToInt32(bytes, 0);
        }

        // Explorer: cut = MOVE (2), copy = COPY|LINK (5). Treat anything that allows copy as copy.
        bool isCut = (effect & DragDropEffects.Move) != 0 && (effect & DragDropEffects.Copy) == 0;
        return (paths, isCut);

        static byte[] ReadAll(Stream st)
        {
            if (st.CanSeek) st.Position = 0;
            var buf = new byte[4];
            int n = 0;
            while (n < 4)
            {
                int r = st.Read(buf, n, 4 - n);
                if (r <= 0) break;
                n += r;
            }
            return n == 4 ? buf : Array.Empty<byte>();
        }
    }

    [RelayCommand]
    public async Task PasteItems()
    {
        List<string> paths;
        bool isCut;
        try
        {
            (paths, isCut) = Application.Current.Dispatcher.Invoke(ReadClipboard);
        }
        catch (Exception ex)
        {
            Log.Warn("FilePane.Paste", "clipboard read failed", ex);
            StatusMessage = $"クリップボードを読めません: {ex.Message}";
            return;
        }

        if (paths.Count == 0)
        {
            StatusMessage = "貼り付けるファイルがありません";
            return;
        }

        var destDir = CurrentPath;

        // PC ビュー（ドライブ一覧）・\\server には貼り付けできない
        if (!CanCreateHere(destDir))
        {
            StatusMessage = "ここには貼り付けできません";
            return;
        }

        IsLoading = true;
        try
        {
            // Shell の IFileOperation で実行（UAC 昇格・進捗・名前衝突ダイアログ・ごみ箱に対応）
            var result = isCut
                ? await Interop.ShellFileOperation.MoveAsync(paths, destDir)
                : await Interop.ShellFileOperation.CopyAsync(paths, destDir);

            // Like Explorer: after a cut is pasted, empty the clipboard so a second paste
            // doesn't try to move files that are already gone. Only if it still holds our data.
            if (isCut && result.Success)
            {
                try
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var (now, nowCut) = ReadClipboard();
                        if (nowCut && now.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
                            Clipboard.Clear();
                    });
                }
                catch (Exception ex) { Log.Warn("FilePane.Paste", "clipboard clear after move failed", ex); }
            }

            if (!result.Success && !result.Aborted)
                Log.Warn("FilePane.Paste", $"{(isCut ? "move" : "copy")} to {destDir} failed: {result.Error}");

            // A move changes the source folders too.
            await RefreshFoldersAsync(isCut ? paths.Select(FileSystemService.ParentOf).Append(destDir) : [destDir]);

            StatusMessage = result.Success
                ? (isCut ? $"{paths.Count} 項目を移動しました" : $"{paths.Count} 項目を貼り付けました")
                : result.Aborted
                    ? "操作がキャンセルされました"
                    : $"貼り付けエラー: {result.Error}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Copies (<paramref name="copy"/>) or moves <paramref name="paths"/> into <paramref name="targetDir"/>
    /// through the shell, then refreshes every pane showing the target or (for a move) a source
    /// folder. Used by drag &amp; drop onto this pane and by the palette's /copy to, /move to (#15).
    /// </summary>
    public async Task DropFilesAsync(IReadOnlyList<string> paths, string targetDir, bool copy)
    {
        if (paths.Count == 0) return;

        if (!CanCreateHere(targetDir))
        {
            StatusMessage = "ここにはドロップできません";
            return;
        }

        // A folder cannot go into itself or one of its subfolders (the shell would stop with an error).
        if (paths.Any(p => FileSystemService.IsSameOrUnder(targetDir, p)))
        {
            StatusMessage = "フォルダーをそれ自身の中へは送れません";
            return;
        }

        // Dropping items onto the folder they are already in does nothing (as before).
        if (paths.All(p => FileSystemService.SamePath(FileSystemService.ParentOf(p), targetDir)))
        {
            StatusMessage = "元と同じフォルダーです";
            return;
        }

        IsLoading = true;
        try
        {
            // Shell の IFileOperation で実行（UAC 昇格・進捗・名前衝突ダイアログはシェルに任せる）
            var result = copy
                ? await Interop.ShellFileOperation.CopyAsync(paths, targetDir)
                : await Interop.ShellFileOperation.MoveAsync(paths, targetDir);

            if (!result.Success && !result.Aborted)
                Log.Warn("FilePane.Drop", $"{(copy ? "copy" : "move")} to {targetDir} failed: {result.Error}");

            await RefreshFoldersAsync(copy ? [targetDir] : paths.Select(FileSystemService.ParentOf).Append(targetDir));

            StatusMessage = result.Success
                ? (copy ? $"{paths.Count} 項目をコピーしました" : $"{paths.Count} 項目を移動しました")
                : result.Aborted
                    ? "操作がキャンセルされました"
                    : $"{(copy ? "コピー" : "移動")}エラー: {result.Error}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Delete: to the recycle bin (Snap asks first).</summary>
    [RelayCommand]
    public Task DeleteItems(System.Collections.IList? selectedItems) => DeleteCoreAsync(selectedItems, permanent: false);

    /// <summary>Shift+Delete: deletes for good, without the recycle bin (Snap asks, then the shell asks too).</summary>
    [RelayCommand]
    public Task DeleteItemsPermanently(System.Collections.IList? selectedItems) => DeleteCoreAsync(selectedItems, permanent: true);

    private async Task DeleteCoreAsync(System.Collections.IList? selectedItems, bool permanent)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var items = selectedItems.OfType<FileItem>()
            .Where(f => f.Name != "..")
            .ToList();

        if (items.Count == 0) return;
        if (Dialogs == null) { Log.Warn("FilePane.Delete", "no dialog service"); return; }

        var names = string.Join("\n", items.Select(f => f.Name));
        var confirmed = permanent
            ? Dialogs.Confirm(
                $"以下の {items.Count} 項目を完全に削除しますか（元に戻せません）？\n\n{names}",
                "完全に削除")
            : Dialogs.Confirm(
                $"以下の {items.Count} 項目をごみ箱へ移動しますか？\n\n{names}\n\n※ ごみ箱が使えない場所（ネットワーク等）では完全に削除されます。",
                "ごみ箱へ移動");
        if (!confirmed) return;

        IsLoading = true;
        try
        {
            // Shell の IFileOperation で削除（権限が必要な対象は UAC 昇格。通常はごみ箱へ送る＝元に戻せる）
            var paths = items.Select(i => i.FullPath).ToList();
            var opResult = await Interop.ShellFileOperation.DeleteAsync(paths, permanent);

            if (!opResult.Success && !opResult.Aborted)
                Log.Warn("FilePane.Delete", $"{(permanent ? "permanent " : "")}delete failed: {opResult.Error}");

            await RefreshFoldersAsync(paths.Select(FileSystemService.ParentOf));

            StatusMessage = opResult.Success
                ? (permanent ? $"{items.Count} 項目を完全に削除しました" : $"{items.Count} 項目をごみ箱へ移動しました")
                : opResult.Aborted
                    ? "削除がキャンセルされました"
                    : $"削除エラー: {opResult.Error}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>F2: renames through the shell (UAC, undo), then selects the renamed item again.</summary>
    [RelayCommand]
    public Task RenameItem(FileItem? item) => RenameCoreAsync(item);

    private async Task RenameCoreAsync(FileItem? item)
    {
        if (item == null || item.Name == "..") return;
        var dir = FileSystemService.ParentOf(item.FullPath);
        // Drives in the PC view and \\server\share have no folder to rename them in.
        if (dir == null || !CanCreateHere(dir)) return;
        if (Dialogs == null) { Log.Warn("FilePane.Rename", "no dialog service"); return; }

        var newName = Dialogs.AskName(item.Name, isFile: !item.IsDirectory);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name)
        {
            RevealPath(item.FullPath);
            return;
        }

        var result = await Interop.ShellFileOperation.RenameAsync(item.FullPath, newName);
        if (!result.Success && !result.Aborted)
            Log.Warn("FilePane.Rename", $"{item.FullPath} -> {newName}: {result.Error}");

        await RefreshFoldersAsync([dir]);

        var newPath = Path.Combine(dir, newName);
        var renamed = result.Success && (Directory.Exists(newPath) || File.Exists(newPath));
        RevealPath(renamed ? newPath : item.FullPath);

        StatusMessage = renamed
            ? $"名前を変更しました: {newName}"
            : result.Aborted
                ? "名前の変更がキャンセルされました"
                : $"名前変更エラー: {result.Error ?? "変更後の項目が見つかりません"}";
    }

    /// <summary>
    /// Ctrl+Shift+N: creates "新しいフォルダー" (or "新しいフォルダー (2)" ...) here through the
    /// shell, selects it and opens the rename dialog right away.
    /// </summary>
    public async Task NewFolderAsync()
    {
        var dir = CurrentPath;
        if (!CanCreateHere(dir))
        {
            StatusMessage = "ここにはフォルダーを作れません";
            return;
        }

        var name = FileSystemService.UniqueName(dir, "新しいフォルダー", isDirectory: true);
        if (name == null)
        {
            StatusMessage = "フォルダー名を決められません";
            return;
        }

        var result = await Interop.ShellFileOperation.NewFolderAsync(dir, name);
        var path = Path.Combine(dir, name);
        await RefreshFoldersAsync([dir]);

        if (!result.Success || !Directory.Exists(path))
        {
            if (!result.Aborted)
                Log.Warn("FilePane.NewFolder", $"{path}: {result.Error}");
            StatusMessage = result.Aborted
                ? "フォルダーの作成がキャンセルされました"
                : $"フォルダーを作れません: {result.Error ?? "作成後のフォルダーが見つかりません"}";
            return;
        }

        var created = FindItem(path);
        if (created == null)
        {
            StatusMessage = $"フォルダーを作成しました: {name}";
            return;
        }
        SelectedItem = created;
        await RenameCoreAsync(created);
    }

    /// <summary>
    /// After a shell context-menu command: refreshes the panes showing this folder, the folders
    /// of the items the menu was for and the folders of the files on the clipboard (a paste of a
    /// cut empties those). <paramref name="clipboardFolders"/> is read before the menu runs.
    /// </summary>
    public Task RefreshAfterShellCommandAsync(IEnumerable<string>? itemPaths, IEnumerable<string> clipboardFolders)
    {
        var folders = (itemPaths ?? []).Select(FileSystemService.ParentOf)
            .Concat(clipboardFolders)
            .Append(CurrentPath);
        return RefreshFoldersAsync(folders);
    }

    /// <summary>Folders of the files now on the clipboard (for <see cref="RefreshAfterShellCommandAsync"/>).</summary>
    public static List<string> ClipboardSourceFolders()
    {
        try
        {
            return ReadClipboard().Paths
                .Select(FileSystemService.ParentOf)
                .Where(d => d != null)
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("FilePane.ShellMenu", "clipboard read failed", ex);
            return [];
        }
    }

    /// <summary>
    /// Refreshes every pane showing one of <paramref name="folders"/> through
    /// <see cref="FoldersChanged"/> (MainViewModel.RefreshPanesShowing); without a host, just this tab.
    /// </summary>
    private async Task RefreshFoldersAsync(IEnumerable<string?> folders)
    {
        var list = folders
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return;

        if (FoldersChanged != null)
            await FoldersChanged(list);
        else if (list.Any(f => FileSystemService.SamePath(f, CurrentPath)))
            await Refresh();
    }

    /// <summary>A real folder that can hold new items (not the PC view, not \\server).</summary>
    private static bool CanCreateHere(string dir) =>
        !string.IsNullOrEmpty(dir)
        && !string.Equals(dir, PcViewPath, StringComparison.OrdinalIgnoreCase)
        && !FileSystemService.IsUncServerPath(dir);

    /// <summary>The loaded entry for <paramref name="path"/> (also when the filter hides it).</summary>
    private FileItem? FindItem(string path) =>
        _allItems.FirstOrDefault(i => FileSystemService.SamePath(i.FullPath, path));

    /// <summary>Selects the entry for <paramref name="path"/> and asks the view to scroll to it.</summary>
    private void RevealPath(string path)
    {
        var item = Items.FirstOrDefault(i => FileSystemService.SamePath(i.FullPath, path));
        if (item == null) return;
        SelectedItem = item;
        RevealRequested?.Invoke(item);
    }

    /// <summary>
    /// Enter on the list (#14): when only files are selected, opens all of them; when a folder is
    /// among them, moves into the first folder (in list order).
    /// </summary>
    public async Task OpenSelection(System.Collections.IList? selectedItems)
    {
        var items = SelectedInListOrder(selectedItems);
        if (items.Count == 0) return;

        var firstFolder = items.FirstOrDefault(f => f.IsDirectory);
        if (firstFolder != null)
        {
            await NavigateToAsync(firstFolder.FullPath);
            return;
        }
        foreach (var file in items)
            await OpenItem(file);
    }

    private List<FileItem> SelectedInListOrder(System.Collections.IList? selectedItems) =>
        (selectedItems?.OfType<FileItem>() ?? Enumerable.Empty<FileItem>())
            .Where(f => f.Name != "..")
            .OrderBy(f => Items.IndexOf(f))
            .ToList();

    /// <summary>Alt+Enter: the shell's Properties dialog for the item (this folder when null).</summary>
    public void ShowProperties(FileItem? item)
    {
        var target = item?.FullPath ?? CurrentPath;
        if (string.IsNullOrEmpty(target) || target == PcViewPath) return;

        if (!Interop.ShellProperties.Show(target, out var error))
        {
            Log.Warn("FilePane.Properties", $"{target}: {error}");
            StatusMessage = $"プロパティを表示できません: {error}";
        }
    }

    /// <summary>Ctrl+Shift+C: copies the selected items' full paths as text, one per line
    /// (this folder's path when nothing is selected).</summary>
    public void CopyFullPaths(System.Collections.IList? selectedItems)
    {
        var paths = SelectedInListOrder(selectedItems).Select(f => f.FullPath).ToList();
        if (paths.Count == 0 && CurrentPath != PcViewPath)
            paths.Add(CurrentPath);
        if (paths.Count == 0) return;

        try
        {
            Interop.ClipboardText.Set(string.Join(Environment.NewLine, paths));
            StatusMessage = paths.Count == 1
                ? $"パスをコピーしました: {paths[0]}"
                : $"{paths.Count} 件のパスをコピーしました";
        }
        catch (Exception ex)
        {
            Log.Warn("FilePane.CopyPath", "clipboard write failed", ex);
            StatusMessage = $"クリップボードに書き込めません: {ex.Message}";
        }
    }

    private readonly record struct LoadResult(List<FileItem> Items, int Skipped);

    private static LoadResult LoadDrives()
    {
        var items = new List<FileItem>();
        var drives = FileSystemService.GetDrives(out var skipped);
        foreach (var drive in drives)
        {
            var (icon, _) = IconHelper.GetIconAndType(drive.RootPath, true);
            items.Add(new FileItem
            {
                Name = drive.Label,
                FullPath = drive.RootPath,
                LastModified = DateTime.MinValue,
                Size = drive.TotalSize,
                IsDirectory = true,
                Type = drive.TypeName,
                Icon = icon,
            });
        }
        return new LoadResult(items, skipped);
    }

    /// <summary>
    /// Lists a directory. Failures of the enumeration itself (access denied, gone, I/O) are
    /// thrown so the caller reports them and keeps them out of the history; only failures of
    /// individual entries are skipped and counted.
    /// </summary>
    private static LoadResult LoadDirectory(string path)
    {
        var items = new List<FileItem>();
        int skipped = 0;
        Exception? firstSkip = null;

        // UNC server path (\\server) — enumerate network shares via NetShareEnum
        if (FileSystemService.IsUncServerPath(path))
            return new LoadResult(LoadShares(path), 0);

        var dirInfo = new DirectoryInfo(path);

        if (!dirInfo.Exists)
            throw new DirectoryNotFoundException($"ディレクトリが見つかりません: {path}");

        // Directories first
        foreach (var dir in dirInfo.EnumerateDirectories())
        {
            try
            {
                var (icon, typeName) = IconHelper.GetIconAndType(dir.FullName, true);
                items.Add(new FileItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    LastModified = dir.LastWriteTime,
                    IsDirectory = true,
                    Type = typeName,
                    Icon = icon,
                });
            }
            catch (Exception ex)
            {
                skipped++;
                firstSkip ??= ex;
            }
        }

        // Files
        foreach (var file in dirInfo.EnumerateFiles())
        {
            try
            {
                var (icon, typeName) = IconHelper.GetIconAndType(file.FullName, false);
                items.Add(new FileItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    LastModified = file.LastWriteTime,
                    Size = file.Length,
                    IsDirectory = false,
                    Type = typeName,
                    Icon = icon,
                });
            }
            catch (Exception ex)
            {
                skipped++;
                firstSkip ??= ex;
            }
        }

        if (skipped > 0)
            Log.Warn("FilePane.LoadDirectory", $"{skipped} entr(ies) skipped in {path}; first error shown", firstSkip);

        return new LoadResult(items, skipped);
    }

    /// <summary>The shares of \\server as folder entries (enumeration failure is thrown).</summary>
    private static List<FileItem> LoadShares(string serverPath)
    {
        var items = new List<FileItem>();
        foreach (var share in FileSystemService.GetShares(serverPath))
        {
            try
            {
                var (icon, _) = IconHelper.GetIconAndType(share.FullPath, true);
                items.Add(new FileItem
                {
                    Name = share.Name,
                    FullPath = share.FullPath,
                    LastModified = DateTime.MinValue,
                    IsDirectory = true,
                    Type = string.IsNullOrEmpty(share.Remark) ? "ネットワーク共有" : share.Remark,
                    Icon = icon,
                });
            }
            catch (Exception ex) { Log.Warn("FilePane.NetShares", share.FullPath, ex); }
        }
        return items;
    }

    public void SortByColumn(string column)
    {
        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));

        ApplySortToItems();
    }

    private void ApplySortToItems()
    {
        IEnumerable<FileItem> source = _allItems;
        if (!string.IsNullOrEmpty(FilterText))
            source = source.Where(i => i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        var dirs = source.Where(i => i.IsDirectory);
        var files = source.Where(i => !i.IsDirectory);

        dirs = ApplySortOrder(dirs);
        files = ApplySortOrder(files);

        var sorted = dirs.Concat(files).ToList();

        Items.Clear();
        foreach (var item in sorted)
            Items.Add(item);
    }

    private IEnumerable<FileItem> ApplySortOrder(IEnumerable<FileItem> items)
    {
        return _sortColumn switch
        {
            "Name" => _sortAscending
                ? items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "LastModified" => _sortAscending
                ? items.OrderBy(i => i.LastModified)
                : items.OrderByDescending(i => i.LastModified),
            "Size" => _sortAscending
                ? items.OrderBy(i => i.Size)
                : items.OrderByDescending(i => i.Size),
            "Type" => _sortAscending
                ? items.OrderBy(i => i.Type, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(i => i.Type, StringComparer.OrdinalIgnoreCase),
            _ => items
        };
    }

    public async Task OnItemDoubleClicked(FileItem item)
    {
        await OpenItem(item);
    }

    public async Task OnAddressBarEnter()
    {
        await NavigateToAsync(AddressText);
    }
}
