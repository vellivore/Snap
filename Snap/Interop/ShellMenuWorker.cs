using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Snap.Interop;

/// <summary>
/// Dedicated, long-lived STA thread that owns every shell COM object used for context menus
/// (IShellFolder / IContextMenu). Slow work such as QueryContextMenu (first load of shell
/// extension DLLs) and InvokeCommand runs here so the UI thread stays responsive.
/// COM objects created on this thread must only be touched from this thread.
/// </summary>
internal sealed class ShellMenuWorker
{
    private static readonly Lazy<ShellMenuWorker> _instance =
        new(() => new ShellMenuWorker(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static ShellMenuWorker Instance => _instance.Value;

    public Dispatcher Dispatcher { get; }

    private ShellMenuWorker()
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
            Name = "Snap.ShellMenuWorker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        Dispatcher = dispatcher!;

        // Shut the worker down together with the app.
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
        catch (Exception ex) { Snap.Services.Log.Warn("ShellMenuWorker.Shutdown", "worker shutdown failed", ex); }
    }

    public bool IsWorkerThread => Dispatcher.CheckAccess();

    /// <summary>Runs <paramref name="func"/> on the worker thread and awaits its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (Dispatcher.HasShutdownStarted)
            return Task.FromException<T>(new OperationCanceledException("ShellMenuWorker is shut down"));
        return Dispatcher.InvokeAsync(func).Task;
    }

    /// <summary>Runs <paramref name="action"/> on the worker thread and awaits completion.</summary>
    public Task InvokeAsync(Action action)
    {
        if (Dispatcher.HasShutdownStarted)
            return Task.FromException(new OperationCanceledException("ShellMenuWorker is shut down"));
        return Dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>
    /// Synchronously runs <paramref name="func"/> on the worker, waiting at most <paramref name="timeout"/>.
    /// Returns false on timeout, shutdown, or exception (the exception is swallowed).
    /// Only the UI thread waits on the worker, never the reverse, so this cannot deadlock.
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

            var op = Dispatcher.InvokeAsync(func, DispatcherPriority.Send);
            if (op.Wait(timeout) != DispatcherOperationStatus.Completed)
            {
                op.Abort();
                return false;
            }
            result = op.Result;
            return true;
        }
        catch (Exception ex)
        {
            Snap.Services.Log.Warn("ShellMenuWorker.TryInvoke", "menu message forwarding failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Pre-loads shell extension DLLs by building (and discarding) a file context menu for
    /// this exe and a background menu for %TEMP%. Never shows UI; all errors are swallowed.
    /// Disabled when the environment variable SNAP_NO_WARMUP=1.
    /// </summary>
    public Task WarmUpAsync()
    {
        if (Environment.GetEnvironmentVariable("SNAP_NO_WARMUP") == "1")
        {
            ShellContextMenu.TimingLog("warmup: disabled (SNAP_NO_WARMUP=1)");
            return Task.CompletedTask;
        }

        return InvokeAsync(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    ShellContextMenu.WarmUpItemMenu(exe);
            }
            catch (Exception ex) { Snap.Services.Log.Warn("ShellMenuWorker.WarmUp", "item menu warm-up failed", ex); }
            try
            {
                var temp = Path.GetTempPath();
                if (!string.IsNullOrEmpty(temp) && Directory.Exists(temp))
                    ShellContextMenu.WarmUpBackgroundMenu(Path.TrimEndingDirectorySeparator(temp));
            }
            catch (Exception ex) { Snap.Services.Log.Warn("ShellMenuWorker.WarmUp", "background menu warm-up failed", ex); }
            ShellContextMenu.TimingLog($"warmup: done in {sw.ElapsedMilliseconds} ms");
        });
    }
}
