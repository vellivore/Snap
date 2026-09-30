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
        DispatcherUnhandledException += (_, args) =>
            Log.Error("App.DispatcherUnhandledException", "unhandled exception on UI thread", args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Log.Error("App.UnobservedTaskException", "unobserved task exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("App.UnhandledException", $"unhandled exception (terminating={args.IsTerminating})",
                args.ExceptionObject as Exception);

        Log.Info("App", $"start v{typeof(App).Assembly.GetName().Version}");
        base.OnStartup(e);
    }
}
