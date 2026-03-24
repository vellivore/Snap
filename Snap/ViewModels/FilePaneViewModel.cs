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

    [ObservableProperty]
    private FileItem? _selectedItem;


    public ObservableCollection<FileItem> Items { get; } = new();

    // All items before filtering
    private List<FileItem> _allItems = new();

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

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        path = path.Trim();

        // Normalize path
        if (!path.StartsWith(@"\\"))
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                StatusMessage = $"パスが無効です: {ex.Message}";
                return;
            }
        }

        IsLoading = true;
        StatusMessage = "読み込み中...";

        try
        {
            var tracker = _usageTracker;
            var items = await Task.Run(() => LoadDirectory(path));

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
                Items.Clear();
                foreach (var item in items)
                {
                    Items.Add(item);
                }
                CurrentPath = path;
                TabHeader = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
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
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "アクセスが拒否されました。";
        }
        catch (DirectoryNotFoundException ex)
        {
            StatusMessage = $"ディレクトリが見つかりません: {path} ({ex.Message})";
        }
        catch (IOException ex)
        {
            StatusMessage = $"IOエラー: {ex.Message}";
        }
        catch (Exception ex)
        {
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

        IsLoading = true;
        var destDir = CurrentPath;
        int successCount = 0;
        int errorCount = 0;

        try
        {
            await Task.Run(() =>
            {
                foreach (var sourcePath in paths)
                {
                    try
                    {
                        var name = Path.GetFileName(sourcePath);
                        var destPath = Path.Combine(destDir, name);

                        // Handle name collision
                        destPath = GetUniqueDestPath(destPath);

                        if (Directory.Exists(sourcePath))
                        {
                            if (isCut)
                            {
                                Directory.Move(sourcePath, destPath);
                            }
                            else
                            {
                                CopyDirectoryRecursive(sourcePath, destPath);
                            }
                        }
                        else if (File.Exists(sourcePath))
                        {
                            if (isCut)
                            {
                                File.Move(sourcePath, destPath);
                            }
                            else
                            {
                                File.Copy(sourcePath, destPath);
                            }
                        }
                        successCount++;
                    }
                    catch
                    {
                        errorCount++;
                    }
                }
            });

            if (isCut)
            {
                _clipboardPaths = null;
            }

            StatusMessage = errorCount > 0
                ? $"{successCount} 項目を貼り付け、{errorCount} 項目でエラー"
                : $"{successCount} 項目を貼り付けました";

            await Refresh();
        }
        catch (Exception ex)
        {
            StatusMessage = $"貼り付けエラー: {ex.Message}";
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
            $"以下の {items.Count} 項目を削除しますか？\n\n{names}",
            "削除の確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        IsLoading = true;
        int successCount = 0;
        int errorCount = 0;

        try
        {
            await Task.Run(() =>
            {
                foreach (var item in items)
                {
                    try
                    {
                        if (item.IsDirectory)
                        {
                            Directory.Delete(item.FullPath, true);
                        }
                        else
                        {
                            File.Delete(item.FullPath);
                        }
                        successCount++;
                    }
                    catch
                    {
                        errorCount++;
                    }
                }
            });

            StatusMessage = errorCount > 0
                ? $"{successCount} 項目を削除、{errorCount} 項目でエラー"
                : $"{successCount} 項目を削除しました";

            await Refresh();
        }
        catch (Exception ex)
        {
            StatusMessage = $"削除エラー: {ex.Message}";
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
            StatusMessage = $"プロパティを表示できません: {ex.Message}";
        }
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
                catch
                {
                    // Skip inaccessible directories
                }
            }
        }
        catch
        {
            // Skip if enumeration fails
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
                catch
                {
                    // Skip inaccessible files
                }
            }
        }
        catch
        {
            // Skip if enumeration fails
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
                catch { }
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

    private static string GetUniqueDestPath(string destPath)
    {
        if (!File.Exists(destPath) && !Directory.Exists(destPath))
            return destPath;

        var dir = Path.GetDirectoryName(destPath)!;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(destPath);
        var ext = Path.GetExtension(destPath);

        int counter = 2;
        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{nameWithoutExt} ({counter}){ext}");
            counter++;
        } while (File.Exists(newPath) || Directory.Exists(newPath));

        return newPath;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
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
