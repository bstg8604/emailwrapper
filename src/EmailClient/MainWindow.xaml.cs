using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
    private readonly ICollectionView _messagesView;
    private readonly DispatcherTimer _refreshDebounce;
    private TrayIcon? _tray;
    private bool _isExiting;
    private string _currentFolder = "Inbox";
    private string _searchText = "";
    private InboxRow? _openRow;
    private MessageDetail? _openDetail;

    // TODO(polish): remove once DomBridge selectors are verified end-to-end against live webmail.
    // Seeds the UI with sample mail so layout/interaction can be reviewed without a real login.
    private static readonly bool UseMockData = true;

    public MainWindow()
    {
        InitializeComponent();
        MaximizeBoundsFix.Apply(this);

        _bridge = new DomBridge(_host);
        MessageList.ItemsSource = _messages;
        _messagesView = CollectionViewSource.GetDefaultView(_messages);
        _messagesView.Filter = FilterMessage;

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

    private string _quickFilter = "All";

    private bool FilterMessage(object obj)
    {
        if (obj is not InboxRow row)
            return true;

        if (!string.IsNullOrWhiteSpace(_searchText)
            && !row.Sender.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            && !row.Subject.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            return false;

        return _quickFilter switch
        {
            "Unread" => row.Unread,
            "Starred" => row.Starred,
            _ => true,
        };
    }

    private void QuickFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string filter } clicked)
            return;

        // Chips behave like a single-select group, not independent toggles.
        foreach (var chip in new[] { FilterAll, FilterUnread, FilterStarred })
            chip.IsChecked = ReferenceEquals(chip, clicked);

        _quickFilter = filter;
        _messagesView.Refresh();
        UpdateListEmptyState();
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

        var unread = MockData.InboxRows.Count(r => r.Unread);
        InboxUnreadCount.Text = unread.ToString();
        InboxUnreadBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (UseMockData)
        {
            LoadFolder("Inbox");
        }
        else
        {
            StatusText.Text = "Signing in…";
        }

        _host.LoggedIn += Host_LoggedIn;
        _host.PageMessageReceived += Host_PageMessageReceived;

        await _host.InitializeAsync();
        if (!UseMockData)
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

            UpdateListEmptyState();
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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (UseMockData)
        {
            // Re-loading from the static sample set would silently undo any star/delete/archive
            // done during this session, which looks like a bug rather than "nothing new arrived".
            StatusText.Text = $"{_messages.Count} messages (sample data, up to date)";
            return;
        }
        await RefreshInboxAsync();
    }

    /// <summary>
    /// Shows the hidden automation window and opens DevTools on the live webmail page, so real
    /// selectors can be inspected/verified against the authenticated site — see the "Known open
    /// risk" section in PLAN.md. Not needed once DomBridge's selectors are confirmed working.
    /// </summary>
    private void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host.Core is null)
        {
            StatusText.Text = "Automation page is still starting up — try again in a moment";
            return;
        }
        _host.Show();
        _host.Core.OpenDevToolsWindow();
    }

    private void ComposeButton_Click(object sender, RoutedEventArgs e) => OpenCompose();

    private void OpenCompose(string to = "", string subject = "", string body = "")
    {
        var compose = new ComposeWindow(_bridge, UseMockData, to, subject, body) { Owner = this };
        compose.ShowDialog();
    }

    // ---- Folders ------------------------------------------------------------------------

    private void FolderInbox_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectFolder("Inbox", FolderInbox);
    private void FolderSent_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectFolder("Sent", FolderSent);
    private void FolderDrafts_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectFolder("Drafts", FolderDrafts);
    private void FolderTrash_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectFolder("Trash", FolderTrash);
    private void FolderStarred_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectFolder("Starred", FolderStarred);

    private static readonly System.Windows.Media.Brush ActiveFolderBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xEE, 0xFC));

    private void SelectFolder(string folder, Border activeBorder)
    {
        foreach (var border in new[] { FolderInbox, FolderSent, FolderDrafts, FolderTrash, FolderStarred })
            border.Background = System.Windows.Media.Brushes.Transparent;
        activeBorder.Background = ActiveFolderBrush;

        if (!UseMockData)
        {
            StatusText.Text = "Only Inbox is wired to live automation right now";
            return;
        }

        LoadFolder(folder);
    }

    private void LoadFolder(string folder)
    {
        _currentFolder = folder;
        ResetReadingPane();

        // A leftover "Unread"/"Starred" quick filter from the previous folder could make the
        // new folder look empty for no visible reason — quick filters reset per folder.
        _quickFilter = "All";
        FilterAll.IsChecked = true;
        FilterUnread.IsChecked = false;
        FilterStarred.IsChecked = false;

        IReadOnlyList<InboxRow> rows = folder switch
        {
            "Sent" => MockData.SentRows,
            "Drafts" => MockData.DraftRows,
            "Trash" => MockData.TrashRows,
            "Starred" => [.. MockData.InboxRows.Concat(MockData.SentRows).Concat(MockData.DraftRows).Where(r => r.Starred)],
            _ => MockData.InboxRows,
        };

        _messages.Clear();
        foreach (var row in rows)
            _messages.Add(row);

        UpdateListEmptyState();
        StatusText.Text = $"{_messages.Count} messages (sample data)";
    }

    private void UpdateListEmptyState() =>
        ListEmptyState.Visibility = _messagesView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    // ---- Search ---------------------------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        _messagesView.Refresh();
        UpdateListEmptyState();
    }

    // ---- Star -------------------------------------------------------------------------------

    // ListBoxItem selection happens on mouse-down, before Click ever fires, so a star click
    // would also select/open the row underneath it unless the mouse-down is stopped here first.
    private void StarToggle_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        e.Handled = true;

    private void StarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: InboxRow row })
            return;
        var index = _messages.IndexOf(row);
        if (index < 0)
            return;
        _messages[index] = row with { Starred = !row.Starred };
        e.Handled = true;
    }

    // ---- Reading ------------------------------------------------------------------------

    private async void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not InboxRow row)
            return;

        MessageDetail? detail = UseMockData
            ? MockData.MessageBodies.GetValueOrDefault(row.Id)
            : await _bridge.OpenMessageAsync(row.Id);
        if (detail is null)
            return;

        await ReadingPane.EnsureCoreWebView2Async();

        _openRow = row;
        _openDetail = detail;

        EmptyState.Visibility = Visibility.Collapsed;
        ReadingCard.Visibility = Visibility.Visible;

        ReadingSubject.Text = detail.Subject;
        ReadingFrom.Text = detail.From;
        ReadingDate.Text = detail.Date;
        ReadingAvatarInitial.Text = row.Initial;
        ReadingPane.NavigateToString($"<html><body style='font-family:Segoe UI'>{detail.BodyHtml}</body></html>");
    }

    private void ResetReadingPane()
    {
        _openRow = null;
        _openDetail = null;
        EmptyState.Visibility = Visibility.Visible;
        ReadingCard.Visibility = Visibility.Collapsed;
        MessageList.SelectedItem = null;
    }

    private void ReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is null)
            return;
        OpenCompose(_openDetail.From, $"Re: {_openDetail.Subject}", "\n\n---- Original message ----\n");
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is null)
            return;
        OpenCompose("", $"Fwd: {_openDetail.Subject}", "\n\n---- Forwarded message ----\n");
    }

    private void ArchiveButton_Click(object sender, RoutedEventArgs e) => RemoveOpenMessage("Archived");

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => RemoveOpenMessage("Deleted");

    private void RemoveOpenMessage(string verb)
    {
        if (_openRow is null)
            return;
        RemoveMessage(_openRow, verb);
    }

    private void RemoveMessage(InboxRow row, string verb)
    {
        _messages.Remove(row);
        UpdateListEmptyState();
        StatusText.Text = $"{verb} (sample data)";
        if (ReferenceEquals(row, _openRow))
            ResetReadingPane();
    }

    // Delete-to-remove is a near-universal mail-client convention (Gmail, Outlook, Apple Mail).
    private void MessageList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete)
            return;
        if (MessageList.SelectedItem is not InboxRow row)
            return;
        RemoveMessage(row, "Deleted");
        e.Handled = true;
    }

    // ---- Window chrome --------------------------------------------------------------------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
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
