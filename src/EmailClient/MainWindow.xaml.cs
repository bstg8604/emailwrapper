using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EmailClient.Automation;
using EmailClient.Diagnostics;
using EmailClient.Mail;
using EmailClient.Settings;
using EmailClient.UI;

namespace EmailClient;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<InboxRow> _messages = [];
    private readonly ICollectionView _messagesView;
    private readonly DispatcherTimer _pollTimer;
    private TrayIcon? _tray;
    private readonly NewMailNotifier _notifier = new();

    /// <summary>
    /// Disk copy of what's already been fetched, so the list isn't blank while signing in and
    /// still shows something when the server can't be reached. Null until an account connects.
    /// </summary>
    private MessageCache? _cache;
    private bool _isExiting;
    private string _currentFolder = "Inbox";
    private string _searchText = "";
    private string _quickFilter = "All";
    private string _sortKey = "Date";
    private InboxRow? _openRow;
    private MessageDetail? _openDetail;
    private string? _blockedImagesHtml;
    private string? _meetingLinkUrl;

    // Keyed by message id — lets the appattach:// link handler (see ConfigureReadingPane) resolve
    // a clicked in-card attachment chip back to the real MailAttachment, without needing script
    // enabled in the reading pane to bridge HTML back to C#.
    private readonly Dictionary<string, IReadOnlyList<MailAttachment>> _conversationAttachments = new();
    // Every clickable inline body image, keyed by message id then index — same idea as
    // _conversationAttachments, since an <img src="..."> can't carry the click handler itself
    // (no script), only a link wrapped around it that this dictionary resolves back to a real src.
    private readonly Dictionary<string, List<string>> _conversationImages = new();
    private readonly HashSet<string> _checkedIds = [];

    // ---- Real account state ------------------------------------------------------------------
    private ImapMailBackend? _mail;
    private AccountSettings? _account;

    /// <summary>True until a real account is connected — the app always starts on sample data.</summary>
    private bool UseMockData => _mail is null;

    private MessagePage _page = MessagePage.Empty;
    private readonly List<MailFolder> _liveFolders = [];
    private IReadOnlyDictionary<string, string> _specialMailboxes = new Dictionary<string, string>();

    // Mutable per-session copies of the mock data — session-persistent so star/read/delete/archive
    // survive switching folders back and forth (the static MockData arrays are never mutated).
    private readonly Dictionary<string, List<InboxRow>> _folderData = new()
    {
        ["Inbox"] = [.. MockData.InboxRows],
        ["Sent"] = [.. MockData.SentRows],
        ["Drafts"] = [.. MockData.DraftRows],
        ["Archive"] = [.. MockData.ArchiveRows],
        ["Trash"] = [.. MockData.TrashRows],
    };

    public MainWindow()
    {
        InitializeComponent();
        MaximizeBoundsFix.Apply(this);

        MessageList.ItemsSource = _messages;
        _messagesView = CollectionViewSource.GetDefaultView(_messages);
        _messagesView.Filter = FilterMessage;

        // Real push (IMAP IDLE) is a reasonable upgrade later; a periodic refresh is simpler and
        // catches new mail / other-client changes within a minute, which is enough for a first cut.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _pollTimer.Tick += async (_, _) => await RefreshMessagesAsync(announceNewMail: true);

        // Long enough that normal typing never fires a search per keystroke, short enough that
        // pausing after a word feels responsive rather than "did anything happen".
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _searchDebounceTimer.Tick += async (_, _) =>
        {
            _searchDebounceTimer.Stop();
            await RunFolderSearchAsync();
        };

        ApplyStoredBounds();
        UpdateSelfDomain();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private bool FilterMessage(object obj)
    {
        if (obj is not InboxRow row)
            return true;

        // _messages IS the exact result set once a server-side search is active — re-filtering it
        // by sender/subject/snippet on top would wrongly hide any live-mode row (which always has
        // Snippet == "") that the server matched on body text instead, even though it's already a
        // correct match.
        if (!_serverSearchActive && !string.IsNullOrWhiteSpace(_searchText)
            && !row.Sender.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            && !row.Subject.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            && !row.Snippet.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
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

    private static readonly (string Label, string Key)[] SortOptions =
    [
        ("Newest first", "Date"),
        ("Sender A–Z", "Sender"),
        ("Subject A–Z", "Subject"),
    ];

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = SortButton };
        foreach (var (label, key) in SortOptions)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = _sortKey == key,
            };
            item.Click += (_, _) =>
            {
                _sortKey = key;
                ApplyCurrentFolderView();
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => menu.PopIn();
        menu.IsOpen = true;
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

    /// <summary>
    /// Set before <c>Show()</c> for a start-with-Windows launch: the app comes up in the tray
    /// watching for mail instead of opening a window on a desktop the user just signed in to.
    /// </summary>
    public bool StartInTray { get; init; }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowCorners.Apply(this);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        _tray = new TrayIcon(this, iconPath);
        _tray.ExitRequested += (_, _) =>
        {
            _isExiting = true;
            Close();
            System.Windows.Application.Current.Shutdown();
        };
        _tray.NotificationOpened += (_, id) => OpenNotifiedMessage(id);
        _tray.RestoredFresh += (_, _) => _ = ResetToFreshViewAsync();

        if (StartInTray)
        {
            // The window is Minimized at this point purely to avoid flashing on screen before it
            // hides, so the state to restore to is the one the bounds were saved at.
            _tray.RestoreState = _settings.Maximized ? WindowState.Maximized : WindowState.Normal;
            GoToTray();
        }

        LoadMockFolder("Inbox");
        UpdateInboxBadge();
        UpdateAccountButtonVisual();

        await ReadingPane.EnsureCoreWebView2Async();
        ConfigureReadingPane();

        var saved = AccountSettings.Load();
        if (saved is not null)
        {
            // Painted before the first network call: signing in takes seconds, and staring at an
            // empty list for those seconds (or forever, on a bad connection) is the whole reason
            // the cache exists. Anything the server later says replaces this wholesale.
            ShowCachedInbox(saved);
            await TryConnectAsync(saved);
        }
        else
        {
            StatusText.Text = "Not signed in — click Sign in to connect to your mailbox";
        }
    }

    /// <summary>
    /// Fills the message list from the last cached Inbox for this account. Also primes the
    /// new-mail notifier, so messages already seen in a previous session aren't announced as new
    /// the moment the app reconnects.
    /// </summary>
    private void ShowCachedInbox(AccountSettings account)
    {
        _cache ??= new MessageCache(account.Email);
        var rows = _cache.LoadRows("Inbox");
        if (rows.Count == 0)
            return;

        ReplaceMessages(SortRows(rows));
        _notifier.Collect(rows);
        UpdateListEmptyState();
        StatusText.Text = $"Showing {rows.Count} saved messages — connecting…";
    }

    private async Task TryConnectAsync(AccountSettings account)
    {
        StatusText.Text = $"Signing in as {account.Email}…";
        var backend = new ImapMailBackend(account);
        try
        {
            await backend.ConnectAsync();
        }
        catch (Exception ex)
        {
            await backend.DisposeAsync();
            Log.Error($"Sign-in failed for {account.Email}", ex);
            SetConnectionState(ConnectionState.Offline);
            StatusText.Text = _messages.Count > 0
                ? $"Offline — showing saved mail. ({MailErrors.Friendly(ex)})"
                : $"Sign-in failed ({MailErrors.Friendly(ex)}). Click Sign in to retry.";
            return;
        }

        await SwitchToLiveAsync(account, backend);
    }

    private async Task SwitchToLiveAsync(AccountSettings account, ImapMailBackend backend)
    {
        if (_mail is not null)
            await _mail.DisposeAsync();

        _account = account;
        _cache = new MessageCache(account.Email);
        _mail = backend;
        _mail.MailboxActivity += OnMailboxActivity;
        _mail.ConnectionStateChanged += OnConnectionStateChanged;
        // Another mailbox's message ids say nothing about this one, and its first page is history
        // rather than news — so start the new-mail baseline over on every account switch.
        _notifier.Reset();
        UpdateSelfDomain();
        _currentFolder = "Inbox";
        HighlightFolder("Inbox");
        ResetReadingPane();
        ClearSelection();

        StatusText.Text = "Connected";
        UpdateAccountButtonVisual();
        await LoadSpecialMailboxesAsync();
        await RefreshFoldersAsync();
        await RefreshMessagesAsync();
        _mail.StartIdleMonitor("INBOX");
        // IDLE delivers new mail almost immediately; the poll timer stays on purely as a safety
        // net for whatever it doesn't cover (a dropped IDLE connection, flag changes made from
        // another client that don't always trip CountChanged) — no reason to poll every 60s for
        // the common case IDLE already handles.
        _pollTimer.Start();
    }

    /// <summary>Fired from the IMAP IDLE background loop's own thread — never touch UI state
    /// directly from here.</summary>
    private void OnMailboxActivity() =>
        Dispatcher.BeginInvoke(async () => await RefreshMessagesAsync(announceNewMail: true));

    /// <summary>Also raised from a background thread — same rule.</summary>
    private void OnConnectionStateChanged(ConnectionState state) =>
        Dispatcher.BeginInvoke(() => SetConnectionState(state));

    private ConnectionState _connectionState = ConnectionState.SignedOut;

    /// <summary>
    /// Paints the status-bar dot. Colour rather than text carries this because the status line
    /// next to it is already spoken for by whatever the last action said; the dot is the one thing
    /// that stays put, and its tooltip carries the explanation.
    /// </summary>
    private void SetConnectionState(ConnectionState state)
    {
        _connectionState = state;

        var colour = state switch
        {
            ConnectionState.Push => System.Windows.Media.Color.FromRgb(0x2E, 0xA0, 0x43),      // green — push, instant
            ConnectionState.Polling => System.Windows.Media.Color.FromRgb(0xE3, 0xA0, 0x08),   // amber — works, but slower
            ConnectionState.Connecting => System.Windows.Media.Color.FromRgb(0xE3, 0xA0, 0x08),
            ConnectionState.Offline => System.Windows.Media.Color.FromRgb(0xD1, 0x3A, 0x3A),   // red — not receiving mail
            _ => System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A),                          // grey — signed out
        };

        ConnectionDot.Background = new SolidColorBrush(colour);
        ConnectionDot.ToolTip = $"{state.Label()}{Environment.NewLine}{state.Detail()}";
        System.Windows.Automation.AutomationProperties.SetHelpText(ConnectionDot, state.Detail());
    }

    /// <summary>
    /// Swaps the account button's content between the plain person glyph (signed out) and a small
    /// colored avatar carrying the account's initial (signed in) — and keeps the tooltip honest,
    /// since it used to permanently claim "Sign in to your real mailbox" even once already signed
    /// in and clicking it did something completely different (opened the account menu).
    /// </summary>
    private void UpdateAccountButtonVisual()
    {
        if (_account is null)
        {
            SignInButton.Content = new System.Windows.Controls.TextBlock
            {
                Text = "\uE77B",
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
            };
            SignInButton.ToolTip = "Sign in to your real mailbox";
            return;
        }

        var avatar = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = (System.Windows.Media.Brush)FindResource("AvatarBrush"),
        };
        avatar.Child = new System.Windows.Controls.TextBlock
        {
            Text = _account.Initial,
            // Without this the TextBlock inherits SignInButton's IconButton FontFamily (Segoe
            // Fluent Icons/Segoe MDL2 Assets) — an icon font, so the plain initial letter renders
            // as whatever unrelated glyph that codepoint happens to map to instead of the letter.
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 10.5,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        SignInButton.Content = avatar;
        SignInButton.ToolTip = $"Signed in as {_account.Identity}";
    }

    /// <summary>
    /// The toolbar's account button: opens sign-in when signed out, or the account menu (switch
    /// between saved accounts / add another / sign out) once connected.
    /// </summary>
    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mail is not null)
        {
            ShowAccountMenu(sender as FrameworkElement);
            return;
        }

        OpenAccountWindow(existing: _account);
    }

    /// <summary>
    /// Opens the account page — sign-in/profile, signature, contacts, folders, and settings behind
    /// one sidebar. A real, owned, taskbar-less window (WindowStyle=None, DWM-rounded corners via
    /// WindowCorners.Apply — see AccountWindow.xaml.cs) rather than a Popup: Popups can't reliably
    /// composite rounded-corner transparency around a windowed native control, and this hosts
    /// WebView2 (the signature editor). Being owned and taskbar-less, it still doesn't show up in
    /// Alt-Tab/taskbar disconnected from the app the way a plain top-level window would.
    /// </summary>
    private void OpenAccountWindow(AccountSettings? existing, AccountPage startPage = AccountPage.Profile)
    {
        var account = new AccountWindow(existing, _settings, _mail,
            onFoldersChanged: () => { _ = RefreshFoldersAsync(); },
            onSwitchAccount: a => { _ = TryConnectAsync(a); })
        {
            Owner = this,
        };

        account.Completed += async (_, _) =>
        {
            if (account.Backend is not null && account.Result is not null)
                await SwitchToLiveAsync(account.Result, account.Backend);
        };

        account.Show();
        account.NavigateTo(startPage);
    }

    /// <summary>
    /// Every saved account is listed (not just the active one) so switching means picking a
    /// still-signed-in account instead of discarding it to sign into another — the "Switch
    /// account" flow previously only ever knew about the single one that had ever been saved.
    /// </summary>
    private void ShowAccountMenu(FrameworkElement? anchor)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = anchor };
        menu.KeepInsideWindow();
        var accounts = AccountStore.Load().Accounts;

        foreach (var account in accounts)
        {
            var isActive = _account is not null
                && account.Email.Equals(_account.Email, StringComparison.OrdinalIgnoreCase);
            var item = new System.Windows.Controls.MenuItem
            {
                Header = account.Identity,
                IsCheckable = true,
                IsChecked = isActive,
            };
            item.Click += async (_, _) =>
            {
                if (isActive)
                    return;
                StatusText.Text = $"Signing in as {account.Email}…";
                await TryConnectAsync(account);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new System.Windows.Controls.Separator());

        var addItem = new System.Windows.Controls.MenuItem { Header = "Add account…" };
        addItem.Click += (_, _) => OpenAccountWindow(existing: null);
        menu.Items.Add(addItem);

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Account settings…" };
        settingsItem.Click += (_, _) => OpenAccountWindow(existing: _account);
        menu.Items.Add(settingsItem);

        var signOutItem = new System.Windows.Controls.MenuItem
        {
            Header = _account is null ? "Sign out" : $"Sign out of {_account.Email}",
        };
        signOutItem.Click += async (_, _) => await SignOutActiveAsync();
        menu.Items.Add(signOutItem);

        menu.Opened += (_, _) => menu.PopIn();
        menu.IsOpen = true;
    }

    /// <summary>Manually-added contacts (account page's Contacts section) plus, once signed in,
    /// everyone auto-learned from mail history — manual entries win on display name when both
    /// exist for the same address, since a manual edit is deliberate.</summary>
    private IReadOnlyList<ContactEntry> ListAllContacts()
    {
        var manual = ManualContactsStore.Load()
            .Select(c => new ContactEntry(
                string.IsNullOrWhiteSpace(c.Name) ? c.Email : c.Name, c.Email,
                string.IsNullOrWhiteSpace(c.Name) ? c.Email : $"{c.Name} <{c.Email}>"))
            .ToList();

        var learned = _mail?.Contacts.ListAll() ?? [];
        return manual.Concat(learned.Where(l => !manual.Any(m => m.Address.Equals(l.Address, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    private IReadOnlyList<ContactEntry> SuggestAllContacts(string query, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var manual = ManualContactsStore.Load()
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Email.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(c => new ContactEntry(
                string.IsNullOrWhiteSpace(c.Name) ? c.Email : c.Name, c.Email,
                string.IsNullOrWhiteSpace(c.Name) ? c.Email : $"{c.Name} <{c.Email}>"))
            .ToList();

        var learned = _mail?.Contacts.Suggest(query, max) ?? [];
        return manual.Concat(learned.Where(l => !manual.Any(m => m.Address.Equals(l.Address, StringComparison.OrdinalIgnoreCase))))
            .Take(max)
            .ToList();
    }

    /// <summary>Signs out of the currently active account only. If other accounts are still saved,
    /// the next one becomes active automatically; otherwise the app falls back to sample data —
    /// matching what "sign out" always did when there was only ever one account to lose.</summary>
    private async Task SignOutActiveAsync()
    {
        _pollTimer.Stop();
        if (_mail is not null)
        {
            _mail.MailboxActivity -= OnMailboxActivity;
            _mail.ConnectionStateChanged -= OnConnectionStateChanged;
            await _mail.DisposeAsync();
            _mail = null;
        }
        SetConnectionState(ConnectionState.SignedOut);
        // The account is being removed from this machine, so its cached mail goes too — leaving a
        // signed-out account's messages readable on disk would be a surprise.
        _cache?.Clear();
        _cache = null;
        _notifier.Reset();
        _tray?.SetUnread(0);

        _account?.Remove();
        _account = null;

        var next = AccountSettings.Load();
        if (next is not null)
        {
            await TryConnectAsync(next);
            if (_mail is not null)
                return;
            // Connecting to the next saved account failed too — fall back to sample data rather
            // than leaving the just-signed-out account's live view stuck on screen.
        }

        UpdateAccountButtonVisual();
        UpdateSelfDomain();
        _liveFolders.Clear();
        LiveFolderSection.Visibility = Visibility.Collapsed;
        LoadMockFolder("Inbox");
        UpdateInboxBadge();
        if (next is null)
            StatusText.Text = "Signed out";
    }


    // ---- Window chrome --------------------------------------------------------------------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Pre-existing bug found while chasing a "white on white" hover report: both ternary
        // branches here were literal empty strings — the glyph itself was always invisible, hover
        // just made the empty button's highlighted hit area obvious. Verified via disposable
        // harness: Restore (overlapping squares) / Maximize (single square).
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        if (WindowState == WindowState.Minimized)
            GoToTray();
    }

    /// <summary>
    /// Brings the window back from the tray. Public because a second launch of the exe asks the
    /// already-running instance to surface rather than starting its own (see <see cref="SingleInstance"/>).
    /// </summary>
    public void RestoreFromTray()
    {
        if (_tray is not null)
        {
            _tray.Restore();
            return;
        }

        // Before Loaded has run there's no tray to restore through, but the window still exists.
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Hides the window into the tray, telling the user where it went the first time — an app that
    /// vanishes from both the screen and the taskbar with no explanation reads as a crash.
    /// </summary>
    private void GoToTray()
    {
        // No tray icon means no way back, so stay merely minimized rather than hiding into nothing.
        if (_tray is null)
            return;

        _tray.HideToTray();

        if (_settings.TrayHintShown)
            return;
        _settings.TrayHintShown = true;
        _settings.Save();
        _tray.Notify("Purplemail is still running", "It's in the system tray — click the icon to open it, or right-click for Exit.");
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
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
            if (_mail is not null)
                await _mail.DisposeAsync();
            return;
        }

        if (!_settings.CloseToTray || _tray is null)
        {
            _tray?.Dispose();
            if (_mail is not null)
                await _mail.DisposeAsync();
            System.Windows.Application.Current.Shutdown();
            return;
        }

        // Closing with the X button goes to the tray instead of quitting.
        e.Cancel = true;
        GoToTray();
    }
}
