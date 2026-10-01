using System.Threading.Tasks;
using System.Windows;
using Snap.Services;

namespace Snap;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // An exception on the UI thread is logged and shown in the status bar (Log.UserFacing →
        // MainViewModel.ShowStatus) and the app keeps running (#23). Only fatal ones end it.
        DispatcherUnhandledException += (_, args) =>
        {
            if (IsFatal(args.Exception))
            {
                Log.Error("App.DispatcherUnhandledException", "fatal exception on UI thread, terminating", args.Exception);
                return;
            }
            Log.UserError("App.DispatcherUnhandledException", "予期しないエラーが発生しました", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Log.Error("App.UnobservedTaskException", "unobserved task exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("App.UnhandledException", $"unhandled exception (terminating={args.IsTerminating})",
                args.ExceptionObject as Exception);

        Log.Info("App", $"start v{typeof(App).Assembly.GetName().Version}");
        base.OnStartup(e);
    }

    /// <summary>Exceptions after which the process state cannot be trusted: let them end the app.</summary>
    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or StackOverflowException or AccessViolationException
            or InsufficientExecutionStackException or System.Runtime.InteropServices.SEHException
        || (ex.InnerException != null && IsFatal(ex.InnerException));
}
