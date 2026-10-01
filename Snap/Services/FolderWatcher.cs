using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Snap.Services;

/// <summary>
/// Watches the one folder a tab shows (#16) and reports its changes to the UI thread in batches:
/// events are collected until 300 ms pass without a new one (at most 1 s after the first), then
/// handed over as the set of changed entry names. More than <see cref="FullReloadThreshold"/>
/// names, or a lost event buffer (<see cref="FileSystemWatcher.Error"/>), asks for a full reload
/// instead. Not used for the PC view or network locations (UNC, mapped drives).
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    /// <summary>More changed names than this in one batch: reload the whole folder.</summary>
    public const int FullReloadThreshold = 200;

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(1000);

    /// <summary>Called on the UI thread: (folder, changed names, reload everything).</summary>
    private readonly Action<string, IReadOnlyCollection<string>, bool> _onChanges;
    private readonly Dispatcher? _ui;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private FileSystemWatcher? _fsw;
    private string? _path;
    private HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _reloadAll;
    private bool _restart;
    private DateTime _firstPendingUtc;
    private bool _disposed;

    public FolderWatcher(Action<string, IReadOnlyCollection<string>, bool> onChanges)
    {
        _onChanges = onChanges;
        _ui = Application.Current?.Dispatcher;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The folder being watched, or null.</summary>
    public string? WatchedPath
    {
        get { lock (_gate) return _fsw != null ? _path : null; }
    }

    /// <summary>
    /// Watches <paramref name="path"/> (no-op when it is already watched). Null, the PC view and
    /// network locations stop watching. Failures are logged and leave the tab unwatched.
    /// </summary>
    public void Watch(string? path)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_fsw != null && FileSystemService.SamePath(_path, path)) return;
            StopLocked();

            if (string.IsNullOrEmpty(path)
                || path == ViewModels.FilePaneViewModel.PcViewPath
                || FileSystemService.IsNetworkPath(path))
                return;

            try
            {
                var fsw = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                fsw.Created += OnEntryEvent;
                fsw.Deleted += OnEntryEvent;
                fsw.Changed += OnEntryEvent;
                fsw.Renamed += OnRenamed;
                fsw.Error += OnError;
                fsw.EnableRaisingEvents = true;
                _fsw = fsw;
                _path = path;
            }
            catch (Exception ex)
            {
                Log.Warn("FolderWatcher.Watch", $"not watched: {path}", ex);
            }
        }
    }

    /// <summary>Stops watching (pending changes are dropped).</summary>
    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_fsw != null)
        {
            try
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Created -= OnEntryEvent;
                _fsw.Deleted -= OnEntryEvent;
                _fsw.Changed -= OnEntryEvent;
                _fsw.Renamed -= OnRenamed;
                _fsw.Error -= OnError;
                _fsw.Dispose();
            }
            catch (Exception ex) { Log.Warn("FolderWatcher.Stop", _path ?? "", ex); }
        }
        _fsw = null;
        _path = null;
        _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _reloadAll = false;
        _restart = false;
        _firstPendingUtc = default;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnEntryEvent(object sender, FileSystemEventArgs e) => Add(sender, e.Name, null);

    private void OnRenamed(object sender, RenamedEventArgs e) => Add(sender, e.OldName, e.Name);

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _fsw)) return;
            var ex = e.GetException();
            Log.Warn("FolderWatcher.Error", $"{_path}: full reload", ex);
            _reloadAll = true;
            // An overflow keeps the watcher running; anything else (folder gone, handle lost) may
            // have stopped it, so it is set up again on the next flush.
            _restart = ex is not InternalBufferOverflowException;
            ScheduleLocked();
        }
    }

    private void Add(object sender, string? name1, string? name2)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _fsw)) return; // a watcher that was replaced meanwhile
            if (!_reloadAll)
            {
                if (!string.IsNullOrEmpty(name1)) _pending.Add(name1);
                if (!string.IsNullOrEmpty(name2)) _pending.Add(name2);
                if (_pending.Count > FullReloadThreshold)
                {
                    _reloadAll = true;
                    _pending.Clear();
                }
            }
            ScheduleLocked();
        }
    }

    private void ScheduleLocked()
    {
        var now = DateTime.UtcNow;
        if (_firstPendingUtc == default) _firstPendingUtc = now;
        var untilMax = MaxDelay - (now - _firstPendingUtc);
        var due = untilMax < Quiet ? (untilMax < TimeSpan.Zero ? TimeSpan.Zero : untilMax) : Quiet;
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        string folder;
        IReadOnlyCollection<string> names;
        bool reloadAll;
        lock (_gate)
        {
            _firstPendingUtc = default;
            if (_disposed || _fsw == null || _path == null) return;
            if (_pending.Count == 0 && !_reloadAll) return;
            folder = _path;
            names = _pending;
            reloadAll = _reloadAll;
            _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _reloadAll = false;

            if (_restart)
            {
                _restart = false;
                var path = _path;
                StopLocked();
                if (Directory.Exists(path))
                {
                    // Watch() takes the (re-entrant) lock again.
                    Watch(path);
                }
                else
                {
                    Log.Info("FolderWatcher.Flush", $"folder gone, not watched any more: {path}");
                }
            }
        }

        if (_ui == null || _ui.HasShutdownStarted) return;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                // Dropped when the tab has moved to another folder since.
                if (_disposed) return;
                _onChanges(folder, names, reloadAll);
            }
            catch (Exception ex) { Log.Warn("FolderWatcher.Deliver", folder, ex); }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopLocked();
            _disposed = true;
        }
        _timer.Dispose();
    }
}
