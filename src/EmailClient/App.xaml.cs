using System.Windows.Threading;

namespace EmailClient;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    // Without this, any unhandled exception on the UI thread — even from a single button click —
    // silently kills the entire app (and its tray icon), which then looks like "everything is
    // broken" rather than "one thing failed."
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}",
            "Unexpected error",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
        e.Handled = true;
    }
}
