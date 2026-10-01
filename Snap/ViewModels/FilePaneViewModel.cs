using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snap.Helpers;
using Snap.Models;
using Snap.Services;

namespace Snap.ViewModels;

public partial class FilePaneViewModel : ObservableObject, IDisposable
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
    public void SetSelection(System.Collections.IList selectedItems)
    {
        SelectedItems = SelectedInListOrder(selectedItems);
        SelectionSummary = BuildSelectionSummary(SelectedItems);
        ShowCountStatus();
    }

    /// <summary>
    /// The selection for the status bar (#17): "3 件選択 / 12.4 MB". Folders are not counted in
    /// the size: "3 件選択（うちフォルダー 1） / 12.4 MB", and only folders gives no size.
    /// In the PC view just "1 件選択". Empty when nothing is selected.
    /// </summary>
    [ObservableProperty]
    private string _selectionSummary = string.Empty;

    private string BuildSelectionSummary(IReadOnlyList<FileItem> items)
    {
        if (items.Count == 0) return string.Empty;
        var text = $"{items.Count} 件選択";
        if (CurrentPath == PcViewPath) return text;

        var folders = items.Count(i => i.IsDirectory);
        if (folders > 0) text += $"（うちフォルダー {folders}）";
        if (folders < items.Count)
            text += $" / {FileItem.FormatSize(items.Where(i => !i.IsDirectory).Sum(i => i.Size))}";
        return text;
    }

    // ==================== Host (#15) ====================
    // Set by MainViewModel when the tab joins a pane.

    /// <summary>Confirmations and the rename dialog.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Called after a file operation with the folders it changed (source and target);
    /// refreshes every pane showing one of them (MainViewModel.RefreshPanesShowing).</summary>
    public Func<IReadOnlyList<string>, Task>? FoldersChanged { get; set; }

    /// <summary>The view should select and scroll to this entry (after rename / new folder).</summary>
    public event Action<FileItem>? RevealRequested;


    /// <summary>The rows shown (filtered and sorted). Swapped in one step (#16), never row by row.</summary>
    public BulkObservableCollection<FileItem> Items { get; } = new();

    /// <summary>Raised right before <see cref="Items"/> is swapped while staying in the same folder
    /// (sort, refresh, folder watcher): the view remembers its scroll position and focused row.</summary>
    public event Action? ItemsReplacing;

    /// <summary>Raised right after that swap with the entries that were selected before (by path),
    /// for the view to select again and restore its scroll position.</summary>
    public event Action<IReadOnlyList<FileItem>>? ItemsReplaced;

    // All items before filtering
    private List<FileItem> _allItems = new();

    // Unreadable entries skipped by the last full load (kept in the status line).
    private int _skipped;

    // Folder watcher of this tab (#16) and the icon fill of the current listing.
    private readonly FolderWatcher _watcher;
    private CancellationTokenSource _iconCts = new();
    private bool _disposed;

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
        ApplySortToItems(keepView: false);
        UpdateItemCountStatus();
    }

    /// <summary>Ctrl+Shift+F: shows the filter bar (the view focuses it).</summary>
    public void ShowFilter() => IsFilterVisible = true;

    /// <summary>Esc in the filter bar: hides it and shows every entry again.</summary>
    public void ClearFilter()
    {
        IsFilterVisible = false;
        FilterText = string.Empty;
    }

    /// <param name="skipped">Unreadable entries of a new full load; null keeps the last count.</param>
    private void UpdateItemCountStatus(int? skipped = null)
    {
        if (skipped is int s) _skipped = s;
        var count = string.IsNullOrEmpty(FilterText)
            ? $"{Items.Count} 項目"
            : $"{Items.Count} / {_allItems.Count(IsListed)} 項目（絞り込み: {FilterText}）";
        _countText = _skipped > 0 ? $"{count}（読めない項目 {_skipped} 件を省略）" : count;
        ShowCountStatus();
    }

    // ==================== Status line (#17) ====================
    // StatusMessage shows either the count line ("N 項目" plus the selection) or a message (an
    // operation result, an error). A message is kept for MessageHold before the count line comes
    // back, so a refresh or the folder watcher right after "貼り付けました" does not wipe it out.

    private static readonly TimeSpan MessageHold = TimeSpan.FromSeconds(5);
    private string _countText = string.Empty;
    private System.Windows.Threading.DispatcherTimer? _messageTimer;
    private bool _settingCountStatus;

    /// <summary>The count line: "120 項目　　3 件選択 / 12.4 MB".</summary>
    private string ComposeCountStatus() =>
        string.IsNullOrEmpty(SelectionSummary) ? _countText : $"{_countText}　　{SelectionSummary}";

    /// <summary>Shows the count line, unless a message is being held.</summary>
    private void ShowCountStatus()
    {
        if (_messageTimer?.IsEnabled == true) return;
        SetCountStatus(ComposeCountStatus());
    }

    /// <summary>Sets <see cref="StatusMessage"/> without it being held as a message.</summary>
    private void SetCountStatus(string text)
    {
        _settingCountStatus = true;
        try { StatusMessage = text; }
        finally { _settingCountStatus = false; }
    }

    /// <summary>Drops a held message (a navigation the user started shows its own state at once).</summary>
    private void EndHeldMessage() => _messageTimer?.Stop();

    partial void OnStatusMessageChanged(string value)
    {
        if (_settingCountStatus || _disposed) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(HoldMessage);
            return;
        }
        HoldMessage();
    }

    private void HoldMessage()
    {
        if (_messageTimer == null)
        {
            _messageTimer = new System.Windows.Threading.DispatcherTimer { Interval = MessageHold };
            _messageTimer.Tick += (_, _) =>
            {
                _messageTimer.Stop();
                if (!_disposed) SetCountStatus(ComposeCountStatus());
            };
        }
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    // Sorting state
    private string _sortColumn = "Name";
    private bool _sortAscending = true;

    public string SortColumn => _sortColumn;
    public bool SortAscending => _sortAscending;

    /// <summary>The columns a tab can be sorted by (also the keys of the saved column widths).</summary>
    public static readonly IReadOnlyList<string> SortColumns = ["Name", "LastModified", "Size", "Type"];

    /// <summary>Restores a saved sort (#17) before the tab is first listed. Unknown or null values
    /// keep the default (Name, ascending).</summary>
    public void RestoreSort(string? column, bool? ascending)
    {
        if (column != null && SortColumns.Contains(column))
            _sortColumn = column;
        if (ascending is bool asc)
            _sortAscending = asc;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
    }

    /// <summary>True when the sort is the default one (not written to settings).</summary>
    public bool HasDefaultSort => _sortColumn == "Name" && _sortAscending;

    /// <summary>Hidden files were switched on / off (Ctrl+H, #17): the rows are filtered again
    /// in place (selection and scroll position stay).</summary>
    public void ApplyShowHidden()
    {
        if (_disposed) return;
        ApplySortToItems(keepView: true);
        UpdateItemCountStatus();
    }

    /// <summary>Listed under the current hidden-files setting.</summary>
    private static bool IsListed(FileItem item) => !item.IsHidden || ViewOptions.ShowHidden;

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
        // The PC view is a valid start (a saved PC tab, a copy of a PC tab, #23); any other path
        // must exist, else C:\.
        if (string.Equals(initialPath?.Trim(), PcViewPath, StringComparison.OrdinalIgnoreCase))
            initialPath = PcViewPath;
        else if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
            initialPath = @"C:\";
        CurrentPath = initialPath;
        AddressText = initialPath;
        _watcher = new FolderWatcher((folder, names, reloadAll) =>
            OnFolderChangedAsync(folder, names, reloadAll).SafeFireAndForget("FilePane.Watcher", "フォルダーの変更を反映できません"));
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
        var timing = Stopwatch.StartNew();

        IsLoading = true;
        // A navigation shows its own state at once (a message still held from before is dropped).
        EndHeldMessage();
        SetCountStatus("読み込み中...");

        // The folder is watched before it is read (#23): changes made while it is enumerated are
        // collected and applied once the new listing is in place (Release in finally).
        _watcher.Watch(path);
        _watcher.Hold();
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
                // Same folder (F5, after a file operation): keep the selection and scroll position.
                ApplySortToItems(keepView: !pathChanged);
                if (pathChanged)
                {
                    // The view clears the selection of a new folder; do not show the old one meanwhile.
                    SelectedItems = [];
                    SelectionSummary = string.Empty;
                }
                CurrentPath = path;
                if (pathChanged || !HasCustomTabHeader)
                    ResetTabHeader();
                UpdateItemCountStatus(load.Skipped);
                // Text is on screen now; real folder / drive icons follow from the icon worker (#16).
                StartIconFill(items, path, timing);
            });
            LogShownTiming(path, items.Count, timing);

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
            // Only the latest navigation clears the loading state. When it failed, the tab still
            // shows its previous folder: that one is watched again.
            if (IsCurrent())
            {
                IsLoading = false;
                _watcher.Watch(CurrentPath);
            }
            _watcher.Release();
        }
    }

    /// <summary>Measurement (#16): logs how long the list took until its text was on screen
    /// (the Loaded-priority callback runs after layout and render of the new rows).
    /// Debug builds only (#23): a Release build does not log every navigation.</summary>
    [Conditional("DEBUG")]
    private static void LogShownTiming(string path, int count, Stopwatch timing)
    {
        var applied = timing.ElapsedMilliseconds;
        Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var sinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            Log.Info("FilePane.Timing",
                $"{path}: {count} items, applied {applied} ms, shown {timing.ElapsedMilliseconds} ms after navigate ({sinceStart:F0} ms after process start)");
        });
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
        IsStale = false;
        await NavigateCoreAsync(CurrentPath, addToHistory: false, historyIndex: null);
    }

    /// <summary>
    /// A background tab whose folder was changed by a file operation while no folder watcher
    /// covered it (network location, watcher could not start): reloaded when it comes to the
    /// front (TabPaneViewModel.OnSelectedTabChanged, #23).
    /// </summary>
    public bool IsStale { get; set; }

    /// <summary>True when this tab's folder watcher is running on <see cref="CurrentPath"/>,
    /// i.e. changes to the folder reach the list without a refresh (#16).</summary>
    public bool IsWatchingCurrentFolder =>
        _watcher.WatchedPath is { } watched && FileSystemService.SamePath(watched, CurrentPath);

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
    public static bool CanCreateHere(string dir) =>
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

    /// <summary>The selected entries in list order. One pass over <see cref="Items"/> builds the
    /// positions (#23: an IndexOf per selected entry was O(n²) for a large selection).</summary>
    private List<FileItem> SelectedInListOrder(System.Collections.IList? selectedItems)
    {
        var selected = (selectedItems?.OfType<FileItem>() ?? Enumerable.Empty<FileItem>())
            .Where(f => f.Name != "..")
            .ToList();
        if (selected.Count <= 1) return selected;

        var position = new Dictionary<FileItem, int>(Items.Count, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < Items.Count; i++) position.TryAdd(Items[i], i);
        return selected
            .OrderBy(f => position.TryGetValue(f, out var at) ? at : -1)
            .ToList();
    }

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
            var known = drive.FreeSpace.HasValue && drive.TotalSize > 0;
            items.Add(new FileItem
            {
                Name = drive.Label,
                FullPath = drive.RootPath,
                LastModified = DateTime.MinValue,
                Size = drive.TotalSize,
                IsDirectory = true,
                Type = drive.TypeName,
                Icon = icon,
                IsDrive = true,
                // Not ready / unreadable: "—" and no bar (#17).
                DriveSpaceText = known
                    ? $"空き {FileItem.FormatSize(drive.FreeSpace!.Value)} / {FileItem.FormatSize(drive.TotalSize)}"
                    : "—",
                DriveUsedPercent = known
                    ? Math.Clamp(100.0 * (drive.TotalSize - drive.FreeSpace!.Value) / drive.TotalSize, 0, 100)
                    : null,
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

        // Text first (#16): folders get the generic folder icon here and their own icon later
        // (StartIconFill); files get their extension's cached icon.
        var (folderIcon, folderType) = IconHelper.GetDefaultFolderIcon();

        // Directories first
        foreach (var dir in dirInfo.EnumerateDirectories())
        {
            try
            {
                var (icon, typeName) = (folderIcon, folderType);
                items.Add(new FileItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    LastModified = dir.LastWriteTime,
                    IsDirectory = true,
                    IsHidden = (dir.Attributes & FileAttributes.Hidden) != 0,
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
                var (icon, typeName) = IconHelper.GetFileTypeIcon(file.FullName);
                items.Add(new FileItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    LastModified = file.LastWriteTime,
                    Size = file.Length,
                    IsDirectory = false,
                    IsHidden = (file.Attributes & FileAttributes.Hidden) != 0,
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

    private void ApplySortToItems() => ApplySortToItems(keepView: true);

    /// <summary>The filtered, sorted rows (folders first).</summary>
    private List<FileItem> BuildSortedView()
    {
        IEnumerable<FileItem> source = _allItems.Where(IsListed);
        if (!string.IsNullOrEmpty(FilterText))
            source = source.Where(i => i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        var dirs = ApplySortOrder(source.Where(i => i.IsDirectory));
        var files = ApplySortOrder(source.Where(i => !i.IsDirectory));
        return dirs.Concat(files).ToList();
    }

    /// <summary>
    /// Swaps <see cref="Items"/> for the filtered, sorted rows in one step (#16).
    /// <paramref name="keepView"/>: the list still shows the same folder, so the view is asked to
    /// select the same entries again (matched by path) and to keep its scroll position.
    /// </summary>
    private void ApplySortToItems(bool keepView)
    {
        var sorted = BuildSortedView();
        if (!keepView)
        {
            Items.ReplaceAll(sorted);
            return;
        }

        var selectedPaths = SelectedItems.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ItemsReplacing?.Invoke();
        Items.ReplaceAll(sorted);
        IReadOnlyList<FileItem> reselect = selectedPaths.Count == 0
            ? []
            : sorted.Where(i => selectedPaths.Contains(i.FullPath)).ToList();
        ItemsReplaced?.Invoke(reselect);
    }

    /// <summary>
    /// Brings <see cref="Items"/> in line with the sorted view after a few entries changed
    /// (<paramref name="touched"/>: added or updated), with single inserts / moves / removes so
    /// the selection and scroll position stay. Rows that were not touched keep their relative
    /// order, so only touched rows move. Falls back to a full swap if that does not hold.
    /// </summary>
    private void SyncItemsToSorted(HashSet<FileItem> touched)
    {
        var sorted = BuildSortedView();
        var wanted = new HashSet<FileItem>(sorted, ReferenceEqualityComparer.Instance);

        for (int i = Items.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Items[i])) Items.RemoveAt(i);

        var present = new HashSet<FileItem>(Items, ReferenceEqualityComparer.Instance);
        int ops = 0;
        int k = 0;
        while (k < sorted.Count)
        {
            var want = sorted[k];
            if (k < Items.Count && ReferenceEquals(Items[k], want)) { k++; continue; }
            if (++ops > 400) break;

            if (k < Items.Count && touched.Contains(Items[k]))
            {
                // A touched row that belongs further down: take it out, it is put back when reached.
                present.Remove(Items[k]);
                Items.RemoveAt(k);
                continue;
            }
            if (!present.Contains(want))
            {
                Items.Insert(k, want);
                present.Add(want);
                k++;
                continue;
            }
            if (!touched.Contains(want)) { ops = int.MaxValue; break; } // order assumption broken
            var j = Items.IndexOf(want);
            Items.Move(j, k);
            k++;
        }

        if (ops > 400 || Items.Count != sorted.Count)
        {
            Log.Info("FilePane.Sync", $"incremental update gave up after {ops} ops; full swap");
            ApplySortToItems(keepView: true);
        }
    }

    // ==================== Folder watcher (#16) ====================

    /// <summary>
    /// A batch from this tab's <see cref="FolderWatcher"/>: re-reads each changed name and adds,
    /// updates or removes its row; <paramref name="reloadAll"/> (many changes or a lost event
    /// buffer) reloads the whole folder in place. Ignored when the tab has moved on meanwhile.
    /// </summary>
    private async Task OnFolderChangedAsync(string folder, IReadOnlyCollection<string> names, bool reloadAll)
    {
        if (_disposed || !FileSystemService.SamePath(folder, CurrentPath)) return;
        if (reloadAll)
        {
            Log.Info("FilePane.Watcher", $"{folder}: many changes or lost events, full reload");
            await ReloadInPlaceAsync();
            return;
        }

        var gen = Volatile.Read(ref _navGeneration);
        var tracker = _usageTracker;
        var states = await Task.Run(() => names.Select(n => (Name: n, State: ReadEntry(folder, n, tracker))).ToList());
        if (_disposed || gen != Volatile.Read(ref _navGeneration) || !FileSystemService.SamePath(folder, CurrentPath))
            return;

        var byName = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allItems) byName.TryAdd(item.Name, item);

        var touched = new HashSet<FileItem>(ReferenceEqualityComparer.Instance);
        var removed = 0;
        var newFolders = new List<FileItem>();
        foreach (var (name, state) in states)
        {
            if (!state.Ok) continue; // could not be read now: leave the row as it is
            byName.TryGetValue(name, out var existing);
            var fresh = state.Item;

            if (fresh == null)
            {
                if (existing != null && _allItems.Remove(existing)) removed++;
                continue;
            }

            if (existing != null && existing.IsDirectory == fresh.IsDirectory
                && string.Equals(existing.Name, fresh.Name, StringComparison.Ordinal))
            {
                // Same entry: update in place (the row and its selection stay).
                if (existing.LastModified != fresh.LastModified || existing.Size != fresh.Size
                    || existing.IsHidden != fresh.IsHidden)
                {
                    existing.LastModified = fresh.LastModified;
                    existing.Size = fresh.Size;
                    existing.IsHidden = fresh.IsHidden;
                    touched.Add(existing);
                }
                continue;
            }

            // New entry, or the name's case / kind changed: a new row.
            if (existing != null) _allItems.Remove(existing);
            _allItems.Add(fresh);
            byName[fresh.Name] = fresh;
            touched.Add(fresh);
            if (fresh.IsDirectory) newFolders.Add(fresh);
        }

        if (touched.Count == 0 && removed == 0) return;
        SyncItemsToSorted(touched);
        UpdateItemCountStatus();
        if (newFolders.Count > 0 && !FileSystemService.IsNetworkPath(folder))
            IconHelper.QueuePathIcons(newFolders, i => i.FullPath, SetIcon, _iconCts.Token);
    }

    private readonly record struct EntryState(bool Ok, FileItem? Item);

    /// <summary>
    /// The current state of <paramref name="name"/> in <paramref name="folder"/> (thread pool):
    /// a fresh row, null when it no longer exists, or not Ok when it could not be read.
    /// Looked up by enumeration so the row gets the name's real case.
    /// </summary>
    private static EntryState ReadEntry(string folder, string name, UsageTracker? tracker)
    {
        try
        {
            var info = new DirectoryInfo(folder).EnumerateFileSystemInfos(name)
                .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (info == null) return new EntryState(true, null);

            FileItem item;
            if (info is DirectoryInfo dir)
            {
                var (icon, typeName) = IconHelper.GetDefaultFolderIcon();
                item = new FileItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    LastModified = dir.LastWriteTime,
                    IsDirectory = true,
                    IsHidden = (dir.Attributes & FileAttributes.Hidden) != 0,
                    Type = typeName,
                    Icon = icon,
                };
            }
            else
            {
                var file = (FileInfo)info;
                var (icon, typeName) = IconHelper.GetFileTypeIcon(file.FullName);
                item = new FileItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    LastModified = file.LastWriteTime,
                    Size = file.Length,
                    IsDirectory = false,
                    IsHidden = (file.Attributes & FileAttributes.Hidden) != 0,
                    Type = typeName,
                    Icon = icon,
                };
            }
            if (tracker != null) item.FrequencyLevel = tracker.GetFrequencyLevel(item.FullPath);
            return new EntryState(true, item);
        }
        catch (DirectoryNotFoundException)
        {
            return new EntryState(true, null);
        }
        catch (Exception ex)
        {
            Log.Warn("FilePane.Watcher", $"cannot read {Path.Combine(folder, name)}", ex);
            return new EntryState(false, null);
        }
    }

    /// <summary>
    /// Reloads the folder shown without being a navigation: no "読み込み中", no history entry, and
    /// it never cancels a navigation the user started (it is dropped if one starts meanwhile).
    /// Selection and scroll position stay.
    /// </summary>
    private async Task ReloadInPlaceAsync()
    {
        var path = CurrentPath;
        var gen = Volatile.Read(ref _navGeneration);
        var tracker = _usageTracker;
        // Changes during the re-read wait until the new listing is in place (#23).
        _watcher.Hold();
        try
        {
            var load = await Task.Run(() => path == PcViewPath ? LoadDrives() : LoadDirectory(path));
            if (_disposed || gen != Volatile.Read(ref _navGeneration) || !FileSystemService.SamePath(path, CurrentPath))
                return;
            if (tracker != null)
                foreach (var item in load.Items)
                    item.FrequencyLevel = tracker.GetFrequencyLevel(item.FullPath);
            _allItems = load.Items;
            ApplySortToItems(keepView: true);
            UpdateItemCountStatus(load.Skipped);
            StartIconFill(load.Items, path, timing: null);
        }
        catch (DirectoryNotFoundException ex)
        {
            Log.Warn("FilePane.Reload", $"folder gone: {path}", ex);
            if (FileSystemService.SamePath(path, CurrentPath))
                StatusMessage = $"フォルダーが見つかりません（削除または移動された可能性があります）: {path}";
        }
        catch (Exception ex)
        {
            Log.Warn("FilePane.Reload", path, ex);
        }
        finally
        {
            _watcher.Release();
        }
    }

    // ==================== Icons after the text (#16) ====================

    private static void SetIcon(FileItem item, ImageSource icon) => item.Icon = icon;

    /// <summary>
    /// Queues the real icons of the listed folders on the icon worker (the rows already show the
    /// generic folder icon). The previous listing's pending icons are dropped. Not done for the PC
    /// view (drive icons are read with the list) or network locations (no network access).
    /// </summary>
    private void StartIconFill(List<FileItem> items, string path, Stopwatch? timing)
    {
        if (_disposed) return; // a load that finished after the tab was closed
        _iconCts.Cancel();
        _iconCts.Dispose();
        _iconCts = new CancellationTokenSource();

        if (path == PcViewPath || FileSystemService.IsNetworkPath(path)) return;
        var folders = items.Where(i => i.IsDirectory).ToList();
        if (folders.Count == 0) return;

        Action? completed = null;
#if DEBUG
        // Measurement (#16), Debug builds only (#23).
        if (timing != null)
            completed = () => Log.Info("FilePane.Timing", $"{path}: icons of {folders.Count} folders filled {timing.ElapsedMilliseconds} ms after navigate");
#endif
        IconHelper.QueuePathIcons(folders, i => i.FullPath, SetIcon, _iconCts.Token, completed);
    }

    /// <summary>Tab closed: stops the folder watcher and pending icon work.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messageTimer?.Stop();
        _watcher.Dispose();
        _iconCts.Cancel();
        _iconCts.Dispose();
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
