using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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


    public ObservableCollection<FileItem> Items { get; } = new();

    // All items before filtering
    private List<FileItem> _allItems = new();

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
                _allItems = items;
                ApplySortToItems();
                // A custom tab name is kept across refreshes / paste / delete (same folder)
                // and reset only when the tab moves to a different folder (#12).
                var pathChanged = !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase);
                CurrentPath = path;
                if (pathChanged || !HasCustomTabHeader)
                    ResetTabHeader();
                StatusMessage = load.Skipped > 0
                    ? $"{Items.Count} 項目（読めない項目 {load.Skipped} 件を省略）"
                    : $"{Items.Count} 項目";
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

        // PC ビュー（ドライブ一覧）には貼り付けできない
        if (string.Equals(destDir, PcViewPath, StringComparison.OrdinalIgnoreCase))
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

            StatusMessage = result.Success
                ? (isCut ? "移動しました" : "貼り付けました")
                : result.Aborted
                    ? "操作がキャンセルされました"
                    : $"貼り付けエラー: {result.Error}";

            await Refresh();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task DeleteItems(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var items = selectedItems.OfType<FileItem>()
            .Where(f => f.Name != "..")
            .ToList();

        if (items.Count == 0) return;

        var names = string.Join("\n", items.Select(f => f.Name));
        var result = MessageBox.Show(
            $"以下の {items.Count} 項目をごみ箱へ移動しますか？\n\n{names}\n\n※ ごみ箱が使えない場所（ネットワーク等）では完全に削除されます。",
            "ごみ箱へ移動",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        IsLoading = true;
        try
        {
            // Shell の IFileOperation で削除（権限が必要な対象は UAC 昇格、ごみ箱へ送る＝元に戻せる）
            var opResult = await Interop.ShellFileOperation.DeleteAsync(
                items.Select(i => i.FullPath).ToList());

            if (!opResult.Success && !opResult.Aborted)
                Log.Warn("FilePane.Delete", $"delete failed: {opResult.Error}");

            StatusMessage = opResult.Success
                ? $"{items.Count} 項目をごみ箱へ移動しました"
                : opResult.Aborted
                    ? "削除がキャンセルされました"
                    : $"削除エラー: {opResult.Error}";

            await Refresh();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task RenameItem(FileItem? item)
    {
        if (item == null || item.Name == "..") return;

        // Show rename dialog
        var dialog = new Views.RenameDialog(item.Name)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true) return;

        var newName = dialog.NewName;
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

        try
        {
            var dir = Path.GetDirectoryName(item.FullPath)!;
            var newPath = Path.Combine(dir, newName);

            await Task.Run(() =>
            {
                if (item.IsDirectory)
                {
                    Directory.Move(item.FullPath, newPath);
                }
                else
                {
                    File.Move(item.FullPath, newPath);
                }
            });

            StatusMessage = $"名前を変更しました: {newName}";
            await Refresh();
        }
        catch (Exception ex)
        {
            Log.Warn("FilePane.Rename", $"{item.FullPath} -> {newName}", ex);
            StatusMessage = $"名前変更エラー: {ex.Message}";
        }
    }

    public async Task ShowProperties(FileItem? item)
    {
        if (item == null || item.Name == "..") return;

        try
        {
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.FullPath}\"",
                    UseShellExecute = true
                };
                Process.Start(psi);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("FilePane.Properties", item.FullPath, ex);
            StatusMessage = $"プロパティを表示できません: {ex.Message}";
        }
    }

    private readonly record struct LoadResult(List<FileItem> Items, int Skipped);

    private static LoadResult LoadDrives()
    {
        var items = new List<FileItem>();
        int skipped = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var label = drive.IsReady && !string.IsNullOrEmpty(drive.VolumeLabel)
                    ? $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})"
                    : $"{drive.Name.TrimEnd('\\')}";
                var driveType = drive.DriveType switch
                {
                    DriveType.Fixed => "ローカル ディスク",
                    DriveType.Removable => "リムーバブル ディスク",
                    DriveType.Network => "ネットワーク ドライブ",
                    DriveType.CDRom => "CD/DVD ドライブ",
                    DriveType.Ram => "RAM ディスク",
                    _ => "ドライブ",
                };
                var size = drive.IsReady ? drive.TotalSize : 0;
                var (icon, _) = Helpers.IconHelper.GetIconAndType(drive.Name, true);

                items.Add(new FileItem
                {
                    Name = label,
                    FullPath = drive.Name,
                    LastModified = DateTime.MinValue,
                    Size = size,
                    IsDirectory = true,
                    Type = driveType,
                    Icon = icon,
                });
            }
            catch (Exception ex)
            {
                // Skip inaccessible drives
                skipped++;
                Log.Warn("FilePane.LoadDrives", drive.Name, ex);
            }
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
        if (IsUncServerPath(path))
            return new LoadResult(EnumerateNetworkShares(path), 0);

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

    // ==================== Network share enumeration ====================

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(
        string serverName, int level, out IntPtr bufPtr, int prefMaxLen,
        out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string shi1_netname;
        public uint shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string shi1_remark;
    }

    private const uint STYPE_DISKTREE = 0x00000000;
    private const uint STYPE_SPECIAL = 0x80000000;

    private static List<FileItem> EnumerateNetworkShares(string serverPath)
    {
        var items = new List<FileItem>();
        var server = serverPath.TrimEnd('\\');
        int resumeHandle = 0;

        int result = NetShareEnum(server, 1, out var bufPtr, -1,
            out int entriesRead, out _, ref resumeHandle);

        if (result != 0 || bufPtr == IntPtr.Zero)
            throw new DirectoryNotFoundException($"ネットワーク共有を列挙できません: {server} (エラーコード: {result})");

        try
        {
            var structSize = Marshal.SizeOf<SHARE_INFO_1>();
            var currentPtr = bufPtr;

            for (int i = 0; i < entriesRead; i++)
            {
                var shareInfo = Marshal.PtrToStructure<SHARE_INFO_1>(currentPtr);
                currentPtr = IntPtr.Add(currentPtr, structSize);

                // Skip hidden shares (ending with $) and non-disk shares
                if (shareInfo.shi1_netname.EndsWith('$')) continue;
                if ((shareInfo.shi1_type & ~STYPE_SPECIAL) != STYPE_DISKTREE) continue;

                var sharePath = $"{server}\\{shareInfo.shi1_netname}";
                try
                {
                    var (icon, typeName) = IconHelper.GetIconAndType(sharePath, true);
                    items.Add(new FileItem
                    {
                        Name = shareInfo.shi1_netname,
                        FullPath = sharePath,
                        LastModified = DateTime.MinValue,
                        IsDirectory = true,
                        Type = string.IsNullOrEmpty(shareInfo.shi1_remark) ? "ネットワーク共有" : shareInfo.shi1_remark,
                        Icon = icon,
                    });
                }
                catch (Exception ex) { Log.Warn("FilePane.NetShares", sharePath, ex); }
            }
        }
        finally
        {
            NetApiBufferFree(bufPtr);
        }

        return items;
    }

    /// <summary>UNC server path (\\server) without share name</summary>
    private static bool IsUncServerPath(string path)
    {
        if (!path.StartsWith(@"\\")) return false;
        var trimmed = path.TrimEnd('\\');
        // \\server has no additional backslash after the server name
        var afterPrefix = trimmed[2..];
        return !afterPrefix.Contains('\\');
    }

    private static string? GetUncParent(string uncPath)
    {
        var trimmed = uncPath.TrimEnd('\\');
        var lastSep = trimmed.LastIndexOf('\\');
        if (lastSep <= 1) return null;

        var parent = trimmed[..lastSep];
        var parts = parent.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        return parent;
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
        var dirs = _allItems.Where(i => i.IsDirectory);
        var files = _allItems.Where(i => !i.IsDirectory);

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
