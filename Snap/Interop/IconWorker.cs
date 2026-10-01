using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Snap.Interop;

/// <summary>
/// Dedicated, long-lived STA thread for shell icon lookups (SHGetFileInfo) and the icon bitmaps
/// made from them (#16). Same shape as <see cref="ShellMenuWorker"/>. Every bitmap leaves this
/// thread frozen, so any thread may use it. The UI thread never waits on this worker: the list
/// and the tree show their text first and get the icons posted back afterwards.
/// </summary>
internal sealed class IconWorker
{
    private static readonly Lazy<IconWorker> _instance =
        new(() => new IconWorker(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static IconWorker Instance => _instance.Value;

    public Dispatcher Dispatcher { get; }

    private IconWorker()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Snap.IconWorker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        Dispatcher = dispatcher!;

        var app = Application.Current;
        if (app != null)
        {
            app.Dispatcher.ShutdownStarted += (_, _) => Shutdown();
            app.Exit += (_, _) => Shutdown();
        }
    }

    private void Shutdown()
    {
        try
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.InvokeShutdown();
        }
        catch (Exception ex) { Snap.Services.Log.Warn("IconWorker.Shutdown", "worker shutdown failed", ex); }
    }

    public bool IsWorkerThread => Dispatcher.CheckAccess();

    /// <summary>Queues <paramref name="action"/> on the worker (fire and forget, errors logged).</summary>
    public void Post(Action action, DispatcherPriority priority = DispatcherPriority.Background)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(priority, () =>
        {
            try { action(); }
            catch (Exception ex) { Snap.Services.Log.Warn("IconWorker.Post", "icon job failed", ex); }
        });
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the worker and waits at most <paramref name="timeout"/>.
    /// For background threads only (the list loader): the UI thread must not call this.
    /// Runs inline when already on the worker. Returns false on timeout, shutdown or exception.
    /// </summary>
    public bool TryInvoke<T>(Func<T> func, TimeSpan timeout, out T? result)
    {
        result = default;
        try
        {
            if (Dispatcher.HasShutdownStarted) return false;
            if (Dispatcher.CheckAccess())
            {
                result = func();
                return true;
            }

            // Send: jumps ahead of queued folder-icon batches, so a list load is not held up by them.
            var op = Dispatcher.InvokeAsync(func, DispatcherPriority.Send);
            if (op.Wait(timeout) != DispatcherOperationStatus.Completed)
            {
                op.Abort();
                Snap.Services.Log.Warn("IconWorker.TryInvoke", $"timed out after {timeout.TotalMilliseconds:F0} ms");
                return false;
            }
            result = op.Result;
            return true;
        }
        catch (Exception ex)
        {
            Snap.Services.Log.Warn("IconWorker.TryInvoke", "icon lookup failed", ex);
            return false;
        }
    }
}
