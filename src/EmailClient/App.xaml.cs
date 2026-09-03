using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using EmailClient.Diagnostics;

namespace EmailClient;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    private MainWindow? _window;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything else — a toast notification shown before this runs would register under
        // whatever generic identity Windows infers instead of the app's own.
        UI.AppIdentity.Apply();

        Log.WriteHeader();

        // Ctrl+Shift+S: drag-select a screen region, copy to clipboard — Brave's page-screenshot
        // gesture. A class handler rather than wiring each window individually: it applies to every
        // Window subclass the app creates (MainWindow, ComposeWindow, AccountWindow, the viewers),
        // so the shortcut works no matter which of the app's windows currently has focus, and any
        // new window type added later gets it for free with no extra wiring.
        System.Windows.EventManager.RegisterClassHandler(
            typeof(System.Windows.Window), System.Windows.UIElement.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler(OnGlobalPreviewKeyDown));

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        // A crash on a background thread kills the process outright, so this is the only chance to
        // record why — the UI-thread handler above never sees it.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception on a background thread", args.ExceptionObject as Exception);
        // An async void / fire-and-forget task that throws is otherwise completely silent.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirstInstance)
        {
            // Purplemail lives in the tray, so its window is often hidden — re-launching it is the
            // obvious way to try to get it back. Hand that intent to the instance that can act on
            // it and quit, rather than starting a second client that would fight this one over the
            // IDLE connection and settings.json.
            Log.Info("Another instance is already running — asking it to surface and exiting.");
            _instance.SignalFirstInstance();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        // The window is created here rather than through StartupUri so the duplicate-launch case
        // above can bail out before any of it exists.
        var startInTray = e.Args.Any(a =>
            a.Equals(Settings.StartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow { StartInTray = startInTray };
        MainWindow = _window;
        // Minimized before the first Show so a start-in-tray launch never paints a window that
        // immediately disappears again; MainWindow_Loaded hides it for real once the tray exists.
        if (startInTray)
            _window.WindowState = System.Windows.WindowState.Minimized;
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(() => _window?.RestoreFromTray()));
        _window.Show();
    }

    private static void OnGlobalPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.S
            && System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
        {
            e.Handled = true;
            // The class handler's sender is the specific Window instance the key was pressed in —
            // exactly the one to screenshot, whichever of the app's windows currently has focus.
            UI.ScreenshotTool.Capture(sender as System.Windows.Window);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    // Without this, any unhandled exception on the UI thread — even from a single button click —
    // silently kills the entire app (and its tray icon), which then looks like "everything is
    // broken" rather than "one thing failed."
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The log gets the full chain and stack; the dialog gets a sentence. Previously neither
        // existed — the message box showed a bare ex.Message and the detail was gone forever.
        Log.Error("Unhandled exception on the UI thread", e.Exception);

        // No single "current window" for a handler that can fire from anywhere — whichever window
        // last had focus is the least surprising owner for the dialog to appear on top of.
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>()
            .FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current.MainWindow;

        string? choice;
        if (owner is not null)
        {
            choice = UI.ConfirmDialog.Show(owner, "Unexpected error",
                $"Something went wrong: {e.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                "Purplemail is still running. Details have been saved to the log — open it?",
                warningIcon: true, new UI.ConfirmChoice("Dismiss"), new UI.ConfirmChoice("Open log"));
        }
        else
        {
            // No window to own a themed dialog against (e.g. this fires before any window ever
            // opened) — falling back to a plain MessageBox here beats letting the exception
            // handler itself throw trying to show something fancier.
            choice = System.Windows.MessageBox.Show(
                $"Something went wrong: {e.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                "Purplemail is still running. Details have been saved to the log — open it?",
                "Unexpected error", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
                == System.Windows.MessageBoxResult.Yes ? "Open log" : null;
        }

        if (choice == "Open log")
            OpenLog();

        e.Handled = true;
    }

    /// <summary>Opens the log in whatever handles .log (Notepad by default).</summary>
    public static void OpenLog()
    {
        try
        {
            if (System.IO.File.Exists(Log.FilePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't open the log file", ex);
        }
    }
}
