using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
    private bool _uiReady;
    private string _currentFolder = "Inbox";
    private string _searchText = "";
    private string _quickFilter = "All";
    private string _sortKey = "Date";
    private InboxRow? _openRow;
    private MessageDetail? _openDetail;
    private string? _blockedImagesHtml;
    private readonly HashSet<string> _checkedIds = [];

    // TODO(polish): remove once DomBridge selectors are verified end-to-end against live webmail.
    // Seeds the UI with sample mail so layout/interaction can be reviewed without a real login.
    private static readonly bool UseMockData = true;

    // Mutable per-session copies of the mock data — session-persistent so star/read/delete/archive
    // survive switching folders back and forth (the static MockData arrays are never mutated).
    private readonly Dictionary<string, List<InboxRow>> _folderData = new()
    {
        ["Inbox"] = [.. MockData.InboxRows],
        ["Sent"] = [.. MockData.SentRows],
        ["Drafts"] = [.. MockData.DraftRows],
        ["Trash"] = [.. MockData.TrashRows],
    };

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
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        _uiReady = true;
    }

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

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
            return;
        if (SortCombo.SelectedItem is not ComboBoxItem { Tag: string key })
            return;
        _sortKey = key;
        if (UseMockData)
            ApplyCurrentFolderView();
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

        if (UseMockData)
        {
            LoadFolder("Inbox");
            UpdateInboxBadge();
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
            // The session-persistent _folderData is already the source of truth — nothing to
            // silently revert, so refresh is just a status ping in sample-data mode.
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

    private void OpenCompose(string to = "", string subject = "", string body = "", string cc = "", string bcc = "",
        string? replacesDraftId = null)
    {
        var compose = new ComposeWindow(to, subject, body, cc, bcc) { Owner = this };
        compose.ShowDialog();

        if (replacesDraftId is not null)
            RemoveMessageEverywhere(replacesDraftId);

        if (compose.Result is { } result)
        {
            BeginUndoableSend(result);
            return;
        }

        // Closed without sending but with content typed — keep it as a draft rather than
        // silently discarding what the user wrote (Gmail/Outlook both auto-save drafts).
        if (compose.Draft is { } draft && draft.HasContent)
            AddLocalMessage("Drafts", draft, "Saved to Drafts");
        else if (replacesDraftId is not null)
            ApplyCurrentFolderView();
    }

    private int _localIdSeq;

    private void AddLocalMessage(string folder, ComposeResult message, string status)
    {
        var id = $"local{++_localIdSeq}";
        var row = new InboxRow(
            id,
            folder == "Drafts" ? "You" : message.To,
            string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject,
            message.Body.Replace("\r", " ").Replace("\n", " ").Trim(),
            DateTime.Now.ToString("h:mm tt"),
            Unread: false);

        _folderData[folder].Insert(0, row);
        _localBodies[id] = new MessageDetail(
            row.Subject,
            folder == "Drafts" ? "You <you@iitb.ac.in>" : $"To: {message.To}",
            "Just now",
            $"<p>{System.Net.WebUtility.HtmlEncode(message.Body).Replace("\n", "<br/>")}</p>",
            To: message.To,
            Cc: message.Cc);

        ApplyCurrentFolderView();
        StatusText.Text = $"{status} (sample data)";
    }

    private readonly Dictionary<string, MessageDetail> _localBodies = [];

    // ---- Undo send ----------------------------------------------------------------------

    private DispatcherTimer? _sendTimer;
    private ComposeResult? _pendingSend;

    // Gmail's signature feature: sending doesn't happen immediately — it's held for a few
    // seconds with an "Undo" option, then actually goes out once that window passes.
    private void BeginUndoableSend(ComposeResult result)
    {
        _pendingSend = result;
        SendSnackbar.Visibility = Visibility.Visible;

        _sendTimer?.Stop();
        _sendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _sendTimer.Tick += async (_, _) =>
        {
            _sendTimer!.Stop();
            SendSnackbar.Visibility = Visibility.Collapsed;
            var toSend = _pendingSend;
            _pendingSend = null;
            if (toSend is not null)
                await PerformSendAsync(toSend);
        };
        _sendTimer.Start();
    }

    private void UndoSend_Click(object sender, RoutedEventArgs e)
    {
        _sendTimer?.Stop();
        SendSnackbar.Visibility = Visibility.Collapsed;
        var toEdit = _pendingSend;
        _pendingSend = null;
        if (toEdit is not null)
            OpenCompose(toEdit.To, toEdit.Subject, toEdit.Body, toEdit.Cc, toEdit.Bcc);
    }

    private async Task PerformSendAsync(ComposeResult result)
    {
        try
        {
            if (UseMockData)
            {
                await Task.Delay(200); // simulate the round trip
            }
            else
            {
                await _bridge.StartComposeAsync();
                await _bridge.FillComposeAsync(result.To, result.Subject, result.Body, result.Cc, result.Bcc);
                await _bridge.ClickSendAsync();
            }
            AddLocalMessage("Sent", result, "Message sent");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Send failed: {ex.Message}";
        }
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
        ClearSelection();

        // A leftover "Unread"/"Starred" quick filter from the previous folder could make the
        // new folder look empty for no visible reason — quick filters reset per folder.
        _quickFilter = "All";
        FilterAll.IsChecked = true;
        FilterUnread.IsChecked = false;
        FilterStarred.IsChecked = false;

        ApplyCurrentFolderView();
        StatusText.Text = $"{_messages.Count} messages (sample data)";
    }

    private List<InboxRow> GetFolderRows(string folder) => folder switch
    {
        "Starred" => [.. _folderData.Values.SelectMany(rows => rows).Where(r => r.Starred)],
        _ => _folderData.TryGetValue(folder, out var rows) ? rows : [],
    };

    private void ApplyCurrentFolderView()
    {
        var source = GetFolderRows(_currentFolder);
        IEnumerable<InboxRow> ordered = _sortKey switch
        {
            "Sender" => source.OrderBy(r => r.Sender, StringComparer.OrdinalIgnoreCase),
            "Subject" => source.OrderBy(r => r.Subject, StringComparer.OrdinalIgnoreCase),
            _ => source,
        };

        _messages.Clear();
        foreach (var row in ordered)
            _messages.Add(row);

        UpdateListEmptyState();
    }

    private void UpdateListEmptyState() =>
        ListEmptyState.Visibility = _messagesView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateInboxBadge()
    {
        var unread = _folderData["Inbox"].Count(r => r.Unread);
        InboxUnreadCount.Text = unread.ToString();
        InboxUnreadBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Applies `updater` to the row with this id wherever it lives in `_folderData`, and mirrors
    /// the result into the currently visible `_messages` if it's shown there. Single source of
    /// truth per row, so a folder switch never "resets" a star/read change made earlier.
    /// </summary>
    private InboxRow? UpdateRow(string id, Func<InboxRow, InboxRow> updater)
    {
        InboxRow? updated = null;
        foreach (var rows in _folderData.Values)
        {
            var idx = rows.FindIndex(r => r.Id == id);
            if (idx < 0)
                continue;
            updated = updater(rows[idx]);
            rows[idx] = updated;
        }
        if (updated is null)
            return null;

        for (var i = 0; i < _messages.Count; i++)
        {
            if (_messages[i].Id == id)
            {
                _messages[i] = updated;
                break;
            }
        }
        return updated;
    }

    private void RemoveMessageEverywhere(string id)
    {
        foreach (var rows in _folderData.Values)
            rows.RemoveAll(r => r.Id == id);
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            if (_messages[i].Id == id)
                _messages.RemoveAt(i);
        }
    }

    // ---- Search ---------------------------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        _messagesView.Refresh();
        UpdateListEmptyState();
    }

    // ---- Star -------------------------------------------------------------------------------

    // ListBoxItem selection happens on mouse-down, before Click ever fires, so a star/checkbox
    // click would also select/open the row underneath it unless the mouse-down is stopped here.
    private void StarToggle_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        e.Handled = true;

    private void StarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: InboxRow row })
            return;

        UpdateRow(row.Id, r => r with { Starred = !r.Starred });

        // The Starred folder is a derived view — unstarring while looking at it must remove the
        // row outright, not just leave it visible-but-unstarred.
        if (_currentFolder == "Starred")
            ApplyCurrentFolderView();
        else
        {
            _messagesView.Refresh();
            UpdateListEmptyState();
        }
        e.Handled = true;
    }

    // ---- Multi-select / bulk actions --------------------------------------------------------

    private void RowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { Tag: InboxRow row } cb)
            return;
        if (cb.IsChecked == true)
            _checkedIds.Add(row.Id);
        else
            _checkedIds.Remove(row.Id);
        UpdateBulkBar();
        e.Handled = true;
    }

    private void UpdateBulkBar()
    {
        var count = _checkedIds.Count;
        BulkActionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QuickFilterBar.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
        BulkCountText.Text = $"{count} selected";
    }

    private void ClearSelection()
    {
        _checkedIds.Clear();
        UpdateBulkBar();
    }

    private void BulkMarkRead_Click(object sender, RoutedEventArgs e) =>
        BulkAction(id => UpdateRow(id, r => r with { Unread = false }), "Marked as read");

    private void BulkArchive_Click(object sender, RoutedEventArgs e) =>
        BulkAction(id => RemoveMessageEverywhere(id), "Archived");

    private void BulkDelete_Click(object sender, RoutedEventArgs e) =>
        BulkAction(id => RemoveMessageEverywhere(id), "Deleted");

    private void BulkClear_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void BulkAction(Action<string> perId, string verb)
    {
        foreach (var id in _checkedIds.ToList())
            perId(id);

        _checkedIds.Clear();
        UpdateBulkBar();
        ApplyCurrentFolderView();
        UpdateInboxBadge();
        StatusText.Text = $"{verb} (sample data)";
    }

    // ---- Reading ------------------------------------------------------------------------

    private static string WrapHtml(string bodyHtml) => $"<html><body style='font-family:Segoe UI'>{bodyHtml}</body></html>";

    private static string StripHtml(string html) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(html.Replace("<br/>", "\n"), "<[^>]+>", "")).Trim();

    private static readonly Regex RemoteImageSrc =
        new("""(<img\b[^>]*\bsrc\s*=\s*["'])(https?://[^"']+)(["'])""", RegexOptions.IgnoreCase);

    private const string TransparentPixel = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==";

    // Gmail/Outlook/Apple Mail all block remote images by default (they can be used to confirm
    // an email was opened, i.e. a tracking pixel) and offer a one-click "show images" bar.
    private static (string html, bool blocked) SanitizeRemoteImages(string html)
    {
        var blocked = false;
        var result = RemoteImageSrc.Replace(html, m =>
        {
            blocked = true;
            return $"{m.Groups[1].Value}{TransparentPixel}{m.Groups[3].Value}";
        });
        return (result, blocked);
    }

    private void ShowImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_blockedImagesHtml is null)
            return;
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
        ReadingPane.NavigateToString(WrapHtml(_blockedImagesHtml));
        _blockedImagesHtml = null;
    }

    private async void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not InboxRow row)
            return;

        // Opening a draft resumes editing it, rather than showing it as a read-only message.
        if (_currentFolder == "Drafts" && _localBodies.TryGetValue(row.Id, out var draftDetail))
        {
            MessageList.SelectedItem = null;
            OpenCompose(draftDetail.To, draftDetail.Subject, StripHtml(draftDetail.BodyHtml),
                draftDetail.Cc, replacesDraftId: row.Id);
            return;
        }

        MessageDetail? detail = _localBodies.GetValueOrDefault(row.Id)
            ?? (UseMockData
                ? MockData.MessageBodies.GetValueOrDefault(row.Id)
                : await _bridge.OpenMessageAsync(row.Id));
        if (detail is null)
            return;

        await ReadingPane.EnsureCoreWebView2Async();

        _openRow = UpdateRow(row.Id, r => r with { Unread = false }) ?? row;
        _openDetail = detail;
        UpdateInboxBadge();

        EmptyState.Visibility = Visibility.Collapsed;
        ReadingCard.Visibility = Visibility.Visible;

        ReadingSubject.Text = detail.Subject;
        ReadingFrom.Text = detail.From;
        ReadingDate.Text = detail.Date;
        ReadingAvatarInitial.Text = row.Initial;

        var attachments = detail.Attachments ?? [];
        AttachmentList.ItemsSource = attachments;
        AttachmentList.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var (safeHtml, hadRemoteImages) = SanitizeRemoteImages(detail.BodyHtml);
        _blockedImagesHtml = hadRemoteImages ? detail.BodyHtml : null;
        ImagesBlockedBar.Visibility = hadRemoteImages ? Visibility.Visible : Visibility.Collapsed;
        ReadingPane.NavigateToString(WrapHtml(safeHtml));
    }

    // Saves the attachment to a location the user picks. With sample data there's no real file
    // behind it, so a readable placeholder is written — the picker/save path is the real flow.
    private void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: MailAttachment attachment })
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = attachment.Name,
            Title = "Save attachment",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            if (UseMockData || string.IsNullOrEmpty(attachment.Url))
            {
                File.WriteAllText(dialog.FileName,
                    $"Sample attachment: {attachment.Name} ({attachment.Size})\r\n" +
                    "This placeholder stands in for the real file until live webmail data is wired up.\r\n");
            }
            else
            {
                _host.Core?.Navigate(attachment.Url); // Roundcube serves the real download
            }
            StatusText.Text = $"Saved {attachment.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't save: {ex.Message}";
        }
    }

    private void ResetReadingPane()
    {
        _openRow = null;
        _openDetail = null;
        _blockedImagesHtml = null;
        AttachmentList.ItemsSource = null;
        AttachmentList.Visibility = Visibility.Collapsed;
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
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

    private void ReplyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is null)
            return;
        var cc = string.Join(", ", new[] { _openDetail.To, _openDetail.Cc }.Where(s => !string.IsNullOrWhiteSpace(s)));
        OpenCompose(_openDetail.From, $"Re: {_openDetail.Subject}", "\n\n---- Original message ----\n", cc);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is null)
            return;
        OpenCompose("", $"Fwd: {_openDetail.Subject}", "\n\n---- Forwarded message ----\n");
    }

    private void MarkUnreadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRow is null)
            return;
        UpdateRow(_openRow.Id, r => r with { Unread = true });
        UpdateInboxBadge();
        _messagesView.Refresh();
        ResetReadingPane();
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
        RemoveMessageEverywhere(row.Id);
        UpdateListEmptyState();
        UpdateInboxBadge();
        StatusText.Text = $"{verb} (sample data)";
        if (_openRow is not null && _openRow.Id == row.Id)
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

    // ---- Keyboard shortcuts (Gmail-style) ---------------------------------------------------

    private bool _gPending;
    private DispatcherTimer? _gPendingTimer;

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ShortcutsOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key is System.Windows.Input.Key.Escape or System.Windows.Input.Key.OemQuestion)
            {
                ShortcutsOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }

        // Never hijack typing — only Escape (to clear/blur the search box) is handled while a
        // text field has focus.
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                SearchBox.Text = "";
                Keyboard.ClearFocus();
            }
            return;
        }

        // '?' (Shift+/) opens the shortcut cheat sheet; plain '/' focuses search.
        if (e.Key == System.Windows.Input.Key.OemQuestion)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                ShortcutsOverlay.Visibility = Visibility.Visible;
            else
                SearchBox.Focus();
            e.Handled = true;
            return;
        }

        // '#' (Shift+3) deletes, matching Gmail.
        if (e.Key == System.Windows.Input.Key.D3 && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (_openRow is not null)
                RemoveOpenMessage("Deleted");
            else if (MessageList.SelectedItem is InboxRow row)
                RemoveMessage(row, "Deleted");
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (_gPending)
        {
            _gPending = false;
            _gPendingTimer?.Stop();
            switch (e.Key)
            {
                case System.Windows.Input.Key.I: SelectFolder("Inbox", FolderInbox); break;
                case System.Windows.Input.Key.S: SelectFolder("Sent", FolderSent); break;
                case System.Windows.Input.Key.D: SelectFolder("Drafts", FolderDrafts); break;
                case System.Windows.Input.Key.T: SelectFolder("Trash", FolderTrash); break;
            }
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case System.Windows.Input.Key.C:
                OpenCompose();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.J:
                SelectAdjacentMessage(1);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.K:
                SelectAdjacentMessage(-1);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.U:
                ResetReadingPane();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.R:
                ReplyButton_Click(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.A:
                ReplyAllButton_Click(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F:
                ForwardButton_Click(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.E:
                if (_openRow is not null)
                    ArchiveButton_Click(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.G:
                _gPending = true;
                _gPendingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                _gPendingTimer.Tick += (_, _) => { _gPending = false; _gPendingTimer!.Stop(); };
                _gPendingTimer.Start();
                e.Handled = true;
                break;
        }
    }

    private void SelectAdjacentMessage(int delta)
    {
        var items = _messagesView.Cast<InboxRow>().ToList();
        if (items.Count == 0)
            return;
        var currentIndex = _openRow is null ? -1 : items.FindIndex(r => r.Id == _openRow.Id);
        var nextIndex = Math.Clamp(currentIndex + delta, 0, items.Count - 1);
        MessageList.SelectedItem = items[nextIndex];
        MessageList.ScrollIntoView(items[nextIndex]);
    }

    private void CloseShortcutsOverlay_Click(object sender, RoutedEventArgs e) =>
        ShortcutsOverlay.Visibility = Visibility.Collapsed;

    private void ShortcutsButton_Click(object sender, RoutedEventArgs e) =>
        ShortcutsOverlay.Visibility = Visibility.Visible;

    // ---- Window chrome --------------------------------------------------------------------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
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
