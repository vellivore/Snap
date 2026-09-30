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

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _tabHeader = "新しいタブ";

    /// <summary>True when user has set a custom tab name (not auto-generated from path).</summary>
    public bool HasCustomTabHeader { get; set; }

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
    private bool _navigatingFromHistory;

    // Clipboard state for cut/copy
    private static List<string>? _clipboardPaths;
    private static bool _clipboardIsCut;

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
    }

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
    public async Task NavigateToAsync(string path)
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

        IsLoading = true;
        StatusMessage = "読み込み中...";

        try
        {
            var tracker = _usageTracker;
            var items = await Task.Run(() => isPcView ? LoadDrives() : LoadDirectory(path));

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
                CurrentPath = path;
                // Reset custom tab name when navigating to a different directory
                if (HasCustomTabHeader)
                    HasCustomTabHeader = false;

                TabHeader = isPcView ? "PC"
                    : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                            is { Length: > 0 } name ? name : path;
                StatusMessage = $"{Items.Count} 項目";
            });

            // Update navigation history
            if (!_navigatingFromHistory)
            {
                // 同じパスなら履歴に追加しない
                if (_historyIndex >= 0 && _historyIndex < _history.Count
                    && string.Equals(_history[_historyIndex], path, StringComparison.OrdinalIgnoreCase))
                {
                    // skip
                }
                else
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
            _navigatingFromHistory = false;

            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            GoBackCommand.NotifyCanExecuteChanged();
            GoForwardCommand.NotifyCanExecuteChanged();
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn("FilePane.Navigate", $"access denied: {path}", ex);
            StatusMessage = "アクセスが拒否されました。";
        }
        catch (DirectoryNotFoundException ex)
        {
            Log.Warn("FilePane.Navigate", $"not found: {path}", ex);
            StatusMessage = $"ディレクトリが見つかりません: {path} ({ex.Message})";
        }
        catch (IOException ex)
        {
            Log.Warn("FilePane.Navigate", $"io error: {path}", ex);
            StatusMessage = $"IOエラー: {ex.Message}";
        }
        catch (Exception ex)
        {
            Log.Error("FilePane.Navigate", path, ex);
            StatusMessage = $"エラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    public async Task GoBack()
    {
        if (!CanGoBack) return;
        _historyIndex--;
        _navigatingFromHistory = true;
        await NavigateToAsync(_history[_historyIndex]);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    public async Task GoForward()
    {
        if (!CanGoForward) return;
        _historyIndex++;
        _navigatingFromHistory = true;
        await NavigateToAsync(_history[_historyIndex]);
    }

    [RelayCommand]
    public async Task Refresh()
    {
        _navigatingFromHistory = true;
        await NavigateToAsync(CurrentPath);
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
    public void CopyItems(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var paths = selectedItems.OfType<FileItem>()
            .Where(f => f.Name != "..")
            .Select(f => f.FullPath)
            .ToList();

        if (paths.Count == 0) return;

        _clipboardPaths = paths;
        _clipboardIsCut = false;

        // Also set Windows clipboard
        var fileDropList = new StringCollection();
        fileDropList.AddRange(paths.ToArray());
        Application.Current.Dispatcher.Invoke(() => Clipboard.SetFileDropList(fileDropList));

        StatusMessage = $"{paths.Count} 項目をコピーしました";
    }

    [RelayCommand]
    public void CutItems(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var paths = selectedItems.OfType<FileItem>()
            .Where(f => f.Name != "..")
            .Select(f => f.FullPath)
            .ToList();

        if (paths.Count == 0) return;

        _clipboardPaths = paths;
        _clipboardIsCut = true;

        // Also set Windows clipboard
        var fileDropList = new StringCollection();
        fileDropList.AddRange(paths.ToArray());
        Application.Current.Dispatcher.Invoke(() => Clipboard.SetFileDropList(fileDropList));

        StatusMessage = $"{paths.Count} 項目を切り取りました";
    }

    [RelayCommand]
    public async Task PasteItems()
    {
        List<string>? paths = _clipboardPaths;
        bool isCut = _clipboardIsCut;

        // Fallback to Windows clipboard
        if (paths == null || paths.Count == 0)
        {
            StringCollection? fileDropList = null;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Clipboard.ContainsFileDropList())
                {
                    fileDropList = Clipboard.GetFileDropList();
                }
            });

            if (fileDropList != null && fileDropList.Count > 0)
            {
                paths = fileDropList.Cast<string>().Where(s => s != null).ToList();
                isCut = false; // Can't determine from Windows clipboard
            }
        }

        if (paths == null || paths.Count == 0)
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

            if (isCut && result.Success)
                _clipboardPaths = null;

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

    private static List<FileItem> LoadDrives()
    {
        var items = new List<FileItem>();
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
                Log.Warn("FilePane.LoadDrives", drive.Name, ex);
            }
        }
        return items;
    }

    private static List<FileItem> LoadDirectory(string path)
    {
        var items = new List<FileItem>();

        // UNC server path (\\server) — enumerate network shares via NetShareEnum
        if (IsUncServerPath(path))
            return EnumerateNetworkShares(path);

        var dirInfo = new DirectoryInfo(path);

        if (!dirInfo.Exists)
            throw new DirectoryNotFoundException($"ディレクトリが見つかりません: {path}");

        // Directories first
        try
        {
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
                    // Skip inaccessible directories
                    Log.Warn("FilePane.LoadDirectory", $"skip dir: {dir.FullName}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            // Skip if enumeration fails
            Log.Warn("FilePane.LoadDirectory", $"directory enumeration failed: {path}", ex);
        }

        // Files
        try
        {
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
                    // Skip inaccessible files
                    Log.Warn("FilePane.LoadDirectory", $"skip file: {file.FullName}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            // Skip if enumeration fails
            Log.Warn("FilePane.LoadDirectory", $"file enumeration failed: {path}", ex);
        }

        return items;
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
        await NavigateToAsync(CurrentPath);
    }
}
