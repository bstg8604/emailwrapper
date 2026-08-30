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

        LoadMockFolder("Inbox");
        UpdateInboxBadge();
        UpdateAccountButtonVisual();

        await ReadingPane.EnsureCoreWebView2Async();
        ConfigureReadingPane();

        var saved = AccountSettings.Load();
        if (saved is not null)
            await TryConnectAsync(saved);
        else
            StatusText.Text = "Not signed in — click Sign in to connect to your mailbox";
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
            StatusText.Text = $"Sign-in failed ({MailErrors.Friendly(ex)}). Click Sign in to retry.";
            return;
        }

        await SwitchToLiveAsync(account, backend);
    }

    private async Task SwitchToLiveAsync(AccountSettings account, ImapMailBackend backend)
    {
        if (_mail is not null)
            await _mail.DisposeAsync();

        _account = account;
        _mail = backend;
        _mail.MailboxActivity += OnMailboxActivity;
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

    private IReadOnlyList<string> SuggestAllContacts(string query, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var manual = ManualContactsStore.Load()
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Email.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(c => string.IsNullOrWhiteSpace(c.Name) ? c.Email : $"{c.Name} <{c.Email}>");

        return manual.Concat(_mail?.Contacts.Suggest(query, max) ?? []).Distinct().Take(max).ToList();
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
            await _mail.DisposeAsync();
            _mail = null;
        }

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

    // ---- Live data loading --------------------------------------------------------------------

    private async Task LoadSpecialMailboxesAsync()
    {
        if (_mail is null)
            return;
        try
        {
            _specialMailboxes = await _mail.GetSpecialMailboxesAsync();
        }
        catch (Exception)
        {
            // Cosmetic only — it decides which folders get a fixed sidebar row vs. a listed one.
        }
    }

    /// <summary>
    /// Re-reads the current folder's messages. In live mode this is the single entry point for
    /// "what's in the list", used by refresh, folder switches and pagination alike.
    ///
    /// Guarded against overlapping calls: the 60-second poll timer and IMAP IDLE's push
    /// notification can both ask for a refresh, and MailKit's ImapClient can't run two commands
    /// concurrently on one connection — a second call arriving while the first is still awaiting
    /// the server is a no-op instead of a race that corrupts both.
    /// </summary>
    private bool _refreshInFlight;

    /// <summary>
    /// Swaps the whole message list in one shot instead of a Clear() + N-many Add() calls — the
    /// ListBox otherwise re-lays-out after every single Add, which for a full search-result or
    /// refresh replacement looks like the rows popping in one at a time rather than the list just
    /// updating. Detaching ItemsSource means none of those intermediate states ever get rendered.
    /// </summary>
    private void ReplaceMessages(IEnumerable<InboxRow> rows)
    {
        MessageList.ItemsSource = null;
        _messages.Clear();
        foreach (var row in rows)
            _messages.Add(row);
        MessageList.ItemsSource = _messages;
    }

    private async Task RefreshMessagesAsync(bool announceNewMail = false)
    {
        if (_refreshInFlight)
            return;
        if (_mail is null)
        {
            ApplyCurrentFolderView();
            return;
        }

        _refreshInFlight = true;
        try
        {
            var previousTopId = _messages.FirstOrDefault()?.Id;

            var page = await _mail.ListMessagesAsync();
            _page = page;

            ReplaceMessages(SortRows(page.Rows));

            UpdatePagerBar();
            UpdateListEmptyState();
            StatusText.Text = DescribePage(page);

            await RefreshFoldersAsync();

            // A genuinely new message shows up as a different, unread row at the top — that's a
            // truer signal than "the count went up", which also fires on a delete-then-refresh.
            if (announceNewMail && previousTopId is not null && page.Rows.Count > 0)
            {
                var newest = page.Rows[0];
                if (newest.Id != previousTopId && newest.Unread)
                    _tray?.ShowBalloon("New mail", $"{newest.Sender}: {newest.Subject}");
            }
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private async Task RefreshFoldersAsync()
    {
        if (_mail is null)
            return;

        try
        {
            var folders = await _mail.ListFoldersAsync();
            _liveFolders.Clear();
            _liveFolders.AddRange(folders);
            RenderLiveFolders();

            var inboxMailbox = SpecialMailbox("Inbox");
            var inbox = folders.FirstOrDefault(f => f.Mailbox.Equals(inboxMailbox, StringComparison.OrdinalIgnoreCase));
            SetInboxBadge(inbox?.Unread ?? 0);
        }
        catch (Exception)
        {
            // Folder chrome is cosmetic — never let a sidebar refresh failure break the mail list.
        }
    }

    private string SpecialMailbox(string folder) =>
        _specialMailboxes.TryGetValue(folder, out var mailbox) && !string.IsNullOrEmpty(mailbox)
            ? mailbox
            : folder == "Inbox" ? "INBOX" : folder;

    /// <summary>A real folder as the sidebar renders it, once the fixed rows are accounted for.</summary>
    private sealed record FolderView(string Mailbox, string Display, int Unread)
    {
        public bool HasUnread => Unread > 0;

        // A DataTrigger on IsActive (see LiveFolderList's ItemTemplate) rather than binding
        // Background directly — a bound Background, even to Transparent, outranks the FolderRow
        // style's IsMouseOver trigger and silently kills hover on every one of these rows.
        public bool IsActive { get; init; }
    }

    private void RenderLiveFolders()
    {
        // The folders that already have their own fixed row at the top of the sidebar.
        var pinned = new[] { "Inbox", "Sent", "Drafts", "Archive", "Trash" }
            .Select(SpecialMailbox)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var views = _liveFolders
            .Where(f => !pinned.Contains(f.Mailbox))
            .Select(f => new FolderView(
                f.Mailbox,
                new string(' ', f.Depth * 4) + f.Name,
                f.Unread)
            {
                IsActive = string.Equals(f.Mailbox, _currentFolder, StringComparison.Ordinal),
            })
            .ToList();

        LiveFolderList.ItemsSource = views;

        // This runs on every folder refresh (every 60s poll included), not just once at sign-in —
        // only the actual collapsed → visible transition should animate; re-triggering a fade on
        // every routine refresh once it's already showing would just be distracting noise.
        var shouldShow = views.Count > 0;
        if (shouldShow && LiveFolderSection.Visibility != Visibility.Visible)
            LiveFolderSection.FadeIn();
        else if (!shouldShow)
            LiveFolderSection.Visibility = Visibility.Collapsed;
    }

    private static string DescribePage(MessagePage page) =>
        page.PageCount > 1
            ? $"{page.Total} messages · page {page.Page} of {page.PageCount}"
            : $"{page.Rows.Count} messages";

    private void UpdatePagerBar()
    {
        if (UseMockData || _page.PageCount <= 1)
        {
            // Only actually animate the real hide transition — this runs on every refresh, and
            // fading out something that's already collapsed would be a silent no-op anyway, so the
            // check is just to avoid restarting/re-triggering an animation needlessly.
            if (PagerBar.Visibility == Visibility.Visible)
                PagerBar.SlideDownHide();
            return;
        }

        // Same reasoning in reverse: only the collapsed → visible transition should animate — once
        // it's already showing, paging back and forth must update the label instantly, not replay
        // a reveal animation on every single page change.
        if (PagerBar.Visibility != Visibility.Visible)
            PagerBar.SlideUpReveal();
        PagerText.Text = $"Page {_page.Page} of {_page.PageCount} · {_page.Total} messages";
        PrevPageButton.IsEnabled = _page.Page > 1;
        NextPageButton.IsEnabled = _page.Page < _page.PageCount;
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e) => await ChangePageAsync(forward: false);

    private async void NextPage_Click(object sender, RoutedEventArgs e) => await ChangePageAsync(forward: true);

    private async Task ChangePageAsync(bool forward)
    {
        if (_mail is null)
            return;

        PrevPageButton.IsEnabled = NextPageButton.IsEnabled = false;
        StatusText.Text = "Loading page…";
        try
        {
            var moved = forward ? await _mail.NextPageAsync() : await _mail.PreviousPageAsync();
            if (!moved)
            {
                UpdatePagerBar();
                return;
            }
            ResetReadingPane();
            ClearSelection();
            await RefreshMessagesAsync();
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
            UpdatePagerBar();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (UseMockData)
        {
            StatusText.Text = "Sign in to view your mailbox";
            return;
        }
        await RefreshMessagesAsync();
    }

    private void ComposeButton_Click(object sender, RoutedEventArgs e) => OpenCompose(bodyHtml: NewMessageBodyHtml);

    /// <summary>
    /// Non-modal by design: reading, switching folders and archiving stay usable while composing,
    /// and — unlike a modal dialog — nothing stops opening a second compose window (a reply from
    /// one message while another is still being written), which used to be blocked outright by
    /// the previous <c>ShowDialog</c>.
    /// </summary>
    private void OpenCompose(string to = "", string subject = "", string body = "", string cc = "", string bcc = "",
        string? replacesDraftId = null, string bodyHtml = "", IReadOnlyList<ComposeAttachment>? attachments = null)
    {
        var compose = new ComposeWindow(to, subject, body, cc, bcc, bodyHtml, attachments) { Owner = this };
        compose.SuggestContacts = query => SuggestAllContacts(query);
        compose.ListAllContacts = ListAllContacts;
        // Live lookup (not a snapshot at open time) so editing signatures while a compose window
        // is already open still offers the current set, not whatever existed a minute ago.
        compose.GetSignatures = () =>
            _settings.Signatures.Select(s => new ComposeWindow.SignatureOption { Name = s.Name, Html = s.BodyHtml }).ToList();

        // Periodic autosave (real mailboxes only — sample data has nothing at stake if a demo
        // window is lost). autosavedUid tracks the current autosave copy so each tick replaces the
        // previous one instead of littering Drafts with a new message every 30 seconds; both
        // closures below share it directly since they're declared in this same method scope.
        MailKit.UniqueId? autosavedUid = null;
        if (!UseMockData)
        {
            compose.AutoSaveDraft = async draft =>
            {
                if (_mail is null || !draft.HasContent)
                    return;
                try
                {
                    var (saved, newUid) = await _mail.SaveDraftAsync(draft, autosavedUid);
                    if (saved)
                        autosavedUid = newUid ?? autosavedUid;
                }
                catch (Exception)
                {
                    // Best-effort — the explicit save-on-close path still covers the normal case.
                }
            };
        }

        compose.Closed += (_, _) => OnComposeClosed(compose, replacesDraftId, autosavedUid);
        compose.Show();
    }

    private void OnComposeClosed(ComposeWindow compose, string? replacesDraftId, MailKit.UniqueId? autosavedUid)
    {
        if (replacesDraftId is not null)
            RemoveMessageEverywhere(replacesDraftId);

        if (compose.Result is { } result)
        {
            BeginUndoableSend(result);
            // The message is being sent for real now — any autosaved copy in Drafts is stale.
            if (autosavedUid is { } sentUid && _mail is not null)
                _ = _mail.DeleteDraftAsync(sentUid);
            return;
        }

        // Closed without sending but with content typed — keep it as a draft rather than
        // silently discarding what the user wrote (Gmail/Outlook both auto-save drafts).
        if (compose.Draft is { } draft && draft.HasContent)
        {
            if (UseMockData)
                AddLocalMessage("Drafts", draft, "Saved to Drafts");
            else
                // Not awaited: this runs from a Closed handler, and blocking it would stall the
                // window-close on a network round trip the user didn't ask to wait for.
                _ = SaveDraftLiveAsync(draft, autosavedUid);
        }
        else
        {
            // Nothing worth keeping — including a periodic autosave from before the user deleted
            // everything they'd typed.
            if (autosavedUid is { } staleUid && _mail is not null)
                _ = _mail.DeleteDraftAsync(staleUid);
            if (replacesDraftId is not null)
                ApplyCurrentFolderView();
        }
    }

    private async Task SaveDraftLiveAsync(ComposeResult draft, MailKit.UniqueId? replacingUid = null)
    {
        if (_mail is null)
            return;
        try
        {
            StatusText.Text = "Saving draft…";
            var (saved, _) = await _mail.SaveDraftAsync(draft, replacingUid);
            StatusText.Text = saved
                ? "Saved to Drafts"
                : "Couldn't save the draft — no Drafts folder found";
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Draft not saved — signed out";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Draft not saved: {ex.Message}";
        }
    }

    private int _localIdSeq;

    private void AddLocalMessage(string folder, ComposeResult message, string status)
    {
        var id = $"local{++_localIdSeq}";
        var now = DateTime.Now;
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject;

        // A rich-text compose already produced HTML; a plain-text one has to be encoded into some.
        var bodyHtml = !string.IsNullOrWhiteSpace(message.BodyHtml)
            ? message.BodyHtml
            : $"<p>{System.Net.WebUtility.HtmlEncode(message.Body).Replace("\n", "<br/>")}</p>";

        var attachments = StageAttachments(message.Files);

        var row = new InboxRow(
            id,
            // Sent and Drafts rows name the recipient, matching how the sample data renders them.
            string.IsNullOrWhiteSpace(message.To) ? "(no recipient)" : MailText.DisplayName(message.To),
            subject,
            MailText.Snippet(bodyHtml),
            MailText.FormatListDate(now),
            Unread: false,
            HasAttachment: attachments.Count > 0,
            Timestamp: now);

        _folderData[folder].Insert(0, row);
        _localBodies[id] = new MessageDetail(
            subject,
            MockData.SelfIdentity,
            MailText.FormatDetailDate(now),
            bodyHtml,
            To: message.To,
            Cc: message.Cc,
            Attachments: attachments,
            Bcc: message.Bcc);

        ApplyCurrentFolderView();
        StatusText.Text = $"{status} (sample data)";
    }

    private readonly Dictionary<string, MessageDetail> _localBodies = [];

    // ---- Undo send ----------------------------------------------------------------------

    /// <summary>
    /// One message held in its undo window. Compose is now non-modal, so more than one send can
    /// legitimately be in flight at once (send from window A, then send from window B before A's
    /// 5 seconds are up) — a single shared timer/result pair would silently drop the earlier one.
    /// </summary>
    private sealed record PendingSend(ComposeResult Result, DispatcherTimer Timer);

    private readonly List<PendingSend> _pendingSends = [];

    // Gmail's signature feature: sending doesn't happen immediately — it's held for a few
    // seconds with an "Undo" option, then actually goes out once that window passes.
    private void BeginUndoableSend(ComposeResult result)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.ClampedUndoSendSeconds) };
        PendingSend pending = new(result, timer);

        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            _pendingSends.Remove(pending);
            UpdateSendSnackbar();
            await PerformSendAsync(pending.Result);
        };

        _pendingSends.Add(pending);
        timer.Start();
        UpdateSendSnackbar();
    }

    // ---- Undo delete/archive/move --------------------------------------------------------------
    // Same bottom-left snackbar as Undo Send — the two are mutually exclusive in practice (both
    // are rare, fleeting, single-user actions), so one shared slot is simpler than a second corner
    // of the screen for what's functionally the same affordance.

    private sealed record PendingActionUndo(string Label, Func<Task> Undo);

    private PendingActionUndo? _pendingActionUndo;
    private DispatcherTimer? _actionUndoTimer;

    /// <summary>Offers "Undo" for an action that already completed (archive/delete/move) rather
    /// than Send's "delay then do it" — <paramref name="undo"/> reverses whatever already
    /// happened.</summary>
    private void ShowActionUndo(string label, Func<Task> undo)
    {
        _actionUndoTimer?.Stop();
        _pendingActionUndo = new PendingActionUndo(label, undo);

        _actionUndoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.ClampedUndoSendSeconds) };
        _actionUndoTimer.Tick += (_, _) =>
        {
            _actionUndoTimer!.Stop();
            _pendingActionUndo = null;
            UpdateSendSnackbar();
        };
        _actionUndoTimer.Start();
        UpdateSendSnackbar();
    }

    private void UpdateSendSnackbar()
    {
        if (_pendingActionUndo is { } action)
        {
            SendSnackbarText.Text = action.Label;
            if (SendSnackbar.Visibility != Visibility.Visible)
                SendSnackbar.SlideUpReveal();
            return;
        }

        if (_pendingSends.Count == 0)
        {
            if (SendSnackbar.Visibility == Visibility.Visible)
                SendSnackbar.SlideDownHide();
            return;
        }

        SendSnackbarText.Text = _pendingSends.Count == 1 ? "Message sent" : $"{_pendingSends.Count} messages sent";
        if (SendSnackbar.Visibility != Visibility.Visible)
            SendSnackbar.SlideUpReveal();
    }

    // Undoes the most recently sent message, or the most recent archive/delete/move if one of
    // those is what's currently offered — the one a user reaching for "Undo" right after acting
    // almost always means.
    private async void UndoSend_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingActionUndo is { } action)
        {
            _actionUndoTimer?.Stop();
            _pendingActionUndo = null;
            UpdateSendSnackbar();
            await action.Undo();
            return;
        }

        if (_pendingSends.Count == 0)
            return;

        var last = _pendingSends[^1];
        last.Timer.Stop();
        _pendingSends.Remove(last);
        UpdateSendSnackbar();

        OpenCompose(last.Result.To, last.Result.Subject, last.Result.Body, last.Result.Cc, last.Result.Bcc,
            bodyHtml: last.Result.BodyHtml, attachments: last.Result.Files);
    }

    private async Task PerformSendAsync(ComposeResult result)
    {
        try
        {
            if (_mail is null)
            {
                await Task.Delay(200); // simulate the round trip
                AddLocalMessage("Sent", result, "Message sent");
                return;
            }

            StatusText.Text = "Sending…";
            var warnings = await _mail.SendAsync(result);
            StatusText.Text = "Message sent";
            await RefreshMessagesAsync();

            // The send itself succeeded, but something in it didn't (a vanished attachment, a
            // mistyped address) — worth an explicit acknowledgment rather than a status-bar line
            // that's easy to miss, since it affects what the recipient actually received.
            if (warnings.Count > 0)
            {
                System.Windows.MessageBox.Show(this,
                    "Your message was sent, but:\n\n" + string.Join("\n", warnings),
                    "Sent with issues", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            // The compose window is already gone by this point (Undo Send closes it immediately),
            // so a failure here used to just lose whatever the user typed. Reopening it with
            // everything intact — the same call Undo itself uses — means nothing's lost; they can
            // fix whatever's wrong (or just retry) and send again.
            StatusText.Text = $"Send failed: {MailErrors.Friendly(ex)}";
            OpenCompose(result.To, result.Subject, result.Body, result.Cc, result.Bcc,
                bodyHtml: result.BodyHtml, attachments: result.Files);
        }
    }

    // ---- Folders ------------------------------------------------------------------------

    private async void FolderInbox_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Inbox");
    private async void FolderSent_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Sent");
    private async void FolderDrafts_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Drafts");
    private async void FolderArchive_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Archive");
    private async void FolderTrash_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Trash");
    private async void FolderStarred_Click(object sender, MouseButtonEventArgs e) => await GoToFolderAsync("Starred");

    private async void LiveFolder_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border { Tag: string mailbox })
            await GoToFolderAsync(mailbox);
    }

    // Matches MainWindow.xaml's FolderActiveBrush (#FFE7D9F7) — deliberately stronger than
    // FolderRow's hover tint (#FFF0EAFA) so "you're in this folder" reads as the dominant cue.
    private static readonly System.Windows.Media.Brush ActiveFolderBrush =
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE7, 0xD9, 0xF7));

    private void HighlightFolder(string folder)
    {
        var fixedRows = new Dictionary<string, System.Windows.Controls.Border>
        {
            ["Inbox"] = FolderInbox,
            ["Sent"] = FolderSent,
            ["Drafts"] = FolderDrafts,
            ["Archive"] = FolderArchive,
            ["Trash"] = FolderTrash,
            ["Starred"] = FolderStarred,
        };

        // A local Background value — even Transparent — always outranks the FolderRow style's
        // IsMouseOver trigger, so stamping Transparent on every inactive row here used to kill
        // their hover highlight entirely. Clearing the local value instead lets the style's own
        // Transparent default (and its hover trigger) take back over.
        foreach (var (name, border) in fixedRows)
        {
            if (name == folder)
                border.Background = ActiveFolderBrush;
            else
                border.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
        }

        if (!UseMockData)
            RenderLiveFolders();
    }

    private async Task GoToFolderAsync(string folder)
    {
        // "Starred" is this app's idea, not a real mailbox — express it the way IMAP servers do:
        // the flagged messages of a real folder (Inbox).
        if (!UseMockData && folder == "Starred")
        {
            await GoToFolderAsync("Inbox");
            FilterStarred.IsChecked = true;
            FilterAll.IsChecked = false;
            _quickFilter = "Starred";
            _messagesView.Refresh();
            UpdateListEmptyState();
            HighlightFolder("Starred");
            StatusText.Text = "Starred messages in Inbox";
            return;
        }

        _currentFolder = folder;
        HighlightFolder(folder);
        ResetReadingPane();
        ClearSelection();
        ResetQuickFilter();

        // A search result set is specific to the folder it was run in — leaving it showing after
        // switching folders would mean the list is silently still filtered to a different folder's
        // search entirely.
        if (_serverSearchActive)
        {
            _serverSearchActive = false;
            SearchBox.Text = "";
        }

        if (_mail is null)
        {
            LoadMockFolder(folder);
            return;
        }

        var mailbox = SpecialMailbox(folder);
        StatusText.Text = $"Opening {FolderDisplayName(folder)}…";
        try
        {
            if (!await _mail.SelectFolderAsync(mailbox))
            {
                StatusText.Text = $"Couldn't open {FolderDisplayName(folder)} — it may no longer exist";
                return;
            }
            _mail.StartIdleMonitor(mailbox);
            await RefreshMessagesAsync();
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open {FolderDisplayName(folder)}: {MailErrors.Friendly(ex)}";
        }
    }

    private string FolderDisplayName(string folder) =>
        _liveFolders.FirstOrDefault(f => f.Mailbox == folder)?.Name ?? folder;

    private void ResetQuickFilter()
    {
        // A leftover "Unread"/"Starred" quick filter from the previous folder could make the
        // new folder look empty for no visible reason — quick filters reset per folder.
        _quickFilter = "All";
        FilterAll.IsChecked = true;
        FilterUnread.IsChecked = false;
        FilterStarred.IsChecked = false;
    }

    private void LoadMockFolder(string folder)
    {
        _currentFolder = folder;
        HighlightFolder(folder);
        ResetReadingPane();
        ClearSelection();
        ResetQuickFilter();
        ApplyCurrentFolderView();
        StatusText.Text = "Sign in to view your mailbox";
    }

    private List<InboxRow> GetFolderRows(string folder) => folder switch
    {
        // Starred is a view across folders, but deleted mail shouldn't resurface in it — Gmail
        // leaves trashed messages out of Starred for the same reason.
        "Starred" =>
        [
            .. _folderData.Where(pair => pair.Key != "Trash")
                          .SelectMany(pair => pair.Value)
                          .Where(r => r.Starred)
        ],
        _ => _folderData.TryGetValue(folder, out var rows) ? rows : [],
    };

    private IEnumerable<InboxRow> SortRows(IEnumerable<InboxRow> source)
    {
        var rows = source as IReadOnlyList<InboxRow> ?? [.. source];
        return _sortKey switch
        {
            "Sender" => rows.OrderBy(r => r.Sender, StringComparer.OrdinalIgnoreCase),
            "Subject" => rows.OrderBy(r => r.Subject, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(r => r.Timestamp ?? DateTime.MinValue),
        };
    }

    private void ApplyCurrentFolderView()
    {
        if (!UseMockData)
        {
            ApplySortToLiveRows();
            return;
        }

        // Not signed in — nothing to show. No sample/placeholder mail; ListEmptyState's own text
        // (see UpdateListEmptyState) tells the user to sign in instead.
        _messages.Clear();
        UpdateListEmptyState();
    }

    /// <summary>
    /// Re-sorts what's on screen. Live mode only ever holds the current page, so this sorts that
    /// page rather than the whole folder.
    /// </summary>
    private void ApplySortToLiveRows()
    {
        var sorted = SortRows(_messages.ToList()).ToList();
        _messages.Clear();
        foreach (var row in sorted)
            _messages.Add(row);
        UpdateListEmptyState();
    }

    private void UpdateListEmptyState()
    {
        ListEmptyState.Text = UseMockData ? "Sign in to view your mailbox" : "No messages";
        ListEmptyState.Visibility = _messagesView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateInboxBadge()
    {
        if (!UseMockData)
            return;
        // No sample mail to count — sidebar badges just stay off until signed in.
        SetInboxBadge(0);
        SetDraftsBadge(0);
    }

    private void SetInboxBadge(int unread)
    {
        InboxUnreadCount.Text = unread.ToString();
        InboxUnreadBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDraftsBadge(int count)
    {
        DraftsCount.Text = count.ToString();
        DraftsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Applies `updater` to the row with this id wherever it lives in `_folderData`, and mirrors
    /// the result into the currently visible `_messages` if it's shown there. Single source of
    /// truth per row, so a folder switch never "resets" a star/read change made earlier. In live
    /// mode `_folderData` is empty, so the visible page is the only thing to update.
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

        for (var i = 0; i < _messages.Count; i++)
        {
            if (_messages[i].Id != id)
                continue;
            updated ??= updater(_messages[i]);
            _messages[i] = updated;
            break;
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

    /// <summary>
    /// Runs one live backend action with the error handling every call site needs: an expired
    /// session reads as a sign-in prompt rather than a generic failure.
    /// </summary>
    private async Task<bool> RunLiveAsync(Func<Task<bool>> action, string success, string failure)
    {
        try
        {
            if (await action())
            {
                StatusText.Text = success;
                return true;
            }
            StatusText.Text = failure;
            return false;
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
            return false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{failure}: {ex.Message}";
            return false;
        }
    }

    // ---- Search ---------------------------------------------------------------------------

    // Server-side search, not just the client-side substring filter below — that filter only ever
    // sees whatever page is currently loaded (50 of a folder's possibly thousands of messages), so
    // relying on it alone would make "search" quietly mean "search what's on screen" instead of
    // the whole folder. A short pause after typing runs a real IMAP SEARCH across the whole folder
    // automatically — Enter forces it immediately without waiting out the pause.
    private bool _serverSearchActive;
    private readonly DispatcherTimer _searchDebounceTimer;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        _messagesView.Refresh();
        UpdateListEmptyState();

        _searchDebounceTimer.Stop();
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            // Cleared — drop back to the normal folder view immediately rather than waiting out
            // the debounce for something that isn't actually a search anymore.
            if (_serverSearchActive)
            {
                _serverSearchActive = false;
                _ = RefreshMessagesAsync();
            }
            return;
        }
        _searchDebounceTimer.Start();
    }

    private async void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;
        e.Handled = true;
        _searchDebounceTimer.Stop();
        await RunFolderSearchAsync();
    }

    private async Task RunFolderSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
            return;

        if (_mail is null)
        {
            StatusText.Text = "Sign in to search this folder's full message history";
            return;
        }

        var folderLabel = FolderDisplayName(_currentFolder);
        StatusText.Text = $"Searching {folderLabel}…";
        try
        {
            var results = await _mail.SearchAsync(query);

            // The box may have changed (or been cleared) while the round trip was in flight —
            // an old, slower search finishing after a newer one (or after the user cleared the
            // box) must not clobber whatever's now actually being shown.
            if (query != SearchBox.Text.Trim())
                return;
            _serverSearchActive = true;

            ReplaceMessages(SortRows(results));

            PagerBar.Visibility = Visibility.Collapsed;
            UpdateListEmptyState();
            StatusText.Text = results.Count == 0
                ? $"No matches for \"{query}\" in {folderLabel}"
                : $"{results.Count} match{(results.Count == 1 ? "" : "es")} for \"{query}\" in {folderLabel}";
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {MailErrors.Friendly(ex)}";
        }
    }

    // ---- Star -------------------------------------------------------------------------------

    // ListBoxItem selection happens on mouse-down, before Click ever fires, so a star/checkbox
    // click would also select/open the row underneath it unless the mouse-down is stopped here.
    private void StarToggle_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private async void StarToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button { Tag: InboxRow row })
            return;

        // Fires immediately, before the (possibly slow) network round trip — the "like" pop is a
        // response to the click itself, not confirmation the server accepted it.
        if (!row.Starred)
            ((UIElement)sender).Pulse();

        await SetStarredAsync(row, !row.Starred);
    }

    private async Task SetStarredAsync(InboxRow row, bool starred)
    {
        // The IMAP \Flagged flag is what a star is — set it on the real message first, and only
        // reflect it locally once it actually took.
        if (!UseMockData && !await RunLiveAsync(
                () => _mail!.SetFlaggedAsync(row.Id, starred),
                starred ? "Starred" : "Unstarred",
                "Couldn't change the star"))
            return;

        UpdateRow(row.Id, r => r with { Starred = starred });

        // The Starred folder is a derived view — unstarring while looking at it must remove the
        // row outright, not just leave it visible-but-unstarred.
        if (UseMockData && _currentFolder == "Starred")
        {
            ApplyCurrentFolderView();
            return;
        }

        _messagesView.Refresh();
        UpdateListEmptyState();
    }

    /// <summary>
    /// Right-click on a row — an alternative to hunting for the checkbox or opening the message
    /// just to reach an action. Selects the row too (matching how every other mail client's
    /// right-click also opens/highlights the item), then offers the same actions the reading
    /// pane's toolbar has, plus entering bulk-select.
    /// </summary>
    // ---- Drag a message onto a sidebar folder to move it ----------------------------------------

    private System.Windows.Point? _dragStart;
    private const string MessageDragFormat = "PurplemailMessageIds";

    private void MessageRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(null);

    private void MessageRow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (sender is not FrameworkElement { Tag: InboxRow row })
            return;

        var pos = e.GetPosition(null);
        // A real drag threshold, not "any movement" — otherwise the tiny jitter between a click's
        // down and up starts a drag and eats the click that was meant to just open the message.
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;

        // Dragging a row that's part of the current checkbox selection drags the whole selection,
        // the same "act on what's checked" scoping Move/Archive/Delete already use.
        var ids = _checkedIds.Contains(row.Id) ? _checkedIds.ToList() : [row.Id];
        var data = new System.Windows.DataObject(MessageDragFormat, ids);
        DragDrop.DoDragDrop((DependencyObject)sender, data, System.Windows.DragDropEffects.Move);
    }

    private void FolderDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(MessageDragFormat) || sender is not Border { Tag: string mailbox } border)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }
        // Can't drop a message onto the folder it's already sitting in.
        if (string.Equals(mailbox, _currentFolder, StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        border.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE7, 0xD9, 0xF7));
    }

    // Clears the local Background it set — not to Transparent, but all the way back to "unset" —
    // so the FolderRow style's own hover trigger still governs it afterward, the same fix the
    // sidebar's active-folder highlighting needed for the same reason (see HighlightFolder).
    private void FolderDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is Border border)
            border.ClearValue(Border.BackgroundProperty);
    }

    private async void FolderDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not Border { Tag: string mailbox } border)
            return;
        border.ClearValue(Border.BackgroundProperty);

        if (e.Data.GetData(MessageDragFormat) is not List<string> ids || ids.Count == 0)
            return;
        if (UseMockData)
        {
            foreach (var id in ids)
                MoveRowLocally(id, mailbox);
            ClearSelection();
            ResetReadingPane();
            ApplyCurrentFolderView();
            UpdateInboxBadge();
            StatusText.Text = $"Moved {ids.Count} to {FolderDisplayName(mailbox)} (sample data)";
            return;
        }

        // The fixed rows (Sent/Drafts/Archive/Trash) carry the friendly key as their Tag, not the
        // real IMAP mailbox path MoveToFolderAsync needs — the live-folder rows already carry the
        // real path, and SpecialMailbox passes that through unchanged (it only rewrites the fixed
        // keys it recognises), so this is safe to call either way.
        var targetMailbox = SpecialMailbox(mailbox);

        var moved = 0;
        ImapMailBackend.MoveUndo? undo = null;
        try
        {
            foreach (var id in ids)
            {
                if (!await _mail!.MoveToFolderAsync(id, targetMailbox))
                    continue;
                undo = ids.Count == 1 ? _mail.ConsumeLastMove() : null;
                RemoveMessageEverywhere(id);
                moved++;
            }
            StatusText.Text = moved == ids.Count
                ? $"Moved {moved} to {FolderDisplayName(mailbox)}"
                : $"Moved {moved} of {ids.Count} to {FolderDisplayName(mailbox)}";
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Move failed: {MailErrors.Friendly(ex)}";
        }

        if (undo is { } move)
        {
            var label = FolderDisplayName(mailbox);
            ShowActionUndo($"Moved to {label}", async () =>
            {
                if (await _mail!.UndoMoveAsync(move))
                    await RefreshMessagesAsync();
                else
                    StatusText.Text = "Couldn't undo — the message may have moved again since";
            });
        }

        ClearSelection();
        ResetReadingPane();
        await RefreshMessagesAsync();
    }

    private async void MessageRow_ContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InboxRow row })
            return;

        MessageList.SelectedItem = row;

        var menu = new System.Windows.Controls.ContextMenu();

        void AddItem(string header, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        AddItem(row.Starred ? "Unstar" : "Star", () => _ = SetStarredAsync(row, !row.Starred));
        AddItem(row.Unread ? "Mark as read" : "Mark as unread", () => _ = SetRowUnreadAsync(row, !row.Unread));
        menu.Items.Add(new System.Windows.Controls.Separator());
        AddItem("Select", () =>
        {
            _checkedIds.Add(row.Id);
            UpdateBulkBar();
            _messagesView.Refresh();
        });
        AddItem("Archive", () => _ = ArchiveMessageAsync(row));
        AddItem("Delete", () => _ = RemoveMessageAsync(row, id => _mail!.DeleteAsync(id), "Deleted"));

        menu.Opened += (_, _) => menu.PopIn();
        menu.IsOpen = true;
        e.Handled = true;
        await Task.CompletedTask;
    }

    private async Task SetRowUnreadAsync(InboxRow row, bool unread)
    {
        if (!UseMockData && !await RunLiveAsync(
                () => _mail!.SetReadAsync(row.Id, !unread),
                unread ? "Marked as unread" : "Marked as read",
                unread ? "Couldn't mark as unread" : "Couldn't mark as read"))
            return;

        UpdateRow(row.Id, r => r with { Unread = unread });
        UpdateInboxBadge();
        _messagesView.Refresh();
        if (_openRow?.Id == row.Id && unread)
            ResetReadingPane();

        if (!UseMockData)
            await RefreshFoldersAsync();
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
        BulkCountText.Text = $"{count} selected";

        // The two bars share one cell and swap based on whether anything's checked — a crossfade
        // reads as one bar smoothly becoming the other, instead of an instant, flickery swap.
        var showBulk = count > 0;
        if (showBulk && BulkActionBar.Visibility != Visibility.Visible)
        {
            BulkActionBar.FadeIn(140);
            QuickFilterBar.FadeOut(100);
        }
        else if (!showBulk && QuickFilterBar.Visibility != Visibility.Visible)
        {
            QuickFilterBar.FadeIn(140);
            BulkActionBar.FadeOut(100);
        }
    }

    private void ClearSelection()
    {
        _checkedIds.Clear();
        UpdateBulkBar();
    }

    private async void BulkMarkRead_Click(object sender, RoutedEventArgs e) =>
        await BulkActionAsync(id => _mail!.SetReadAsync(id, true),
            id => UpdateRow(id, r => r with { Unread = false }), "Marked as read");

    private async void BulkArchive_Click(object sender, RoutedEventArgs e) =>
        await BulkActionAsync(id => _mail!.ArchiveAsync(id), RemoveMessageEverywhere, "Archived",
            applyInMock: id => MoveRowLocally(id, "Archive"));

    private async void BulkDelete_Click(object sender, RoutedEventArgs e) =>
        await BulkActionAsync(id => _mail!.DeleteAsync(id), RemoveMessageEverywhere, "Deleted");

    private void BulkClear_Click(object sender, RoutedEventArgs e) => ClearSelection();

    /// <param name="applyInMock">
    /// Used instead of <paramref name="applyLocally"/> on sample data, where some actions differ:
    /// archiving files the message into the Archive folder rather than removing it from view.
    /// </param>
    private async Task BulkActionAsync(Func<string, Task<bool>> liveAction, Action<string> applyLocally,
        string verb, Action<string>? applyInMock = null)
    {
        var ids = _checkedIds.ToList();
        if (ids.Count == 0)
            return;

        if (UseMockData)
        {
            foreach (var id in ids)
                (applyInMock ?? applyLocally)(id);

            ClearSelection();
            ApplyCurrentFolderView();
            UpdateInboxBadge();
            StatusText.Text = $"{verb} (sample data)";
            return;
        }

        var done = 0;
        try
        {
            foreach (var id in ids)
            {
                if (!await liveAction(id))
                    continue;
                applyLocally(id);
                done++;
            }
            StatusText.Text = done == ids.Count ? $"{verb} {done}" : $"{verb} {done} of {ids.Count}";
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = $"{verb} {done} of {ids.Count} — signed out partway through";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{verb} {done} of {ids.Count} — {ex.Message}";
        }

        ClearSelection();
        await RefreshMessagesAsync();
    }

    // ---- Move to folder ------------------------------------------------------------------------

    /// <summary>
    /// Opens a menu of real destination folders. Applies to the checked rows when there are any,
    /// otherwise to the message that's open — the same scoping Gmail and Outlook use.
    /// </summary>
    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
            return;

        var targets = MoveTargets().ToList();
        if (targets.Count == 0)
        {
            StatusText.Text = "No other folders to move to";
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var (mailbox, display) in targets)
        {
            var item = new System.Windows.Controls.MenuItem { Header = display, Tag = mailbox };
            item.Click += MoveTarget_Click;
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => menu.PopIn();
        menu.IsOpen = true;
    }

    private IEnumerable<(string Mailbox, string Display)> MoveTargets()
    {
        if (UseMockData)
        {
            return _folderData.Keys
                .Where(f => f != _currentFolder)
                .Select(f => (f, f));
        }

        return _liveFolders
            .Where(f => !string.Equals(f.Mailbox, _currentFolder, StringComparison.Ordinal))
            .Select(f => (f.Mailbox, new string(' ', f.Depth * 4) + f.Name));
    }

    private async void MoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string mailbox, Header: string display })
            return;

        var ids = _checkedIds.Count > 0
            ? _checkedIds.ToList()
            : _openRow is { } open ? [open.Id] : new List<string>();

        if (ids.Count == 0)
        {
            StatusText.Text = "Select a message to move";
            return;
        }

        var label = display.Trim();

        if (UseMockData)
        {
            foreach (var id in ids)
                MoveRowLocally(id, mailbox);
            ClearSelection();
            ResetReadingPane();
            ApplyCurrentFolderView();
            UpdateInboxBadge();
            StatusText.Text = $"Moved {ids.Count} to {label} (sample data)";
            return;
        }

        var moved = 0;
        // Only the single-message case gets an Undo offer — ConsumeLastMove only ever holds the
        // *most recent* move, so a bulk move would need to track one MoveUndo per message to undo
        // the whole batch; not worth the extra bookkeeping for how rarely "move" is used in bulk.
        Mail.ImapMailBackend.MoveUndo? undo = null;
        try
        {
            foreach (var id in ids)
            {
                if (!await _mail!.MoveToFolderAsync(id, mailbox))
                    continue;
                undo = ids.Count == 1 ? _mail.ConsumeLastMove() : null;
                RemoveMessageEverywhere(id);
                moved++;
            }
            StatusText.Text = moved == ids.Count ? $"Moved {moved} to {label}" : $"Moved {moved} of {ids.Count} to {label}";
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Move failed: {ex.Message}";
        }

        if (undo is { } move)
        {
            ShowActionUndo($"Moved to {label}", async () =>
            {
                if (await _mail!.UndoMoveAsync(move))
                    await RefreshMessagesAsync();
                else
                    StatusText.Text = "Couldn't undo — the message may have moved again since";
            });
        }

        ClearSelection();
        ResetReadingPane();
        await RefreshMessagesAsync();
    }

    // ---- Reading ------------------------------------------------------------------------

    /// <summary>
    /// Wraps message HTML for the reading pane. The explicit light palette and color-scheme are
    /// load-bearing: without them WebView2 follows the OS dark theme and renders the default page
    /// background black, leaving dark message text unreadable on a dark-mode PC.
    /// </summary>
    private static string WrapHtml(string bodyHtml) => $$"""
        <html><head><meta name="color-scheme" content="light"><style>
        :root { color-scheme: light; }
        html, body { background: #ffffff; color: #1f1f1f; }
        /* A trackpad left/right swipe otherwise pans the whole rendered page sideways (Edge's own
           elastic overscroll gesture) before springing back — jarring and pointless here, since a
           message body never has anything to reveal off to the side. Killing horizontal overscroll
           and overflow removes the gesture instead of just tolerating a page wide enough to need it. */
        html { overflow-x: hidden; overscroll-behavior-x: none; }
        body { font-family: 'Segoe UI', system-ui, sans-serif; font-size: 14px; line-height: 1.55; margin: 0;
          overflow-x: hidden; overscroll-behavior-x: none; }
        /* Chromium's "scroll anchoring" tries to keep whatever's at the top of the viewport from
           visually jumping when layout above it changes size — useful for a page that's still
           loading content, but this page never changes size after it's rendered once. With several
           bordered/shadowed .qcard blocks stacked back to back, the anchor can land right on a card
           boundary and make the touchpad feel like it "resists" there before releasing — this turns
           the heuristic off everywhere instead of fighting it card by card. */
        * { overflow-anchor: none; }
        img, video { max-width: 100%; height: auto; }
        table { max-width: 100%; }
        pre { white-space: pre-wrap; word-wrap: break-word; }
        a { color: #6d28d9; }
        .qcard { margin: 12px 0 0; border: 1px solid #ebebef; border-radius: 12px; background: #ffffff;
          box-shadow: 0 1px 4px rgba(20,20,30,0.04); overflow: hidden; }
        .qcard:first-child { margin-top: 0; }
        .qcard .qhead { display: flex; align-items: flex-start; gap: 10px; padding: 11px 14px;
          background: #fafafa; border-bottom: 1px solid #f0f0f0; }
        .qcard .qavatar { flex: none; width: 32px; height: 32px; border-radius: 50%;
          background: linear-gradient(135deg, #8b5cf6, #6d28d9); color: #fff; font-size: 12.5px;
          font-weight: 600; display: flex; align-items: center; justify-content: center; }
        .qcard .qwho { flex: 1; min-width: 0; }
        .qcard .qname { font-size: 13.5px; font-weight: 600; color: #2a2a2e; }
        .qcard .qto { font-size: 11.5px; color: #9a9aa2; margin-top: 2px; white-space: normal; word-wrap: break-word; }
        /* The address (not the name) is the real clickable mailto: link — matching whatever text
           color it's already sitting in (dark in the sender line, muted gray in To/Cc) rather than
           standing out as its own colored link, since it's still plain selectable/copyable text
           first and a link second. */
        .qaddr { color: inherit; text-decoration: none; }
        .qaddr:hover { text-decoration: underline; }
        .qcard .qdate { flex: none; font-size: 11px; color: #a3a3ab; padding-top: 2px; white-space: nowrap; }
        .qcard .qbody { padding: 14px; color: #333; }
        .qcard blockquote { border: none; margin: 0; padding: 0; }
        .qattachments { display: flex; flex-wrap: wrap; align-items: center; gap: 8px;
          padding: 0 16px 16px; }
        .qattach { display: inline-flex; align-items: center; gap: 7px; background: #f4f4f6;
          border-radius: 7px; padding: 7px 10px; font-size: 12px; color: #333; text-decoration: none; }
        .qattach:hover { background: #ebebee; }
        .qaicon { font-size: 12px; }
        .qasize { color: #aaa; font-size: 11px; }
        .qattach-all { font-size: 12px; color: #6d28d9; text-decoration: none; padding: 7px 4px; }
        .qattach-all:hover { text-decoration: underline; }
        /* Apple Mail's own "•••" fold: a message's own trailing quoted history starts collapsed,
           since that same content is almost always already visible as its own separate card
           earlier in the same conversation — showing it again, expanded, by default is what caused
           it to visibly duplicate. A pure-CSS checkbox toggle, since script is disabled. */
        .qtoggle-cb { display: none; }
        .qtoggle-content { display: none; margin-top: 8px; }
        .qtoggle-cb:checked + .qtoggle-label + .qtoggle-content { display: block; }
        .qtoggle-label { display: inline-block; cursor: pointer; color: #6d28d9; background: #f4f4f6;
          border-radius: 12px; padding: 3px 12px; font-size: 13px; letter-spacing: 1px; margin-top: 6px; }
        .qtoggle-label:hover { background: #ebebee; }
        /* Same checkbox-toggle trick, styled inline instead of as a block pill — "and N more" on a
           To/Cc line reads as a simple text link, expanding in place rather than opening a panel.
           Two labels sharing one checkbox (only one visible at a time) is what lets clicking it a
           second time actually say "show less" and collapse back, instead of "and N more" just
           sitting there looking unclickable once everything's already expanded. */
        .rtoggle-content, .rtoggle-less { display: none; }
        .qtoggle-cb:checked + .rtoggle-content { display: inline; }
        .qtoggle-cb:checked + .rtoggle-content + .rtoggle-more { display: none; }
        .qtoggle-cb:checked + .rtoggle-content + .rtoggle-more + .rtoggle-less { display: inline; }
        .rtoggle-label { color: #6d28d9; cursor: pointer; text-decoration: underline; }
        </style></head><body>{{bodyHtml}}</body></html>
        """;

    /// <summary>
    /// Matches one "leaf" quoted message: an attribution line ("On ... wrote:") immediately
    /// followed by a &lt;blockquote&gt; whose content contains no further blockquote tags. Applied
    /// repeatedly, this always resolves the innermost (oldest) quote first, so a chain nested N
    /// levels deep — exactly what a real IMAP reply-to-a-reply produces — unwraps one round at a
    /// time regardless of depth.
    /// </summary>
    // The wrapping structure around the attribution line varies by mail client — Gmail alone wraps
    // it in two or three nested <div>s (a "gmail_quote_container", then a "gmail_attr" div, plus a
    // trailing <br> before the next tag), Roundcube uses a single bare line or one <div>/<p> — so
    // rather than expecting exactly one wrapper (or matching by exact tag-pair), this tolerates any
    // run of up to a few div/p/br tags (open or close) on either side of the attribution text.
    //
    // The attribution text itself is matched lazily up to the literal word "wrote:" rather than
    // excluding '<' characters — Gmail's own format embeds the sender's address as a clickable
    // mailto: link right inside the line ("Head IDC &lt;<a href="mailto:...">...</a>&gt; wrote:"),
    // and excluding '<' entirely meant that embedded tag broke the match before it ever reached
    // "wrote:", so the quote was never recognized as one at all — not even collapsed, just missed.
    //
    // The second guard — refusing to cross into a new <div>/<p>/<br> — is just as essential: without
    // it, the literal word "on" appearing as a substring inside completely unrelated body text (e.g.
    // "...participating in the Convocati[on] and Departmental...") would itself satisfy "On", and the
    // lazy match would then happily stretch all the way to some genuine "wrote:" much later in the
    // same message, swallowing the sender's own new text as if it were quoted history. A real
    // attribution is always one short inline run of text, never spanning a block boundary — inline
    // tags like the mailto <a> above are fine to cross, a paragraph/div break is not.
    private static readonly Regex AttributedQuote = new(
        "(?:</?(?:p|div|br)\\b[^>]*>\\s*){0,6}(On (?:(?!wrote:)(?!</?(?:div|p|br)\\b).)*?wrote:)(?:\\s*</?(?:p|div|br)\\b[^>]*>){0,6}\\s*<blockquote\\b[^>]*>((?:(?!</?blockquote\\b).)*)</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // The other convention: the attribution line as the blockquote's own first paragraph
    // (<blockquote><p>On ... wrote:</p>...</blockquote>) rather than preceding it — what a raw
    // IMAP reply chain typically looks like, as opposed to this app's own BuildReplyBodyHtml.
    private static readonly Regex AttributedQuoteInside = new(
        "<blockquote\\b[^>]*>\\s*(?:</?(?:p|div|br)\\b[^>]*>\\s*){0,6}(On (?:(?!wrote:)(?!</?(?:div|p|br)\\b).)*?wrote:)(?:\\s*</?(?:p|div|br)\\b[^>]*>){0,6}((?:(?!</?blockquote\\b).)*)</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Turns a flat "quoted history" body — the nested blockquotes an IMAP reply chain (or this
    /// app's own reply/forward) accumulates — into Apple Mail-style separated cards: each earlier
    /// message becomes its own bordered, shadowed card with the "On ... wrote:" line as a header
    /// strip, fully visible rather than collapsed — the same always-expanded, clearly-bounded look
    /// Apple Mail's conversation view uses, just without needing per-message metadata this app
    /// doesn't have (each "message" here is really one HTML blob with quoted history baked in, not
    /// separate stored messages).
    /// </summary>
    // The other common forward convention (Roundcube/Outlook-style): a dashed marker line
    // ("-------- Original Message --------" / "---------- Forwarded message ----------") followed
    // by labeled Subject/Date/From/To/Cc header lines and then the forwarded body, with no
    // blockquote at all — so AttributedQuote/AttributedQuoteInside never match it. Whatever follows
    // the marker is everything there is (a forward is always the last thing in the message), so
    // this matches greedily to the end rather than needing its own closing delimiter.
    private static readonly Regex ForwardMarker = new(
        "<p>\\s*-{2,}\\s*(?:Original Message|Forwarded message)\\s*-{2,}\\s*</p>(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Unique across the whole reading pane, not just one message — several cards in the same
    // conversation each producing their own toggle would otherwise collide on the same checkbox
    // id and cross-wire each other's expand/collapse state.
    private static int _quoteToggleSeq;

    private static string SeparateQuotedThread(string html, bool suppressNestedQuotes = false)
    {
        string previous;
        do
        {
            previous = html;
            html = AttributedQuote.Replace(html, m =>
                $"<div class=\"qcard\"><div class=\"qhead\">{m.Groups[1].Value}</div>"
                + $"<div class=\"qbody\">{m.Groups[2].Value}</div></div>");
            html = AttributedQuoteInside.Replace(html, m =>
                $"<div class=\"qcard\"><div class=\"qhead\">{m.Groups[1].Value}</div>"
                + $"<div class=\"qbody\">{m.Groups[2].Value}</div></div>");
        } while (html != previous);

        var forward = ForwardMarker.Match(html);
        // Only hoist a forward marker that's still at the top level — one nested inside a quote
        // that AttributedQuote already turned into its own qcard above is a different case: "match
        // to end of string" would swallow that qcard's own closing tags into the new one, corrupting
        // both. Left alone, it just renders as ordinary nested content inside the existing qcard —
        // still folded under the same collapse toggle, so nothing is lost, just not double-carded.
        if (forward.Success && !html[..forward.Index].Contains("<div class=\"qcard\">", StringComparison.Ordinal))
        {
            html = html[..forward.Index]
                + $"<div class=\"qcard\"><div class=\"qhead\">Forwarded message</div>"
                + $"<div class=\"qbody\">{forward.Groups[1].Value}</div></div>";
        }

        // Everything from the first quote card to the end of the message IS the quoted history —
        // nothing meaningful ever follows it in a normal reply/forward — so folding from there
        // onward is enough, without needing to track each nesting level separately.
        var firstQuote = html.IndexOf("<div class=\"qcard\">", StringComparison.Ordinal);
        if (firstQuote >= 0)
        {
            if (suppressNestedQuotes)
            {
                // A sibling message elsewhere in this same conversation already shows this exact
                // content as its own real card — including it again here, even collapsed behind a
                // toggle, is a guaranteed duplicate rather than a possible one, so it's dropped
                // entirely instead of folded.
                html = html[..firstQuote];
            }
            else
            {
                var toggleId = $"qtoggle{System.Threading.Interlocked.Increment(ref _quoteToggleSeq)}";
                html = html[..firstQuote]
                    + $"""<input type="checkbox" id="{toggleId}" class="qtoggle-cb">"""
                    + $"""<label for="{toggleId}" class="qtoggle-label">&#8226;&#8226;&#8226;</label>"""
                    + $"""<div class="qtoggle-content">{html[firstQuote..]}</div>""";
            }
        }

        return html;
    }

    private static readonly Regex RemoteImageSrc =
        new("""(<img\b[^>]*\bsrc\s*=\s*["'])(https?://[^"']+)(["'])""", RegexOptions.IgnoreCase);

    private const string TransparentPixel = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==";

    // ---- Untrusted HTML hardening -------------------------------------------------------------
    // A received message's HTML comes straight from whoever sent it. Two layers of defense: script
    // execution is turned off entirely for ReadingPane's WebView2 (see ConfigureReadingPane, the
    // strong guarantee), and HtmlSanitizer (Ganss.Xss, AngleSharp-based — an actual DOM parse, not a
    // regex pass over tag soup) strips everything else that doesn't need script to do damage:
    // <iframe>/<object>/<embed>/<form> still fetch or submit to a remote URL, <meta
    // http-equiv="refresh"> still redirects, and inline event-handler attributes/javascript: hrefs
    // are gone regardless of how they're spelled or obfuscated — a regex pass over raw tag text
    // can be fooled by things a real parser can't (broken attribute quoting, HTML entities inside a
    // tag name, duplicate attributes).
    private static readonly Ganss.Xss.HtmlSanitizer BodySanitizer = CreateBodySanitizer();

    private static Ganss.Xss.HtmlSanitizer CreateBodySanitizer()
    {
        var sanitizer = new Ganss.Xss.HtmlSanitizer();
        // Base64-embedded images (signatures, inline logos) are common in real mail and were never
        // blocked by the old regex pass — only SanitizeRemoteImages' http(s) tracking-pixel check
        // applies to images, so this keeps that same behavior instead of silently dropping them.
        sanitizer.AllowedSchemes.Add("data");
        return sanitizer;
    }

    private static string SanitizeHtmlForDisplay(string html) => BodySanitizer.Sanitize(html);

    /// <summary>
    /// Locks down the reading pane's WebView2 once, at startup: no script execution at all (mail
    /// HTML has zero legitimate need for it), no DevTools, and every link click or attempted popup
    /// is redirected to the OS default browser instead of navigating in place.
    /// </summary>
    private void ConfigureReadingPane()
    {
        var core = ReadingPane.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenInDefaultBrowser(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            // Our own WrapHtml() output arrives via NavigateToString, not an http(s) URI — only a
            // real link click (or a meta-refresh regex missed) looks like this, and neither should
            // ever navigate the reading pane itself.
            if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenInDefaultBrowser(e.Uri);
            }
            // In-card attachment chips — a fake scheme instead of real script, since the reading
            // pane has JavaScript turned off entirely (mail HTML has no legitimate need for it, and
            // this way that guarantee never has to be relaxed just to open an attachment).
            else if (e.Uri.StartsWith("appattach://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                HandleAttachmentLink(e.Uri);
            }
            // A link flagged by FlagMismatchedLinks — same fake-scheme trick as appattach://,
            // routed to a confirmation dialog instead of straight to the browser.
            else if (e.Uri.StartsWith("applink://warn", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                HandleSuspiciousLink(e.Uri);
            }
            // An inline body image, wrapped by WrapClickableImages so clicking it opens a full-size
            // preview instead of doing nothing — <img> has no click handler of its own to give it
            // with script disabled, so the wrapping <a> is what makes it clickable at all.
            else if (e.Uri.StartsWith("applink://viewimage", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                _ = HandleImageLinkAsync(e.Uri);
            }
            // A sender/recipient name clicked in the reading pane (see FormatAddressLink) — opens a
            // new message addressed to them, the same as clicking a name in Apple Mail's own header.
            else if (e.Uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                var address = Uri.UnescapeDataString(e.Uri["mailto:".Length..].Split('?')[0]);
                if (!string.IsNullOrWhiteSpace(address))
                    OpenCompose(address, bodyHtml: NewMessageBodyHtml);
            }
        };
    }

    private static void OpenInDefaultBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Nothing sensible to do if the OS can't hand the link to a browser.
        }
    }

    // Matches a plain `<a href="...">text</a>` — no nested tags in the link text. Real phishing
    // mail almost always disguises a link this simple way ("click here" or, more convincingly, a
    // fake URL as the visible text); a link whose text is itself formatted HTML is vanishingly
    // rare and not worth the complexity of a real DOM walk just to also cover it.
    private static readonly Regex PlainLinkTag = new(
        """<a\b([^>]*?)\bhref\s*=\s*(["'])(.*?)\2([^>]*)>([^<]*)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Thunderbird's "link mismatch" check: when a link's own visible text is itself a URL (the
    /// classic phishing trick of showing "https://your-bank.com" as the text while the real href
    /// points somewhere else entirely), compare the two hosts. A mismatch gets routed through
    /// applink://warn instead of the real destination, so ConfigureReadingPane can show exactly
    /// where it actually goes before the user's browser opens it. A link whose visible text isn't
    /// itself a URL (i.e. the vast majority of ordinary mail links) is left completely alone —
    /// there's nothing to compare it against.
    /// </summary>
    private static string FlagMismatchedLinks(string html) => PlainLinkTag.Replace(html, m =>
    {
        var href = System.Net.WebUtility.HtmlDecode(m.Groups[3].Value);
        var visibleText = System.Net.WebUtility.HtmlDecode(m.Groups[5].Value).Trim();

        if (!Uri.TryCreate(href, UriKind.Absolute, out var hrefUri)
            || (hrefUri.Scheme != Uri.UriSchemeHttp && hrefUri.Scheme != Uri.UriSchemeHttps))
            return m.Value;

        var looksLikeUrl = visibleText.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || visibleText.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || visibleText.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeUrl)
            return m.Value;

        var textForParsing = visibleText.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? "https://" + visibleText
            : visibleText;
        if (!Uri.TryCreate(textForParsing, UriKind.Absolute, out var textUri))
            return m.Value;

        if (HostsMatch(hrefUri.Host, textUri.Host))
            return m.Value;

        var warnHref = $"applink://warn?url={Uri.EscapeDataString(href)}&label={Uri.EscapeDataString(visibleText)}";
        return $"""<a{m.Groups[1].Value}href="{warnHref}"{m.Groups[4].Value}>{m.Groups[5].Value}</a>""";
    });

    private static bool HostsMatch(string a, string b)
    {
        string Strip(string h) => h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        return Strip(a).Equals(Strip(b), StringComparison.OrdinalIgnoreCase);
    }

    private void HandleSuspiciousLink(string uri)
    {
        var query = new Uri(uri).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        if (!query.TryGetValue("url", out var realUrl))
            return;
        var label = query.GetValueOrDefault("label", realUrl);

        var choice = System.Windows.MessageBox.Show(this,
            $"This link's text says it goes to:\n{label}\n\nBut it actually opens:\n{realUrl}\n\n" +
            "This is a common phishing trick. Open it anyway?",
            "Suspicious link", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (choice == MessageBoxResult.Yes)
            OpenInDefaultBrowser(realUrl);
    }

    private static readonly Regex ImgTag = new("""<img\b[^>]*\bsrc\s*=\s*["']([^"']+)["'][^>]*>""", RegexOptions.IgnoreCase);

    /// <summary>
    /// Wraps every real inline image in an `applink://viewimage` link so clicking it opens a
    /// full-size preview — plain &lt;img&gt; has no click behavior of its own to give it with
    /// script disabled, so a wrapping &lt;a&gt; plus a fake scheme (same trick as appattach://) is
    /// what makes an inline image clickable at all. The transparent tracking-pixel placeholder
    /// SanitizeRemoteImages substitutes isn't real content, so it's deliberately left unwrapped.
    /// </summary>
    private string WrapClickableImages(string html, string messageId) => ImgTag.Replace(html, m =>
    {
        var src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        if (src == TransparentPixel)
            return m.Value;

        var images = _conversationImages.TryGetValue(messageId, out var list) ? list : _conversationImages[messageId] = [];
        var idx = images.Count;
        images.Add(src);

        return $"""<a href="applink://viewimage?id={Uri.EscapeDataString(messageId)}&idx={idx}">{m.Value}</a>""";
    });

    private async Task HandleImageLinkAsync(string uri)
    {
        var query = new Uri(uri).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        if (!query.TryGetValue("id", out var id) || !query.TryGetValue("idx", out var idxStr)
            || !int.TryParse(idxStr, out var idx)
            || !_conversationImages.TryGetValue(id, out var images) || idx < 0 || idx >= images.Count)
            return;

        var path = await MaterializeImageAsync(images[idx]);
        if (path is null)
        {
            StatusText.Text = "Couldn't open the image";
            return;
        }

        var name = Path.GetFileName(path);
        if (QuickLookWindow.CanPreview(name))
            new QuickLookWindow(path, name) { Owner = this }.Show();
        else
            new AttachmentViewerWindow(path, name) { Owner = this }.Show();
    }

    /// <summary>
    /// QuickLookWindow (like AttachmentViewerWindow) needs a real file on disk — an inline image's
    /// "source" is either a data: URI (already-decoded bytes, from an embedded cid: image) or a
    /// remote http(s) URL, neither of which it can open directly.
    /// </summary>
    private async Task<string?> MaterializeImageAsync(string src)
    {
        try
        {
            Directory.CreateDirectory(AttachmentCacheDirectory);
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma < 0)
                    return null;
                var header = src[5..comma]; // "image/png;base64"
                var ext = header.Split(';')[0].Split('/').ElementAtOrDefault(1) ?? "png";
                var bytes = Convert.FromBase64String(src[(comma + 1)..]);
                var path = Path.Combine(AttachmentCacheDirectory, $"inline-{Guid.NewGuid():N}.{ext}");
                await File.WriteAllBytesAsync(path, bytes);
                return path;
            }
            if (Uri.TryCreate(src, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var ext = Path.GetExtension(uri.LocalPath) is { Length: > 1 } e ? e : ".img";
                var path = Path.Combine(AttachmentCacheDirectory, $"inline-{Guid.NewGuid():N}{ext}");
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(uri);
                await File.WriteAllBytesAsync(path, bytes);
                return path;
            }
        }
        catch (Exception)
        {
            // Falls through to null — StatusText tells the user it couldn't be opened.
        }
        return null;
    }

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
        ImagesBlockedBar.SlideUpHide();
        ReadingPane.NavigateToString(WrapHtml(_blockedImagesHtml));
        _blockedImagesHtml = null;
    }

    private bool _syncingSelection;
    // Bumped on every selection change so a slow fetch that's still in flight when a newer one
    // starts (or finishes first) knows it's stale and doesn't overwrite what's now actually
    // selected — without this, clicking through messages quickly could show mail B's content
    // arriving late and stomping over mail C's, which had already loaded and rendered correctly.
    private int _openRequestSeq;

    // Once a real message body has actually been fetched over IMAP, keep it — reopening the same
    // mail later in the session is then a free in-memory lookup instead of another network round
    // trip. Never evicted: a folder page tops out at 50-ish rows, so worst case this is a few
    // hundred cached bodies over a long session, not an unbounded leak.
    private readonly Dictionary<string, MessageDetail> _messageDetailCache = new();

    private async void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
            return;
        if (MessageList.SelectedItem is not InboxRow row)
            return;

        // Opening a draft resumes editing it, rather than showing it as a read-only message.
        if (UseMockData && _currentFolder == "Drafts")
        {
            var draft = _localBodies.GetValueOrDefault(row.Id) ?? MockData.MessageBodies.GetValueOrDefault(row.Id);
            if (draft is not null)
            {
                MessageList.SelectedItem = null;
                OpenCompose(
                    draft.To,
                    draft.Subject == "(no subject)" ? "" : draft.Subject,
                    MailText.HtmlToPlainText(draft.BodyHtml),
                    draft.Cc,
                    draft.Bcc,
                    replacesDraftId: row.Id,
                    bodyHtml: draft.BodyHtml);
                return;
            }
        }

        var requestId = ++_openRequestSeq;
        ShowReadingPaneLoading(row);

        MessageDetail? detail;
        var wasCached = _localBodies.ContainsKey(row.Id) || _messageDetailCache.ContainsKey(row.Id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            detail = _localBodies.GetValueOrDefault(row.Id)
                ?? _messageDetailCache.GetValueOrDefault(row.Id)
                ?? (UseMockData
                    ? MockData.MessageBodies.GetValueOrDefault(row.Id)
                    : await _mail!.OpenMessageAsync(row.Id));
            if (!UseMockData && detail is not null)
                _messageDetailCache[row.Id] = detail;
        }
        catch (SessionExpiredException)
        {
            ReadingPaneLoadingOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = "Signed out — sign in again to continue";
            return;
        }
        catch (Exception ex)
        {
            ReadingPaneLoadingOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Couldn't open the message: {ex.Message}";
            return;
        }
        // Temporary diagnostic — remove once we've confirmed where the time actually goes. Not
        // gated behind UseMockData: this is exactly the number we need from a real account.
        var fetchMs = sw.ElapsedMilliseconds;

        if (detail is null)
        {
            ReadingPaneLoadingOverlay.Visibility = Visibility.Collapsed;
            if (!UseMockData)
                StatusText.Text = "Couldn't read the message";
            return;
        }

        // Selection moved on again while this fetch was still in flight — applying it now would
        // stomp over whatever's already loaded (or still loading) for the message actually
        // selected, which is exactly the "selected and opened don't match" symptom this guards.
        if (requestId != _openRequestSeq)
            return;

        await ReadingPane.EnsureCoreWebView2Async();

        if (!UseMockData && row.Unread)
        {
            try
            {
                await _mail!.SetReadAsync(row.Id, true);
            }
            catch (Exception)
            {
                // Reading the message still worked; a failed read-flag isn't worth blocking on.
            }
        }

        _openRow = UpdateRow(row.Id, r => r with { Unread = false }) ?? row;
        _openDetail = detail;
        UpdateInboxBadge();

        // UpdateRow just replaced this row's object in _messages (InboxRow is an immutable record,
        // so marking it read created a new instance) — the ListBox's own SelectedItem still points
        // at the old, now-absent instance, which orphans the selection: SelectionChanged already
        // fired (that's how we got here), but no container is left marked IsSelected, so the "this
        // is the open message" highlight silently never shows. Re-pointing SelectedItem at the
        // replacement re-syncs it.
        if (!ReferenceEquals(MessageList.SelectedItem, _openRow))
        {
            _syncingSelection = true;
            try
            {
                MessageList.SelectedItem = _openRow;
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        EmptyState.Visibility = Visibility.Collapsed;
        // Only the empty-state-to-first-message transition fades in; switching between two
        // messages that are both already showing skips the animation; re-fading on every click
        // would add perceived latency rather than smoothness there.
        if (ReadingCard.Visibility != Visibility.Visible)
            ReadingCard.FadeIn(180);
        else
            ReadingCard.Visibility = Visibility.Visible;

        ReadingSubject.Text = detail.Subject;
        ReadingFrom.Text = detail.From;
        ReadingDate.Text = detail.Date;
        ReadingAvatarInitial.Text = row.Initial;

        if (IsExternalSender(detail.From))
            ExternalSenderBar.SlideDownReveal();
        else
            ExternalSenderBar.Visibility = Visibility.Collapsed;

        // Every message renders the same way — as one or more Apple Mail-style cards, each fully
        // self-contained with its own avatar/sender/to/date — rather than switching between a plain
        // layout for a lone message and cards only once there's a real conversation. A single
        // message is just a conversation of one. Siblings are found by subject across every folder,
        // matching Apple Mail's own default of including related messages from other mailboxes.
        ReadingFromRow.Visibility = Visibility.Collapsed;

        string cleanHtml;
        try
        {
            var conversation = await GatherConversationAsync(_openRow, detail);
            if (requestId != _openRequestSeq)
                return;
            cleanHtml = FlagMismatchedLinks(BuildConversationHtml(conversation));

            if (detail.Calendar is { } invite)
            {
                // A real calendar invite beats guessing — an exact start time (and location, when the
                // invite has one) instead of scraping "tomorrow at 5 PM" out of prose.
                _meetingLinkUrl = invite.JoinUrl;
                MeetingBarTitle.Text = invite.Title;
                var when = invite.Start is { } start ? start.LocalDateTime.ToString("ddd, MMM d · h:mm tt") : null;
                MeetingBarSubtext.Text = (when, invite.Location) switch
                {
                    (not null, not null) => $"{when} · {invite.Location}",
                    (not null, null) => when,
                    (null, not null) => invite.Location,
                    _ => "Calendar invite",
                };
                JoinMeetingButton.Visibility = invite.JoinUrl is null ? Visibility.Collapsed : Visibility.Visible;
                MeetingBar.SlideDownReveal();
            }
            else if (FindMeetingLink(cleanHtml) is { } meetingLink)
            {
                _meetingLinkUrl = meetingLink.Url;
                // The subject is almost always the meeting's real name ("Weekly sync", "Thesis
                // review") — far more useful as the card's title than repeating "Zoom meeting" or the
                // raw URL, which is all the link itself carries.
                MeetingBarTitle.Text = detail.Subject;
                MeetingBarSubtext.Text = meetingLink.When is { } linkWhen
                    ? $"{meetingLink.Label} · {linkWhen}"
                    : $"{meetingLink.Label} meeting";
                JoinMeetingButton.Visibility = Visibility.Visible;
                MeetingBar.SlideDownReveal();
            }
            else
            {
                _meetingLinkUrl = null;
                MeetingBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception)
        {
            // The conversation-card/meeting-detection pipeline is all best-effort presentation on
            // top of the real body — a message with some unusual structure that trips one of those
            // regexes/parsers must still show its plain body, not a blank reading pane forever.
            cleanHtml = SanitizeHtmlForDisplay(detail.BodyHtml);
            _meetingLinkUrl = null;
            MeetingBar.Visibility = Visibility.Collapsed;
        }

        var (safeHtml, hadRemoteImages) = SanitizeRemoteImages(cleanHtml);
        _blockedImagesHtml = hadRemoteImages ? cleanHtml : null;
        if (hadRemoteImages)
            ImagesBlockedBar.SlideDownReveal();
        else
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
        ReadingPane.NavigateToString(WrapHtml(safeHtml));
        ReadingPaneLoadingOverlay.Visibility = Visibility.Collapsed;

        if (!UseMockData)
            StatusText.Text = $"{(wasCached ? "Cache hit" : "Live fetch")} — {fetchMs}ms";
    }

    /// <summary>
    /// Finds every other message in the same conversation (subject with reply/forward prefixes
    /// stripped) — across every folder for sample data, matching Apple Mail's default of including
    /// related messages from other mailboxes, or across the *whole current folder* for a live
    /// account (not just whatever page happens to be loaded — a reply from months ago still needs
    /// to thread in, the same as Apple Mail does regardless of pagination). Returns newest-first
    /// (Apple Mail's own conversation-view default), opened message included.
    /// </summary>
    private async Task<List<(InboxRow Row, MessageDetail Detail)>> GatherConversationAsync(InboxRow row, MessageDetail openedDetail)
    {
        var key = row.ConversationKey;
        var results = new List<(InboxRow Row, MessageDetail Detail)> { (row, openedDetail) };
        if (string.IsNullOrWhiteSpace(key))
            return results;

        IEnumerable<InboxRow> siblings;
        if (UseMockData)
        {
            siblings = _folderData.Values.SelectMany(rows => rows)
                .Where(r => r.Id != row.Id && r.ConversationKey == key)
                .GroupBy(r => r.Id).Select(g => g.First())
                .Take(8);
        }
        else
        {
            try { siblings = await _mail!.FindConversationSiblingsAsync(key, row.Id); }
            catch (Exception) { siblings = []; } // best-effort — a lookup failure still shows the opened message alone
        }

        foreach (var sibling in siblings)
        {
            MessageDetail? siblingDetail = _localBodies.GetValueOrDefault(sibling.Id)
                ?? (UseMockData ? MockData.MessageBodies.GetValueOrDefault(sibling.Id) : null);

            if (siblingDetail is null && !UseMockData)
            {
                try { siblingDetail = await _mail!.OpenMessageAsync(sibling.Id); }
                catch (Exception) { /* best-effort — a conversation missing one sibling still shows the rest */ }
            }

            if (siblingDetail is not null)
                results.Add((sibling, siblingDetail));
        }

        return [.. results.OrderByDescending(r => r.Row.Timestamp ?? DateTime.MinValue)];
    }

    /// <summary>
    /// A To/Cc line that actually lets you see who's hidden behind "and N more" — a plain collapsed
    /// summary with no way to expand it is a dead end once a list is long enough to need collapsing
    /// in the first place. Same checkbox-toggle trick as the quote fold, styled inline.
    /// </summary>
    private string BuildRecipientLine(string label, string recipients)
    {
        if (string.IsNullOrWhiteSpace(recipients))
            return "";

        var (shown, hidden) = MailText.SplitRecipients(recipients);
        var shownText = string.Join(", ", shown.Select(FormatAddressLink));
        if (hidden.Count == 0)
            return $"""<div class="qto">{label}: {shownText}</div>""";

        var toggleId = $"rtoggle{System.Threading.Interlocked.Increment(ref _quoteToggleSeq)}";
        var hiddenText = string.Join(", ", hidden.Select(FormatAddressLink));
        return $"""
            <div class="qto">{label}: {shownText} <input type="checkbox" id="{toggleId}" class="qtoggle-cb"><span class="rtoggle-content">, {hiddenText}</span> <label for="{toggleId}" class="rtoggle-label rtoggle-more">and {hidden.Count} more</label><label for="{toggleId}" class="rtoggle-label rtoggle-less">show less</label></div>
            """;
    }

    /// <summary>
    /// Turns "Name &lt;address&gt;" (or a bare address) into plain name text plus a real clickable
    /// mailto: link for just the address — the name isn't itself a link (there's nothing to select
    /// or copy about a display name), the address is, matching what someone reaches for when they
    /// click/select a recipient at all. Clicking it opens a new message addressed to them (see the
    /// mailto:// handling in ConfigureReadingPane).
    /// </summary>
    private static string FormatAddressLink(string rawAddress)
    {
        var address = MailText.AddressOnly(rawAddress);
        if (string.IsNullOrWhiteSpace(address))
            return Encode(rawAddress);

        var href = Encode("mailto:" + address);
        var addressLink = $"""<a class="qaddr" href="{href}">{Encode(address)}</a>""";

        var name = MailText.DisplayName(rawAddress);
        if (string.IsNullOrWhiteSpace(name) || name.Equals(address, StringComparison.OrdinalIgnoreCase))
            return addressLink;

        return $"{Encode(name)} &lt;{addressLink}&gt;";
    }

    /// <summary>Renders a whole conversation as separate Apple Mail-style cards — every message,
    /// including the one that was actually clicked, gets identical treatment: its own avatar,
    /// sender, to-line and date, newest at the top. See SeparateQuotedThread for the single-message
    /// equivalent (a quoted-history blob baked into one message, rather than genuinely separate
    /// stored messages).</summary>
    private string BuildConversationHtml(List<(InboxRow Row, MessageDetail Detail)> conversation)
    {
        _conversationAttachments.Clear();
        _conversationImages.Clear();

        // A real earlier message in this same conversation already gets its own full card below —
        // that same content is almost always also baked into a later reply's own raw body as
        // quoted history, so showing it there too (even collapsed) is a guaranteed duplicate, not
        // a maybe. Only when a message has no siblings here (conversation.Count == 1) is its own
        // quoted history the only place that content exists, so it's kept (collapsed) in that case.
        var suppressNestedQuotes = conversation.Count > 1;

        var sb = new System.Text.StringBuilder();
        foreach (var (msgRow, msgDetail) in conversation)
        {
            var body = WrapClickableImages(
                SeparateQuotedThread(SanitizeHtmlForDisplay(msgDetail.BodyHtml), suppressNestedQuotes), msgRow.Id);
            var toLine = BuildRecipientLine("To", msgDetail.To);
            var ccLine = BuildRecipientLine("Cc", msgDetail.Cc);
            var senderLink = FormatAddressLink(msgDetail.From);
            var attachments = WithRealSizes(msgDetail.Attachments ?? []);
            var attachmentsHtml = "";
            if (attachments.Count > 0)
            {
                _conversationAttachments[msgRow.Id] = attachments;
                var chips = new System.Text.StringBuilder();
                for (var i = 0; i < attachments.Count; i++)
                {
                    var a = attachments[i];
                    chips.Append($"""
                        <a class="qattach" href="appattach://open?id={Uri.EscapeDataString(msgRow.Id)}&idx={i}">
                          <span class="qaicon">&#128206;</span>{Encode(a.Name)}<span class="qasize">{Encode(a.Size)}</span>
                        </a>
                        """);
                }
                var downloadAll = attachments.Count > 1
                    ? $"""<a class="qattach-all" href="appattach://downloadall?id={Uri.EscapeDataString(msgRow.Id)}">Download all {attachments.Count}</a>"""
                    : "";
                attachmentsHtml = $"""<div class="qattachments">{chips}{downloadAll}</div>""";
            }
            sb.Append($"""
                <div class="qcard">
                  <div class="qhead">
                    <div class="qavatar">{Encode(msgRow.Initial)}</div>
                    <div class="qwho">
                      <div class="qname">{senderLink}</div>
                      {toLine}
                      {ccLine}
                    </div>
                    <div class="qdate">{Encode(msgDetail.Date)}</div>
                  </div>
                  <div class="qbody">{body}</div>
                  {attachmentsHtml}
                </div>
                """);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Where attachments are materialised before being shown. Kept out of the user's Downloads
    /// folder — opening a mail shouldn't quietly litter it with files.
    /// </summary>
    private static string AttachmentCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IITBWebmailWrapper", "attachments");

    /// <summary>
    /// Gets a real file on disk for an attachment: generated for sample data, downloaded via IMAP
    /// for live mail. Returns null when it couldn't be obtained.
    /// </summary>
    private async Task<string?> ResolveAttachmentAsync(MailAttachment attachment)
    {
        if (UseMockData || string.IsNullOrEmpty(attachment.Url))
            return SampleAttachments.EnsureFile(AttachmentCacheDirectory, attachment.Name);

        // Keyed by a per-message subfolder (attachment.Url is "imap:{uid}:{index}"), not just the
        // filename — otherwise two different emails with a same-named attachment (e.g. "invoice.pdf")
        // would silently overwrite each other's cached file.
        var messageDir = Path.Combine(AttachmentCacheDirectory, SampleAttachments.SafeName(attachment.Url.Replace(':', '_')));
        Directory.CreateDirectory(messageDir);
        var path = Path.Combine(messageDir, SampleAttachments.SafeName(attachment.Name));

        StatusText.Text = $"Downloading {attachment.Name}…";
        return await _mail!.DownloadAttachmentAsync(attachment, path);
    }

    /// <summary>
    /// Copies files picked in the compose window into the attachment cache, so a message that has
    /// been "sent" on sample data can still open its own attachments afterwards.
    /// </summary>
    private IReadOnlyList<MailAttachment> StageAttachments(IReadOnlyList<ComposeAttachment> files)
    {
        if (files.Count == 0)
            return [];

        var staged = new List<MailAttachment>(files.Count);
        try
        {
            Directory.CreateDirectory(AttachmentCacheDirectory);
        }
        catch (Exception)
        {
            // Fall through: the chips are still worth showing even if the copy can't be made.
        }

        foreach (var file in files)
        {
            try
            {
                File.Copy(file.Path,
                    Path.Combine(AttachmentCacheDirectory, SampleAttachments.SafeName(file.Name)),
                    overwrite: true);
            }
            catch (Exception)
            {
                // Keep the chip; opening it will fall back to a generated sample file.
            }
            staged.Add(new MailAttachment(file.Name, file.Size));
        }
        return staged;
    }

    /// <summary>
    /// Sample attachments are generated on demand, so their declared size is whatever the sample
    /// data happened to claim. Replacing it with the real file size keeps the chip honest.
    /// </summary>
    private IReadOnlyList<MailAttachment> WithRealSizes(IReadOnlyList<MailAttachment> attachments)
    {
        if (!UseMockData)
            return attachments;

        var sized = new List<MailAttachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            try
            {
                var path = SampleAttachments.EnsureFile(AttachmentCacheDirectory, attachment.Name);
                sized.Add(attachment with { Size = AttachmentViewerWindow.FormatSize(new FileInfo(path).Length) });
            }
            catch (Exception)
            {
                sized.Add(attachment);
            }
        }
        return sized;
    }

    /// <summary>
    /// Routes an appattach://open?id=..&idx=.. or appattach://downloadall?id=.. link click (see
    /// BuildConversationHtml) back to a real attachment on a real message.
    /// </summary>
    private async void HandleAttachmentLink(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return;

        var query = parsed.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        if (!query.TryGetValue("id", out var id) || !_conversationAttachments.TryGetValue(id, out var attachments))
            return;

        if (parsed.Host.Equals("downloadall", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadAllAttachmentsAsync(attachments);
            return;
        }

        if (parsed.Host.Equals("open", StringComparison.OrdinalIgnoreCase)
            && query.TryGetValue("idx", out var idxStr) && int.TryParse(idxStr, out var idx)
            && idx >= 0 && idx < attachments.Count)
        {
            await OpenAttachmentAsync(attachments[idx]);
        }
    }

    /// <summary>
    /// Opens the attachment inside the app. Saving is still available, but from the viewer —
    /// clicking a PDF should show you the PDF, not immediately ask where to put it.
    /// </summary>
    private async Task OpenAttachmentAsync(MailAttachment attachment)
    {
        try
        {
            var path = await ResolveAttachmentAsync(attachment);
            if (path is null)
            {
                StatusText.Text = $"Couldn't download {attachment.Name}";
                return;
            }

            StatusText.Text = $"Opened {attachment.Name}";

            // Images get a small floating Quick Look-style panel instead of a full document
            // window — everything else (PDF, text) still goes to AttachmentViewerWindow, since a
            // plain Image control can't render those and Edge's own PDF viewer is already the
            // better experience for that one anyway.
            if (QuickLookWindow.CanPreview(attachment.Name))
                new QuickLookWindow(path, attachment.Name) { Owner = this }.Show();
            else
                new AttachmentViewerWindow(path, attachment.Name) { Owner = this }.Show();
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open {attachment.Name}: {ex.Message}";
        }
    }

    /// <summary>Saves every attachment on a message into one folder the user picks — a thread with
    /// several files (project data + notes, say) otherwise means clicking Save on each one
    /// individually.</summary>
    private async Task DownloadAllAttachmentsAsync(IReadOnlyList<MailAttachment> attachments)
    {
        if (attachments.Count == 0)
            return;

        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a folder to save attachments to" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        var saved = 0;
        foreach (var attachment in attachments)
        {
            try
            {
                var sourcePath = await ResolveAttachmentAsync(attachment);
                if (sourcePath is null)
                    continue;
                File.Copy(sourcePath, Path.Combine(dialog.SelectedPath, SampleAttachments.SafeName(attachment.Name)), overwrite: true);
                saved++;
            }
            catch (Exception)
            {
                // Best-effort — one bad attachment shouldn't abort the rest of the batch.
            }
        }

        StatusText.Text = saved > 0 ? $"Saved {saved} attachment(s) to {dialog.SelectedPath}" : "Couldn't save attachments";
    }

    // A weekday name or "today"/"tomorrow" paired with a clock time ("Tuesday ... 5 PM") is enough
    // to build a real-looking "when" line without needing to actually parse a calendar invite —
    // used only as a fallback for a plain message that has no real text/calendar part to read.
    private static readonly System.Text.RegularExpressions.Regex MeetingDayPattern = new(
        @"\b(today|tomorrow|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex MeetingTimePattern = new(
        @"\b\d{1,2}(:\d{2})?\s?(AM|PM|am|pm)\b");

    /// <summary>
    /// Scans the raw (pre-sanitize) message HTML for a Zoom/Meet/Teams join link, so the reading
    /// pane can surface a Gmail-style meeting card instead of just saying "this message contains a
    /// link". Checked against the unsanitized HTML since the link only ever lives in an href/text,
    /// neither of which SanitizeRemoteImages touches — but running it first keeps this independent
    /// of that step's behavior. Only used when the message has no real calendar invite to read
    /// (see MessageDetail.Calendar) — that's always the more accurate source when present.
    /// </summary>
    private static (string Label, string Url, string? When)? FindMeetingLink(string html)
    {
        if (Automation.MeetingLinkFinder.Find(html) is not (var label, var url))
            return null;

        var plainText = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        var day = MeetingDayPattern.Match(plainText);
        var time = MeetingTimePattern.Match(plainText);
        string? when = (day.Success, time.Success) switch
        {
            (true, true) => $"{Capitalize(day.Value)}, {time.Value.ToUpperInvariant()}",
            (false, true) => time.Value.ToUpperInvariant(),
            (true, false) => Capitalize(day.Value),
            _ => null,
        };

        return (label, url, when);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private void JoinMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_meetingLinkUrl is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(_meetingLinkUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open the meeting link: {ex.Message}";
        }
    }

    /// <summary>
    /// Shown the instant a row is clicked, before the async IMAP fetch for its full body even
    /// starts — the header (subject/sender/date/avatar) is already known from the row itself, so it
    /// updates immediately instead of lagging behind the list selection. Only the body has to wait,
    /// and shows an explicit "Loading…" placeholder rather than the *previous* message's content,
    /// which otherwise stays on screen long enough to look like the wrong mail opened.
    /// </summary>
    private void ShowReadingPaneLoading(InboxRow row)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        if (ReadingCard.Visibility != Visibility.Visible)
            ReadingCard.FadeIn(180);
        else
            ReadingCard.Visibility = Visibility.Visible;

        ReadingSubject.Text = row.Subject;
        ReadingFrom.Text = row.Sender;
        ReadingDate.Text = row.Date;
        ReadingAvatarInitial.Text = row.Initial;
        ReadingFromRow.Visibility = Visibility.Collapsed;
        ExternalSenderBar.Visibility = Visibility.Collapsed;
        MeetingBar.Visibility = Visibility.Collapsed;
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
        JoinMeetingButton.Visibility = Visibility.Visible;

        // A native overlay instead of a placeholder WebView2 navigation — navigating here just to
        // navigate again moments later once the real content arrives doubled WebView2's per-open
        // engine overhead for no visible benefit, which is what made opening a message feel slower
        // right after this loading state was added.
        ReadingPaneLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void ResetReadingPane()
    {
        _openRow = null;
        _openDetail = null;
        _blockedImagesHtml = null;
        _meetingLinkUrl = null;
        _conversationAttachments.Clear();
        _conversationImages.Clear();
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
        ExternalSenderBar.Visibility = Visibility.Collapsed;
        MeetingBar.Visibility = Visibility.Collapsed;
        JoinMeetingButton.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;
        ReadingCard.Visibility = Visibility.Collapsed;
        MessageList.SelectedItem = null;
    }

    // ---- Reply / forward ------------------------------------------------------------------

    private string SelfAddress => _account?.Email ?? MockData.SelfAddress;

    /// <summary>
    /// True when a sender's domain doesn't match your own — derived from your own signed-in
    /// address rather than a hardcoded organisation domain, so it still makes sense if this app is
    /// ever pointed at a different institution's mail. A subdomain of your own domain still counts
    /// as internal (mail from "cse.iitb.ac.in" when you're "you@iitb.ac.in" isn't "external").
    /// </summary>
    private bool IsExternalSender(string fromAddress)
    {
        var senderDomain = MailText.AddressOnly(fromAddress).Split('@').ElementAtOrDefault(1);
        var ownDomain = SelfAddress.Split('@').ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(senderDomain) || string.IsNullOrEmpty(ownDomain))
            return false;

        return !senderDomain.Equals(ownDomain, StringComparison.OrdinalIgnoreCase)
            && !senderDomain.EndsWith("." + ownDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Keeps InboxRow.SelfDomain in sync so the message list can show the same "external
    /// sender" indicator the reading pane shows, without every row needing to know about accounts.</summary>
    private void UpdateSelfDomain() =>
        InboxRow.SelfDomain = SelfAddress.Split('@').ElementAtOrDefault(1) ?? "";

    private static string ReplySubject(string subject) =>
        subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : $"Re: {subject}";

    private static string ForwardSubject(string subject) =>
        subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? subject : $"Fwd: {subject}";

    /// <summary>
    /// A reply body with the original message actually quoted below it: an attribution line, then
    /// the original text prefixed with "&gt; ". The leading blank lines are where the user types —
    /// top-posting, the way Gmail and Outlook both open a reply.
    /// </summary>
    private static string BuildReplyBody(MessageDetail detail)
    {
        var original = MailText.HtmlToPlainText(detail.BodyHtml);
        if (string.IsNullOrWhiteSpace(original))
            return "\n\n";

        return $"\n\n{AppleStyleAttribution(detail)}\n\n{MailText.Quote(original)}\n";
    }

    /// <summary>An absolute date for anything baked into a reply/forward's permanent text — see
    /// MailText.FormatAbsoluteDate for why that can't be the same relative "Today"/"Yesterday"
    /// wording detail.Date already carries for the live reading pane header.</summary>
    private static string QuoteDate(MessageDetail detail) =>
        detail.Timestamp is { } ts ? MailText.FormatAbsoluteDate(ts) : detail.Date;

    /// <summary>
    /// Apple Mail's exact quote attribution format: "On Aug 28, 2026, at 5:49 PM, Name
    /// &lt;address&gt; wrote:" — a real absolute date+time (never relative), "at" before the clock
    /// time, and the sender's address in brackets after their name, not just the display name alone.
    /// </summary>
    private static string AppleStyleAttribution(MessageDetail detail)
    {
        var name = MailText.DisplayName(detail.From);
        var address = MailText.AddressOnly(detail.From);
        var who = string.IsNullOrWhiteSpace(name) ? address : $"{name} <{address}>";
        return $"On {ForwardDate(detail)}, {who} wrote:";
    }

    /// <summary>"Aug 28, 2026 at 5:49 PM" — Apple Mail's absolute-date-plus-"at" wording, shared by
    /// the reply attribution and the forwarded-message header's own Date: line.</summary>
    private static string ForwardDate(MessageDetail detail) =>
        detail.Timestamp is { } ts ? ts.ToString("MMM d, yyyy 'at' h:mm tt") : QuoteDate(detail);

    /// <summary>
    /// A forward carries the original's headers as well as its text — a forwarded mail with no
    /// From/Date/Subject block is unreadable to whoever receives it.
    /// </summary>
    private static string BuildForwardBody(MessageDetail detail)
    {
        // Apple Mail's own forward wording and header order: "Begin forwarded message:", then
        // From/Subject/Date/To(/Cc) — not the "---------- Forwarded message ----------" dashes or
        // From/Date/Subject ordering other clients use.
        var header = new List<string>
        {
            "Begin forwarded message:",
            "",
            $"From: {detail.From}",
            $"Subject: {detail.Subject}",
            $"Date: {ForwardDate(detail)}",
        };
        if (!string.IsNullOrWhiteSpace(detail.To))
            header.Add($"To: {detail.To}");
        if (!string.IsNullOrWhiteSpace(detail.Cc))
            header.Add($"Cc: {detail.Cc}");
        if (detail.Attachments is { Count: > 0 } attachments)
            header.Add($"Attachments: {string.Join(", ", attachments.Select(a => a.Name))}");

        return $"\n\n{string.Join("\n", header)}\n\n{MailText.HtmlToPlainText(detail.BodyHtml)}\n";
    }

    private static readonly Regex ImageTagPattern = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase);

    /// <summary>
    /// Quoted originals keep their formatting but lose their images: a remote image in a quote
    /// would phone home the moment the compose window opened, before anything is even sent.
    /// </summary>
    private static string QuotableHtml(string html) => ImageTagPattern.Replace(html, "");

    private static string Encode(string text) => System.Net.WebUtility.HtmlEncode(text);

    private const string QuoteStyle =
        "margin:0 0 0 10px;padding:0 0 0 12px;border-left:3px solid #ddd;color:#555";

    /// <summary>
    /// The HTML counterpart of <see cref="BuildReplyBody"/>: the original goes into a real
    /// blockquote so it keeps its own formatting, rather than being flattened to "&gt; " lines.
    /// </summary>
    private static string BuildReplyBodyHtml(MessageDetail detail)
    {
        return "<p><br></p><p><br></p>"
            + $"<p>{Encode(AppleStyleAttribution(detail))}</p>"
            + $"<blockquote style=\"{QuoteStyle}\">{QuotableHtml(detail.BodyHtml)}</blockquote>";
    }

    private static string BuildForwardBodyHtml(MessageDetail detail)
    {
        var header = new System.Text.StringBuilder();
        header.Append("<p>Begin forwarded message:</p><p><br></p><p>");
        header.Append($"From: {Encode(detail.From)}<br>");
        header.Append($"Subject: {Encode(detail.Subject)}<br>");
        header.Append($"Date: {Encode(ForwardDate(detail))}");
        if (!string.IsNullOrWhiteSpace(detail.To))
            header.Append($"<br>To: {Encode(detail.To)}");
        if (!string.IsNullOrWhiteSpace(detail.Cc))
            header.Append($"<br>Cc: {Encode(detail.Cc)}");
        if (detail.Attachments is { Count: > 0 } attachments)
            header.Append($"<br>Attachments: {Encode(string.Join(", ", attachments.Select(a => a.Name)))}");
        header.Append("</p>");

        return $"<p><br></p><p><br></p>{header}{QuotableHtml(detail.BodyHtml)}";
    }

    private void ReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is not { } detail)
            return;
        OpenCompose(detail.From, ReplySubject(detail.Subject), BuildReplyBody(detail),
            bodyHtml: WithSignature(BuildReplyBodyHtml(detail)));
    }

    private void ReplyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is not { } detail)
            return;

        var cc = MailText.MergeRecipients(
            [SelfAddress, MailText.AddressOnly(detail.From)], detail.To, detail.Cc);

        OpenCompose(detail.From, ReplySubject(detail.Subject), BuildReplyBody(detail), cc,
            bodyHtml: WithSignature(BuildReplyBodyHtml(detail)));
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openDetail is not { } detail)
            return;
        OpenCompose("", ForwardSubject(detail.Subject), BuildForwardBody(detail),
            bodyHtml: WithSignature(BuildForwardBodyHtml(detail)));
    }

    /// <summary>Saves the exact RFC 822 source to a temp file and opens it as plain text — the
    /// fastest way to see precisely what a server actually sent when a message isn't rendering the
    /// way it should (mailing-list headers/footers, an unusual MIME structure, etc.), matching
    /// Thunderbird's own "View source" (Ctrl+U).</summary>
    private async void ViewSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRow is not { } row)
            return;

        string? source;
        if (UseMockData)
        {
            source = _openDetail is { } d
                ? $"Subject: {d.Subject}\nFrom: {d.From}\nDate: {d.Date}\n\n{d.BodyHtml}"
                : null;
        }
        else
        {
            try { source = await _mail!.GetRawSourceAsync(row.Id); }
            catch (Exception) { source = null; }
        }

        if (source is null)
        {
            StatusText.Text = "Couldn't get the message source";
            return;
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"message-source-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, source);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open the source: {ex.Message}";
        }
    }

    private async void MarkUnreadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRow is not { } open)
            return;

        if (!UseMockData && !await RunLiveAsync(
                () => _mail!.SetReadAsync(open.Id, false), "Marked as unread", "Couldn't mark as unread"))
            return;

        UpdateRow(open.Id, r => r with { Unread = true });
        UpdateInboxBadge();
        _messagesView.Refresh();
        ResetReadingPane();

        if (!UseMockData)
            await RefreshFoldersAsync();
    }

    private async void ArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRow is { } open)
            await ArchiveMessageAsync(open);
    }

    /// <summary>
    /// Archiving files the message away — it should still be findable afterwards. On sample data
    /// that means moving it to the Archive folder rather than deleting it outright.
    /// </summary>
    private async Task ArchiveMessageAsync(InboxRow row)
    {
        if (!UseMockData)
        {
            await RemoveMessageAsync(row, id => _mail!.ArchiveAsync(id), "Archived");
            return;
        }

        MoveRowLocally(row.Id, "Archive");
        if (_openRow is not null && _openRow.Id == row.Id)
            ResetReadingPane();
        ApplyCurrentFolderView();
        UpdateInboxBadge();
        StatusText.Text = "Archived (sample data)";
    }

    /// <summary>Sample-data move: takes a row out of whichever folder holds it and files it in another.</summary>
    private bool MoveRowLocally(string id, string folder)
    {
        var row = _folderData.Values.SelectMany(rows => rows).FirstOrDefault(r => r.Id == id)
            ?? _messages.FirstOrDefault(r => r.Id == id);
        if (row is null || !_folderData.TryGetValue(folder, out var target))
            return false;

        RemoveMessageEverywhere(id);
        target.Insert(0, row);
        return true;
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e) =>
        await RemoveOpenMessageAsync(id => _mail!.DeleteAsync(id), "Deleted");

    private async Task RemoveOpenMessageAsync(Func<string, Task<bool>> liveAction, string verb)
    {
        if (_openRow is { } open)
            await RemoveMessageAsync(open, liveAction, verb);
    }

    private async Task RemoveMessageAsync(InboxRow row, Func<string, Task<bool>> liveAction, string verb)
    {
        if (!UseMockData && !await RunLiveAsync(
                () => liveAction(row.Id), verb, $"Couldn't {verb.ToLowerInvariant().TrimEnd('d')} the message"))
            return;

        // Set by ArchiveAsync/DeleteAsync when the server told us the message's new UID in its
        // destination — only then is there anything to actually move back on Undo.
        var undo = UseMockData ? null : _mail!.ConsumeLastMove();

        RemoveMessageEverywhere(row.Id);
        UpdateListEmptyState();
        UpdateInboxBadge();

        if (_openRow is not null && _openRow.Id == row.Id)
            ResetReadingPane();

        if (UseMockData)
        {
            StatusText.Text = $"{verb} (sample data)";
            return;
        }

        if (undo is { } move)
        {
            ShowActionUndo(verb, async () =>
            {
                if (await _mail!.UndoMoveAsync(move))
                    await RefreshMessagesAsync();
                else
                    StatusText.Text = "Couldn't undo — the message may have moved again since";
            });
        }

        await RefreshMessagesAsync();
    }

    // Delete-to-remove is a near-universal mail-client convention (Gmail, Outlook, Apple Mail).
    private async void MessageList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete)
            return;
        if (MessageList.SelectedItem is not InboxRow row)
            return;
        e.Handled = true;
        await RemoveMessageAsync(row, id => _mail!.DeleteAsync(id), "Deleted");
    }

    // ---- Keyboard shortcuts (Gmail-style) ---------------------------------------------------

    private bool _gPending;
    private DispatcherTimer? _gPendingTimer;

    private async void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
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

        // '?' (Shift+/) opens Help (now part of the account page); plain '/' focuses search.
        if (e.Key == System.Windows.Input.Key.OemQuestion)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                OpenAccountWindow(existing: _account, startPage: AccountPage.Help);
            else
                SearchBox.Focus();
            e.Handled = true;
            return;
        }

        // '#' (Shift+3) deletes, matching Gmail.
        if (e.Key == System.Windows.Input.Key.D3 && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            e.Handled = true;
            if (_openRow is not null)
                await RemoveOpenMessageAsync(id => _mail!.DeleteAsync(id), "Deleted");
            else if (MessageList.SelectedItem is InboxRow row)
                await RemoveMessageAsync(row, id => _mail!.DeleteAsync(id), "Deleted");
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (_gPending)
        {
            _gPending = false;
            _gPendingTimer?.Stop();
            e.Handled = true;
            switch (e.Key)
            {
                case System.Windows.Input.Key.I: await GoToFolderAsync("Inbox"); break;
                case System.Windows.Input.Key.S: await GoToFolderAsync("Sent"); break;
                case System.Windows.Input.Key.D: await GoToFolderAsync("Drafts"); break;
                case System.Windows.Input.Key.A: await GoToFolderAsync("Archive"); break;
                case System.Windows.Input.Key.T: await GoToFolderAsync("Trash"); break;
            }
            return;
        }

        switch (e.Key)
        {
            case System.Windows.Input.Key.C:
                OpenCompose(bodyHtml: NewMessageBodyHtml);
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
                e.Handled = true;
                if (_openRow is { } toArchive)
                    await ArchiveMessageAsync(toArchive);
                break;
            case System.Windows.Input.Key.G:
                _gPending = true;
                if (_gPendingTimer is null)
                {
                    _gPendingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                    _gPendingTimer.Tick += (_, _) => { _gPending = false; _gPendingTimer!.Stop(); };
                }
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

    // ---- Signature --------------------------------------------------------------------------
    // Editing now lives in the account popup's Signature tab (see OpenLoginWindow) rather than a
    // separate window/toolbar button.

    /// <summary>The default signature as HTML, or "" when none is configured.</summary>
    private string SignatureHtml =>
        _settings.DefaultSignatureIndex >= 0 && _settings.DefaultSignatureIndex < _settings.Signatures.Count
            ? _settings.Signatures[_settings.DefaultSignatureIndex].BodyHtml
            : "";

    /// <summary>
    /// Leading blank paragraphs a fresh compose or a quoted reply/forward body starts with — where
    /// the signature gets spliced in, right after where the user types and before any quoted
    /// original, matching where Gmail/Outlook place an auto-inserted signature by default.
    /// </summary>
    private const string ComposeLeadIn = "<p><br></p><p><br></p>";

    /// <summary>Body HTML for a brand-new message: just the signature (if any), above an empty line to type into.</summary>
    private string NewMessageBodyHtml => SignatureHtml.Length == 0 ? "" : $"{ComposeLeadIn}{SignatureHtml}";

    /// <summary>Splices the signature into a reply/forward body that already starts with <see cref="ComposeLeadIn"/>.</summary>
    private string WithSignature(string bodyHtml) =>
        SignatureHtml.Length == 0 || !bodyHtml.StartsWith(ComposeLeadIn, StringComparison.Ordinal)
            ? bodyHtml
            : bodyHtml.Insert(ComposeLeadIn.Length, SignatureHtml);

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
            Hide();
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

        // Closing the X button minimizes to tray instead of quitting.
        e.Cancel = true;
        Hide();
    }
}
