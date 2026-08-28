using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EmailClient.Automation;
using EmailClient.Settings;
using EmailClient.UI;

namespace EmailClient;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AutomationHost _host = new();
    private readonly DomBridge _bridge;
    private readonly ObservableCollection<InboxRow> _messages = [];
    private readonly DispatcherTimer _refreshDebounce;
    private TrayIcon? _tray;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        _bridge = new DomBridge(_host);
        MessageList.ItemsSource = _messages;

        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshDebounce.Tick += async (_, _) =>
        {
            _refreshDebounce.Stop();
            await RefreshInboxAsync();
        };

        ApplyStoredBounds();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void ApplyStoredBounds()
    {
        Width = _settings.Width;
        Height = _settings.Height;
        if (_settings.Left is { } left && _settings.Top is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (_settings.Maximized)
            WindowState = WindowState.Maximized;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        _tray = new TrayIcon(this, iconPath);
        _tray.ExitRequested += (_, _) =>
        {
            _isExiting = true;
            Close();
            System.Windows.Application.Current.Shutdown();
        };

        StatusText.Text = "Signing in…";

        _host.LoggedIn += Host_LoggedIn;
        _host.PageMessageReceived += Host_PageMessageReceived;

        await _host.InitializeAsync();
        _host.Show();

        await ReadingPane.EnsureCoreWebView2Async();
    }

    private async void Host_LoggedIn(object? sender, EventArgs e)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            _host.Hide();
            StatusText.Text = "Connected";
            await _bridge.InstallChangeObserverAsync();
            await RefreshInboxAsync();
        });
    }

    private void Host_PageMessageReceived(object? sender, string json)
    {
        // A change in the live webmail DOM (new mail, a send completing, etc.) — debounce
        // and refresh rather than reacting to every single mutation event.
        Dispatcher.Invoke(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        });
    }

    private async Task RefreshInboxAsync()
    {
        try
        {
            var previousCount = _messages.Count;
            var rows = await _bridge.ListInboxAsync();

            _messages.Clear();
            foreach (var row in rows)
                _messages.Add(row);

            StatusText.Text = $"{_messages.Count} messages";

            if (previousCount > 0 && _messages.Count > previousCount)
            {
                var newest = _messages.FirstOrDefault();
                if (newest is not null)
                    _tray?.ShowBalloon("New mail", $"{newest.Sender}: {newest.Subject}");
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Refresh failed: {ex.Message}";
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshInboxAsync();

    /// <summary>
    /// Shows the hidden automation window and opens DevTools on the live webmail page, so real
    /// selectors can be inspected/verified against the authenticated site — see the "Known open
    /// risk" section in PLAN.md. Not needed once DomBridge's selectors are confirmed working.
    /// </summary>
    private void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        _host.Show();
        _host.Core.OpenDevToolsWindow();
    }

    private void ComposeButton_Click(object sender, RoutedEventArgs e)
    {
        var compose = new ComposeWindow(_bridge) { Owner = this };
        compose.ShowDialog();
    }

    private async void MessageList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not InboxRow row)
            return;

        var detail = await _bridge.OpenMessageAsync(row.Id);
        if (detail is null)
            return;

        ReadingSubject.Text = detail.Subject;
        ReadingFrom.Text = detail.From;
        ReadingPane.NavigateToString($"<html><body style='font-family:Segoe UI'>{detail.BodyHtml}</body></html>");
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Width = Width;
        _settings.Height = Height;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Save();

        if (_isExiting)
        {
            _tray?.Dispose();
            return;
        }

        // Closing the X button minimizes to tray instead of quitting.
        e.Cancel = true;
        Hide();
    }
}
