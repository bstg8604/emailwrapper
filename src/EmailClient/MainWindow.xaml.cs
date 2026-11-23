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

    /// <summary>The local database MailSyncEngine keeps current — introduced alongside the existing
    /// live-refresh path for now (see MailSyncEngine.Start() below): it's populated from the real
    /// account, but nothing reads from it yet. UI call sites are being migrated onto it
    /// incrementally, each verified independently before the next. Null until an account connects,
    /// same lifecycle as <see cref="_cache"/>.</summary>
    private MailStore? _mailStore;
    private MailSyncEngine? _syncEngine;
    // The raw IMAP mailbox name IDLE is currently watching (kept in step with every
    // _mail.StartIdleMonitor call) — OnMailboxActivity needs this to tell MailSyncEngine which
    // folder to sync immediately; _currentFolder is the app's own display key ("Inbox"/"Sent"/...),
    // not necessarily the same string the server expects.
    private string _idleWatchedMailbox = "INBOX";

    private OfflineActionQueue? _offlineActions;
    private bool _isExiting;
    private string _currentFolder = "Inbox";
    // Bumped on every GoToFolderAsync call — lets a folder load whose result would otherwise land
    // after a *later* folder switch already repointed the shared IMAP connection's selected mailbox
    // detect that and discard its own (now-wrong-folder) result instead of applying or caching it
    // under the wrong folder name. See GoToFolderAsync's own remarks for the exact race this closes.
    private int _folderRequestSeq;
    private string _searchText = "";
    private string _quickFilter = "All";
    private string _sortKey = "Date";
    private InboxRow? _openRow;
    private MessageDetail? _openDetail;
    private string? _blockedImagesHtml;
    private string? _meetingLinkUrl;
    private string? _unsubscribeUrl;
    private string? _unsubscribeMailto;

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
        // One subscription point rather than chasing every place _messages gets mutated — see
        // UpdateThreadCounts in MainWindow.MessageList.cs.
        _messages.CollectionChanged += (_, _) => UpdateThreadCounts();

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
        SyncVipSenders();
        // Same fade-in every other window in the app already gets (ComposeWindow, AccountWindow) —
        // without it, the main window was the one place that snapped straight to full opacity.
        this.FadeInOnShow();

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
        if (_settings.Left is { } left && _settings.Top is { } top && IsOnAnyScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (_settings.Maximized)
            WindowState = WindowState.Maximized;

        // Clamped to the column's own MinWidth/MaxWidth (280-700, set in XAML) rather than trusted
        // outright — a hand-edited settings.json shouldn't be able to produce a column the row
        // template can't actually lay out in.
        MessageListColumn.Width = new GridLength(Math.Clamp(_settings.MessageListColumnWidth, 280, 700));
    }

    private void MessageListSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings.MessageListColumnWidth = MessageListColumn.Width.Value;
        _settings.Save();
    }

    /// <summary>Called from the Settings window's own "Reset panel sizes" button — the message list
    /// column is the only user-resizable panel today, but this is named generically so a future
    /// second one (the sidebar, say) has an obvious place to add its own reset alongside this.</summary>
    public void ResetPanelSizes()
    {
        MessageListColumn.Width = new GridLength(360);
        _settings.MessageListColumnWidth = 360;
        _settings.Save();
    }

    /// <summary>
    /// A saved position from a monitor that's since been disconnected (a laptop undocked since its
    /// last close, say) would otherwise restore the window fully off-screen and unreachable without
    /// manually editing settings.json — falling back to WPF's own default placement instead. Checked
    /// against just the saved top-left point rather than the whole window rect, so a window that's
    /// still mostly on-screen (just its bottom-right corner hanging off an edge) is left alone.
    /// </summary>
    private static bool IsOnAnyScreen(double left, double top)
    {
        var point = new System.Drawing.Point((int)left, (int)top);
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            if (screen.WorkingArea.Contains(point))
                return true;
        }
        return false;
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

    /// <summary>Which account (if any) the background loop below is currently retrying for — also
    /// doubles as the "is a retry loop already running" guard, so a second failure while one is
    /// already in flight doesn't spawn a duplicate. Cleared the moment a connect actually succeeds,
    /// the account changes, or the failure turns out to be real bad credentials (see
    /// MailErrors.IsBadCredentials) rather than "just no network right now".</summary>
    private string? _autoReconnectEmail;

    private async Task TryConnectAsync(AccountSettings account)
    {
        StatusText.Text = $"Signing in as {account.Email}…";

        // Created up front — not inside SwitchToLiveAsync, as before — specifically so its existing
        // contacts can be checked *before* connecting: that's what lets ConnectAsync skip its own
        // ~800-envelope scan when a usable copy from a previous session is already there, which was
        // most of what still made sign-in feel slow even after the rest of the local-database
        // rewiring made everything else fast.
        //
        // Both this construction and the ListAllContacts call below live INSIDE the try block, not
        // ahead of it — MailStore's constructor opens two SQLite connections, sets WAL mode, and
        // runs a schema migration, any of which can throw (a mail.db/-wal/-shm left locked by a
        // crashed prior instance, a disk/permission issue). A live incident was exactly this
        // exception escaping uncaught here: the async void Loaded handler let it fall through to
        // the global unhandled-exception dialog, _mail/_mailStore were never assigned, and the app
        // was left permanently stuck on the cached inbox with no retry — the literal "mails not
        // loading" symptom. Folding it into this same try/catch means a failure here now takes the
        // exact same offline-and-keep-retrying path a failed IMAP connect already did.
        MailStore? mailStore = null;
        var backend = new ImapMailBackend(account);
        try
        {
            mailStore = new MailStore(account.Email);
            var skipContactsScan = mailStore.ListAllContacts().Count > 0;
            await backend.ConnectAsync(skipContactsScan);
        }
        catch (Exception ex)
        {
            mailStore?.Dispose();
            await backend.DisposeAsync();

            // A live report was exactly this: the internet dropped for a moment, the very next
            // connect attempt (app startup, or a retry) failed with a plain network error, and the
            // resulting "Sign-in failed — click Sign in to retry" message read as having been
            // logged out, even though nothing about the saved account ever changed. Only a genuine
            // bad-credentials rejection actually needs the user's attention here; anything else
            // (no network, DNS, a TLS hiccup, a timeout) means the account is still fine and just
            // isn't reachable yet, so this keeps quietly retrying in the background instead.
            if (MailErrors.IsBadCredentials(ex))
            {
                _autoReconnectEmail = null;
                Log.Error($"Sign-in failed for {account.Email}", ex);
                SetConnectionState(ConnectionState.Offline);
                StatusText.Text = $"Sign-in failed ({MailErrors.Friendly(ex)}). Click Sign in to retry.";
                return;
            }

            Log.Warn($"Couldn't reach the mail server for {account.Email} — will keep retrying automatically", ex);
            SetConnectionState(ConnectionState.Offline);
            StatusText.Text = _messages.Count > 0
                ? $"Offline — showing saved mail. Reconnecting automatically… ({MailErrors.Friendly(ex)})"
                : $"Offline — reconnecting automatically… ({MailErrors.Friendly(ex)})";

            if (_autoReconnectEmail != account.Email)
            {
                _autoReconnectEmail = account.Email;
                _ = AutoReconnectLoopAsync(account);
            }
            return;
        }

        _autoReconnectEmail = null;
        await SwitchToLiveAsync(account, backend, mailStore!);
    }

    /// <summary>Keeps trying to connect, once every 30 seconds, until it succeeds, the failure
    /// turns out to be real bad credentials, this account stops being the one worth retrying for
    /// (a different account signed in, or this one was signed out), or the app is exiting — same
    /// "never give up silently, but don't spam either" reasoning as the IMAP IDLE loop's own retry
    /// logic elsewhere in this class.</summary>
    private async Task AutoReconnectLoopAsync(AccountSettings account)
    {
        while (!_isExiting && _autoReconnectEmail == account.Email && _mail is null)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
            catch (Exception)
            {
                return;
            }
            if (_isExiting || _autoReconnectEmail != account.Email || _mail is not null)
                return;
            await TryConnectAsync(account);
        }
    }

    private async Task SwitchToLiveAsync(AccountSettings account, ImapMailBackend backend, MailStore? mailStore = null)
    {
        if (_mail is not null)
            await _mail.DisposeAsync();

        _account = account;
        _cache = new MessageCache(account.Email);
        _syncEngine?.Dispose();
        // TryConnectAsync already opens the store itself (it needs to check existing contacts before
        // connecting) and hands it here rather than let it be reopened — reopening would just be
        // wasted work against the same file. The account-switch path (below) has no store yet, so it
        // still passes null and gets one created fresh here.
        if (!ReferenceEquals(_mailStore, mailStore))
            _mailStore?.Dispose();
        _mailStore = mailStore ?? new MailStore(account.Email);
        _syncEngine = new MailSyncEngine(backend, _mailStore);
        // Background-thread events (same "marshal to the UI thread yourself" contract as
        // ImapMailBackend.MailboxActivity/ConnectionStateChanged) — re-render whatever the sync
        // engine just refreshed in the local database, reactively, instead of the UI having to poll
        // or ask for it itself.
        _syncEngine.FoldersChanged += () => Dispatcher.BeginInvoke(async () => await RefreshFoldersAsync());
        // Only worth re-rendering if the folder that just synced is the one actually on screen —
        // a background sync of some other folder has nothing to show right now regardless.
        // RefreshMessagesAsync itself now reads from MailStore first (see its own remarks), so this
        // is an instant local re-render, not another network round trip.
        _syncEngine.DataChanged += mailbox => Dispatcher.BeginInvoke(async () =>
        {
            if (_mail is not null && string.Equals(SpecialMailbox(_currentFolder), mailbox, StringComparison.OrdinalIgnoreCase))
                await RefreshMessagesAsync();
        });
        _syncEngine.SyncStatusChanged += status => Dispatcher.BeginInvoke(() =>
        {
            BackgroundSyncStatusText.Text = status ?? "";
            BackgroundSyncStatusText.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        });
        _offlineActions = new OfflineActionQueue(account.Email);
        _mail = backend;
        _mail.MailboxActivity += OnMailboxActivity;
        _mail.ConnectionStateChanged += OnConnectionStateChanged;
        // Another mailbox's message ids say nothing about this one, and its first page is history
        // rather than news — so start the new-mail baseline over on every account switch.
        _notifier.Reset();
        UpdateSelfDomain();
        SyncVipSenders();
        _currentFolder = "Inbox";
        HighlightFolder("Inbox");
        ResetReadingPane();
        ClearSelection();
        // A search from the previous account means nothing here — without this, RefreshMessagesAsync
        // (MainWindow.MessageList.cs) sees _serverSearchActive still true and skips replacing the
        // list, so account A's search results (and stale pager/status text) stayed on screen after
        // switching to account B until the user happened to touch the search box themselves.
        SearchBox.Text = "";
        _serverSearchActive = false;

        StatusText.Text = "Connected";
        UpdateAccountButtonVisual();
        // ImapMailBackend.ConnectAsync (already finished by the time this method runs) just spent a
        // real IMAP scan building `backend.Contacts` — persisting its result here means every
        // *future* app start, contact autocomplete is instant from MailStore, not another 800-envelope
        // rescan. The rescan itself isn't removed by this (a bigger, separate change — see
        // ContactsIndex.BuildAsync's own lack of lock protection, not safe to call from anywhere
        // else without addressing that first); this only stops its result from being thrown away
        // every time the app restarts.
        foreach (var contact in backend.Contacts.ListAll())
            _mailStore.SeedContact(contact.Address, contact.HasName ? contact.DisplayName : null, contact.Score);
        await LoadSpecialMailboxesAsync();
        await RefreshFoldersAsync();
        await RefreshMessagesAsync();
        _mail.StartIdleMonitor("INBOX");
        _idleWatchedMailbox = "INBOX";
        // Runs alongside the live-refresh calls just above for now, not in place of them — see
        // _mailStore's own remarks on the incremental cutover.
        _syncEngine.Start();
        // IDLE delivers new mail almost immediately; the poll timer stays on purely as a safety
        // net for whatever it doesn't cover (a dropped IDLE connection, flag changes made from
        // another client that don't always trip CountChanged) — no reason to poll every 60s for
        // the common case IDLE already handles.
        //
        // Started 30s late, not immediately alongside _syncEngine — both this and MailSyncEngine's
        // own sweep run on exactly a 60-second DispatcherTimer, and starting them back-to-back put
        // them in near-perfect phase with each other for the rest of the session: a live report's
        // log showed the two colliding on the shared IMAP connection lock repeatedly, not just once
        // at launch — dozens of times over a session, each one forcing whichever side lost to wait
        // the full 60-second lock timeout and then fail outright, which is exactly the kind of stall
        // that made an unrelated attachment download queued behind the same lock at that moment feel
        // stuck or fail too. Offsetting the phase by half the interval keeps them landing roughly
        // 30 seconds apart from each other indefinitely (neither timer drifts relative to the
        // other), rather than trying to make either side's own lock wait smarter.
        _ = Task.Delay(TimeSpan.FromSeconds(30))
            .ContinueWith(_ => _pollTimer.Start(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Fired from the IMAP IDLE background loop's own thread — never touch UI state
    /// directly from here.</summary>
    private void OnMailboxActivity()
    {
        // Diagnostic for a live-reported "notification took 1-2 minutes" complaint with no error in
        // the log — this timestamp is what tells a genuine slow-to-notify-the-client IMAP server
        // apart from a delay introduced somewhere in the app's own dispatch/refresh/notify chain,
        // which the log alone couldn't distinguish. Remove once that's actually diagnosed.
        Log.Info("IDLE: mailbox activity detected, queuing an announcing refresh");
        Dispatcher.BeginInvoke(async () => await RefreshMessagesAsync(announceNewMail: true));
        // Fire-and-forget, and deliberately not marshaled to the UI thread — TriggerImmediateSync
        // never touches a WPF control itself, only _mail/_mailStore; its own DataChanged event is
        // what a UI subscriber would need to marshal, not this call.
        _syncEngine?.TriggerImmediateSync(_idleWatchedMailbox);
    }

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
        var wasOffline = _connectionState == ConnectionState.Offline;
        _connectionState = state;

        if (wasOffline && state is ConnectionState.Push or ConnectionState.Polling)
            _ = ReplayOfflineActionsAsync();

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
            onSwitchAccount: a => { _ = TryConnectAsync(a); },
            onPanelSizesReset: ResetPanelSizes)
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

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => OpenAccountWindow(existing: _account);
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var signOutItem = new System.Windows.Controls.MenuItem
        {
            Header = _account is null ? "Sign out" : $"Sign out of {_account.Email}",
        };
        // A misclick here previously signed out immediately with no way back short of re-entering
        // the password and server settings from scratch — confirm first, the same way Delete
        // Folder and Remove Account already do, and put a separator above it so it isn't the very
        // next item under Settings.
        signOutItem.Click += async (_, _) =>
        {
            var label = _account is null ? "your account" : _account.Email;
            var choice = EmailClient.UI.ConfirmDialog.Show(this, "Sign out",
                $"Sign out of {label}? You'll need to sign in again to see this mailbox.",
                warningIcon: true, new EmailClient.UI.ConfirmChoice("Cancel"), new EmailClient.UI.ConfirmChoice("Sign out", Destructive: true));
            if (choice != "Sign out")
                return;
            await SignOutActiveAsync();
        };
        menu.Items.Add(signOutItem);

        menu.Opened += (_, _) => menu.PopIn();
        menu.IsOpen = true;
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

        var learned = _mailStore?.SuggestContacts(query, max) ?? _mail?.Contacts.Suggest(query, max) ?? [];
        return manual.Concat(learned.Where(l => !manual.Any(m => m.Address.Equals(l.Address, StringComparison.OrdinalIgnoreCase))))
            .Take(max)
            .ToList();
    }

    /// <summary>Signs out of the currently active account only. If other accounts are still saved,
    /// the next one becomes active automatically; otherwise the app falls back to sample data —
    /// matching what "sign out" always did when there was only ever one account to lose.</summary>
    private async Task SignOutActiveAsync()
    {
        // A real, user-requested sign-out always wins over a background auto-reconnect loop that
        // might still be waiting on this exact account from an earlier network failure.
        _autoReconnectEmail = null;
        _pollTimer.Stop();
        _syncEngine?.Dispose();
        _syncEngine = null;
        // Disposed (releasing its file handle on mail.db) before _cache?.Clear() below deletes the
        // whole per-account cache directory that file lives in — Windows won't delete a file that's
        // still open.
        _mailStore?.Dispose();
        _mailStore = null;
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
        SyncVipSenders();
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
        ReturnToInboxFirstPage();

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
        this.FadeInOnRestore();
    }

    /// <summary>
    /// Coming back from the tray (or a second launch while already running) should feel like
    /// opening the app fresh — Inbox, page one — rather than resuming whatever folder/page was left
    /// on screen when it was hidden. The window and its backend state stay alive while in the tray,
    /// so without this the app silently "remembers" the last spot, which reads as a bug rather than
    /// the intended behavior.
    /// </summary>
    private void ReturnToInboxFirstPage()
    {
        if (_currentFolder == "Inbox" && _page.Page <= 1)
            return;

        _ = GoToFolderAsync("Inbox");
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
            _syncEngine?.Dispose();
            _mailStore?.Dispose();
            if (_mail is not null)
                await _mail.DisposeAsync();
            return;
        }

        if (!_settings.CloseToTray || _tray is null)
        {
            _tray?.Dispose();
            _syncEngine?.Dispose();
            _mailStore?.Dispose();
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
