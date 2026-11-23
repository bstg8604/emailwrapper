using System.IO;
using EmailClient.Automation;
using EmailClient.Diagnostics;
using EmailClient.UI;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace EmailClient.Mail;

/// <summary>
/// Drives the account for real over IMAP (reading) and SMTP (sending) via MailKit, instead of
/// puppeting the Roundcube web UI. Method names deliberately mirror the old <c>DomBridge</c>'s
/// surface (<c>ListFoldersAsync</c>, <c>SetFlaggedAsync</c>, etc.) so <c>MainWindow</c>'s call
/// sites barely changed shape — only what answers them did.
///
/// One IMAP connection (<see cref="_imap"/>) is held open and reused for the app's lifetime for
/// every command (list/fetch/move/etc.); a dedicated SMTP connection is opened per send, since
/// sends are infrequent and short-lived.
///
/// A second, read-only connection (<see cref="_idleClient"/>) is opened solely to sit in IMAP IDLE
/// on the current mailbox — IDLE occupies a connection for as long as it's active, so it can't
/// share the command connection above without every other method first having to interrupt it.
/// A second lightweight login is a small, worthwhile trade for real push instead of polling every
/// 60 seconds for new mail.
/// </summary>
public sealed class ImapMailBackend : IAsyncDisposable
{
    /// <summary>
    /// MailKit's own default (120,000ms/2 minutes) bounds every socket operation including the
    /// initial connect — but a live report showed the app appearing to hang indefinitely with no
    /// error, status text frozen on "Opening Inbox…" while the connection dot had already flipped
    /// to Offline. Two minutes of apparent freeze before any error can surface reads as "stuck",
    /// not "slow" — a campus network dropping IMAPS packets silently (rather than actively
    /// refusing the connection) leaves nothing to distinguish "still trying" from "hung forever"
    /// until this fires. 20 seconds is generous for a real LAN/campus connection while still
    /// failing fast enough that a genuinely stuck attempt reads as an error, not a freeze.
    /// </summary>
    private const int NetworkTimeoutMs = 20_000;

    private readonly AccountSettings _account;
    private readonly ImapClient _imap = new() { Timeout = NetworkTimeoutMs };
    private readonly Dictionary<string, IMailFolder> _foldersByMailbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly ContactsIndex _contacts = new();

    private IMailFolder? _current;
    private string _currentMailbox = "INBOX";
    private int _page = 1;
    private const int PageSize = 50;

    /// <summary>The page NextPageAsync/PreviousPageAsync have already advanced to — the single
    /// authoritative page cursor for whatever folder is open. MainWindow's own RefreshMessagesAsync
    /// reads this (rather than keeping a second, separately-tracked page number) specifically so
    /// paging forward/back and then reading from MailStore ask for the page that was actually just
    /// navigated to, not a stale one.</summary>
    public int CurrentPage => _page;

    // ---- IMAP IDLE (push new-mail notifications) -------------------------------------------
    private ImapClient? _idleClient;
    private CancellationTokenSource? _idleCts;
    private Task? _idleLoopTask;

    /// <summary>Raised (on a background thread — marshal to the UI thread before touching
    /// controls) whenever the watched mailbox's message count changes: a new arrival or an
    /// expunge the server told us about without being asked.</summary>
    public event Action? MailboxActivity;

    /// <summary>
    /// Raised (on a background thread) when the push/poll/offline situation changes, so the UI can
    /// say so. Until this existed, IDLE could be abandoned for the whole session without a trace.
    /// </summary>
    public event Action<ConnectionState>? ConnectionStateChanged;

    private ConnectionState _connectionState = ConnectionState.Connecting;

    /// <summary>Latest known connection state; only raises the event when it actually changes.</summary>
    public ConnectionState State => _connectionState;

    private void SetState(ConnectionState state)
    {
        if (_connectionState == state)
            return;
        _connectionState = state;
        Log.Info($"Connection state: {state}");
        ConnectionStateChanged?.Invoke(state);
    }

    /// <summary>
    /// Starts (or restarts, if already running against a different mailbox) a background IDLE loop
    /// watching <paramref name="mailbox"/>. Safe to call repeatedly — e.g. every time the user
    /// switches folders — since it always tears down any previous loop first.
    /// </summary>
    public void StartIdleMonitor(string mailbox)
    {
        StopIdleMonitor();
        _idleCts = new CancellationTokenSource();
        _idleLoopTask = RunIdleLoopAsync(mailbox, _idleCts.Token);
    }

    public void StopIdleMonitor()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        _idleCts = null;
    }

    // If the server won't allow this second connection at all (a hard per-account session cap is
    // common on university mail servers), retrying forever would mean re-authenticating with the
    // real password every 30 seconds indefinitely — enough repeated login attempts to trip a mail
    // server's own brute-force lockout. Give up on IDLE for this session after a few tries in a
    // row; the 60-second poll timer still covers new mail without it.
    private const int MaxConsecutiveIdleFailures = 3;

    private async Task RunIdleLoopAsync(string mailbox, CancellationToken token)
    {
        var consecutiveFailures = 0;
        while (!token.IsCancellationRequested && consecutiveFailures < MaxConsecutiveIdleFailures)
        {
            ImapClient? client = null;
            try
            {
                client = new ImapClient { Timeout = NetworkTimeoutMs };
                await client.ConnectAsync(_account.ImapHost, _account.ImapPort, SecureSocketOptions.SslOnConnect, token);
                await client.AuthenticateAsync(_account.LoginName, _account.Password, token);
                var folder = await client.GetFolderAsync(mailbox, token) ?? client.Inbox;
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
                _idleClient = client;

                // Reached IDLE at all, so the connection/login/open sequence genuinely works —
                // resets the breaker so a later transient blip doesn't inherit an unrelated streak.
                consecutiveFailures = 0;
                SetState(ConnectionState.Push);

                void OnCountChanged(object? s, EventArgs e) => MailboxActivity?.Invoke();
                folder.CountChanged += OnCountChanged;
                try
                {
                    // A real IDLE command times out server-side after ~30 minutes of inactivity;
                    // re-issuing it every 9 minutes (MailKit's own documented example uses the same
                    // figure) keeps well clear of that regardless of server policy.
                    while (!token.IsCancellationRequested)
                    {
                        using var doneSource = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                        try
                        {
                            await client.IdleAsync(doneSource.Token, token);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            // Just the periodic re-issue timing out — loop around and idle again.
                        }
                    }
                }
                finally
                {
                    folder.CountChanged -= OnCountChanged;
                }
            }
            catch (OperationCanceledException)
            {
                // Requested shutdown (folder switch or sign-out) — exit quietly.
            }
            catch (Exception ex)
            {
                // Transient failure (network blip, server hiccup, or the server just doesn't allow
                // a second session) — back off and reconnect rather than letting the whole loop
                // die immediately; the 60-second poll timer still covers new mail regardless. But
                // give up for good once MaxConsecutiveIdleFailures is hit (see its own comment).
                consecutiveFailures++;
                Log.Warn($"IMAP IDLE attempt {consecutiveFailures}/{MaxConsecutiveIdleFailures} failed", ex);
                SetState(ConnectionState.Polling);
                if (consecutiveFailures >= MaxConsecutiveIdleFailures)
                    break;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
                catch (OperationCanceledException)
                {
                }
            }
            finally
            {
                try
                {
                    if (client is { IsConnected: true })
                        await client.DisconnectAsync(true);
                }
                catch (Exception)
                {
                }
                client?.Dispose();
                if (ReferenceEquals(_idleClient, client))
                    _idleClient = null;
            }
        }

        // Falling out of the loop without cancellation means the breaker tripped: push is gone for
        // the rest of this session and the 60-second poll is now the only thing finding new mail.
        // That used to happen in complete silence.
        if (!token.IsCancellationRequested)
        {
            Log.Warn($"Giving up on IMAP IDLE after {MaxConsecutiveIdleFailures} consecutive failures — falling back to polling.");
            SetState(ConnectionState.Polling);
        }
    }

    // Caches the last opened message so downloading one of its attachments doesn't need a second
    // round trip to re-fetch the whole thing.
    private UniqueId? _cachedUid;
    private MimeMessage? _cachedMessage;

    public string SelfAddress => _account.Email;
    public ContactsIndex Contacts => _contacts;

    public ImapMailBackend(AccountSettings account) => _account = account;

    // ---- Thread-safety ----------------------------------------------------------------------

    /// <summary>
    /// MailKit's ImapClient can't run two commands concurrently on one connection — attempting to
    /// do so throws "The ImapClient is currently busy processing a command in another thread."
    /// Confirmed live: the 60-second poll tick and an IDLE-triggered refresh raced here, and since
    /// the exception aborted the refresh before it ever reached the new-mail notification code,
    /// this was the actual cause of notifications sometimes just not showing up — not a
    /// notification-logic bug at all. Every public method that issues a real command on
    /// <see cref="_imap"/> acquires this first via <see cref="AcquireImapLockAsync"/>.
    /// </summary>
    private readonly SemaphoreSlim _imapLock = new(1, 1);

    /// <summary>
    /// Tracks which instance's lock (if any) the *current* async call chain already holds — a
    /// locked method calling another locked method on the same instance (e.g. RenameFolderAsync
    /// calling SelectFolderAsync, or SaveDraftAsync's 2-arg overload calling the 3-arg one) would
    /// otherwise deadlock trying to re-acquire a semaphore that isn't reentrant. Keyed by instance
    /// (not just a bool) so this stays correct if the app ever holds two accounts' backends live
    /// at once — a lock held on one must never be mistaken for one held on another.
    /// </summary>
    private static readonly AsyncLocal<ImapMailBackend?> _lockHolder = new();

    /// <summary>
    /// Generous on purpose — several legitimately queued operations can each take up to
    /// NetworkTimeoutMs while they wait their turn for this same lock, and this only needs to
    /// catch the case that shouldn't happen at all: something holding the lock well past what any
    /// realistic queue depth could explain (a future bug, a code path that skips the per-operation
    /// timeout). Defense in depth for the one thing NetworkTimeoutMs itself can't cover — every
    /// operation under the lock respecting its own timeout is what actually keeps this from firing
    /// in practice.
    /// </summary>
    private const int LockTimeoutMs = 60_000;

    private Task<IDisposable> AcquireImapLockAsync() => AcquireImapLockAsync(CancellationToken.None);

    /// <param name="ct">Cancels only the *wait* for the lock, never a command already running under
    /// it — a caller that's given up on its own result (see FindConversationSiblingsAsync's own
    /// cancellation checks) can stop queuing for a connection it no longer needs without touching
    /// whatever's currently mid-command. SemaphoreSlim.WaitAsync's own cancellation support does
    /// exactly this safely: it only ever cancels the *waiting*, never something already granted.</param>
    private async Task<IDisposable> AcquireImapLockAsync(CancellationToken ct)
    {
        if (ReferenceEquals(_lockHolder.Value, this))
            return NullReleaser.Instance;
        bool acquired;
        try
        {
            acquired = await _imapLock.WaitAsync(LockTimeoutMs, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // the caller's own cancellation — not a busy-connection timeout, don't relabel it
        }
        if (!acquired)
            throw new TimeoutException("The mail server connection is busy and didn't free up in time.");
        _lockHolder.Value = this;
        return new ImapLockReleaser(this);
    }

    private sealed class NullReleaser : IDisposable
    {
        public static readonly NullReleaser Instance = new();
        public void Dispose() { }
    }

    private sealed class ImapLockReleaser(ImapMailBackend owner) : IDisposable
    {
        public void Dispose()
        {
            _lockHolder.Value = null;
            owner._imapLock.Release();
        }
    }

    // ---- Connection -----------------------------------------------------------------------

    /// <param name="skipContactsScan">True when the caller already has a persisted copy of
    /// contacts from a previous session (MailStore's own `contacts` table) worth trusting for now —
    /// the scan itself is a real, several-second IMAP cost (up to ~800 envelope fetches across
    /// Inbox+Sent) paid on every single sign-in otherwise, which is most of why a fresh app launch
    /// felt slow to reach "Connected" even after the local-database rewiring elsewhere made
    /// everything *else* about sign-in fast. Skipping it here doesn't lose the data — MainWindow
    /// already seeded MailStore from a previous successful scan — and MailSyncEngine's own periodic
    /// call to <see cref="RescanContactsAsync"/> keeps it from ever going stale for long, so this
    /// genuinely isn't paying for freshness with staleness, just moving the (still-recurring) cost
    /// off the one moment — sign-in — where a user is actually staring at the screen waiting.</param>
    public async Task ConnectAsync(bool skipContactsScan = false)
    {
        using var _ = await AcquireImapLockAsync();
        SetState(ConnectionState.Connecting);
        Log.Info($"Connecting to {_account.ImapHost}:{_account.ImapPort} as {_account.LoginName}");
        await _imap.ConnectAsync(_account.ImapHost, _account.ImapPort, SecureSocketOptions.SslOnConnect);
        // Deliberately not caught/rewrapped here: MailErrors.Friendly() already has a dedicated
        // "wrong email or password" message for AuthenticationException. Wrapping it into
        // SessionExpiredException made every rejected password show "you've been signed out"
        // instead, which is wrong on a first sign-in attempt (there was never a session to expire).
        await _imap.AuthenticateAsync(_account.LoginName, _account.Password);

        await IndexFoldersAsync();

        var inbox = _imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        _current = inbox;
        _currentMailbox = inbox.FullName;

        // Must be awaited, not fire-and-forget: MailKit's ImapClient can't run two commands
        // concurrently on the same connection, and every caller of ConnectAsync immediately issues
        // more commands on this same _imap right after this method returns (loading folders,
        // listing messages). Firing this in the background used to race those and surface as a
        // confusing "the ImapClient is busy" failure on the very first refresh after signing in —
        // one that also left the message list showing whatever was there before (stale sample
        // data) since the exception aborted the refresh before it could clear it.
        if (!skipContactsScan)
            await _contacts.BuildAsync(_imap, _account.Email);

        // Push only counts once the IDLE loop actually reaches IDLE — it reports that itself. Until
        // then (or if it already gave up) the 60-second poll is what's finding mail.
        SetState(_idleClient is { IsConnected: true } ? ConnectionState.Push : ConnectionState.Polling);
        Log.Info("Connected.");
    }

    /// <summary>Re-scans Inbox+Sent for contacts — the same scan ConnectAsync itself can skip via
    /// <c>skipContactsScan</c>, callable safely from anywhere else (MailSyncEngine's own periodic
    /// call, specifically). ContactsIndex.BuildAsync talks to the shared `_imap` client directly,
    /// with no locking of its own — never a problem while its only caller was ConnectAsync (which
    /// already holds the lock for its whole duration), but a real one for any other caller without
    /// this wrapper: two unrelated commands racing on one IMAP connection is exactly the protocol
    /// desync this app's shared lock exists to prevent everywhere else.</summary>
    public async Task RescanContactsAsync(CancellationToken ct = default)
    {
        using var _ = await AcquireImapLockAsync(ct);
        await EnsureConnectedAsync();
        await _contacts.BuildAsync(_imap, _account.Email);
    }

    private async Task IndexFoldersAsync()
    {
        _foldersByMailbox.Clear();
        foreach (var folder in await AllFoldersAsync())
            _foldersByMailbox[folder.FullName] = folder;
        // The special-mailbox map (see GetSpecialMailboxesAsync) is only ever valid against this
        // exact folder set — invalidated whenever the set changes (rename/delete/create), same as
        // _foldersByMailbox itself.
        _specialMailboxesCache = null;
    }

    private async Task<List<IMailFolder>> AllFoldersAsync()
    {
        var result = new List<IMailFolder>();
        foreach (var ns in _imap.PersonalNamespaces)
        {
            var top = await _imap.GetFoldersAsync(ns);
            await Walk(top);
        }
        return result;

        async Task Walk(IEnumerable<IMailFolder> folders)
        {
            foreach (var folder in folders)
            {
                result.Add(folder);
                IList<IMailFolder> subfolders;
                try
                {
                    subfolders = await folder.GetSubfoldersAsync(false);
                }
                catch (Exception)
                {
                    continue; // \Noselect or a transient error — not fatal to the rest of the tree
                }
                if (subfolders.Count > 0)
                    await Walk(subfolders);
            }
        }
    }

    // ---- Special mailboxes ------------------------------------------------------------------

    private static readonly (string Key, SpecialFolder Special, string[] FallbackNames)[] SpecialFolders =
    [
        ("Sent", MailKit.SpecialFolder.Sent, ["Sent", "Sent Items", "Sent Messages"]),
        ("Drafts", MailKit.SpecialFolder.Drafts, ["Drafts"]),
        ("Trash", MailKit.SpecialFolder.Trash, ["Trash", "Deleted Items", "Deleted Messages"]),
        ("Junk", MailKit.SpecialFolder.Junk, ["Junk", "Spam", "Junk E-mail"]),
        ("Archive", MailKit.SpecialFolder.Archive, ["Archive", "All Mail"]),
    ];

    // Resolved once per connection (see IndexFoldersAsync's own invalidation) rather than re-derived
    // on every single call — GetSpecialMailboxesAsync used to be called fresh (several _imap.GetFolder
    // round trips, all under the shared lock) from every send, save-draft, archive, delete and move,
    // even though the Inbox/Sent/Drafts/Trash mapping essentially never changes mid-session. That
    // repeated, avoidable cost was itself a real contributor to the shared IMAP connection staying
    // busy long enough for unrelated operations to time out waiting for it.
    private IReadOnlyDictionary<string, string>? _specialMailboxesCache;

    public async Task<IReadOnlyDictionary<string, string>> GetSpecialMailboxesAsync()
    {
        if (_specialMailboxesCache is { } cached)
            return cached;

        using var _ = await AcquireImapLockAsync();
        // Re-check under the lock: a concurrent first caller may have already populated this while
        // this call was waiting its turn.
        if (_specialMailboxesCache is { } cachedAfterWait)
            return cachedAfterWait;

        var map = new Dictionary<string, string> { ["Inbox"] = _imap.Inbox.FullName };

        foreach (var (key, special, fallbackNames) in SpecialFolders)
        {
            // Prefer the server's own SPECIAL-USE tag (RFC 6154) over guessing by name — it's
            // authoritative when present, and IMAP servers don't agree on display names otherwise.
            IMailFolder? folder = null;
            try { folder = _imap.GetFolder(special); } catch (Exception) { /* not advertised */ }

            folder ??= _foldersByMailbox.Values.FirstOrDefault(f =>
                fallbackNames.Any(name => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)));

            if (folder is not null)
                map[key] = folder.FullName;
        }

        _specialMailboxesCache = map;
        return map;
    }

    private async Task<IMailFolder?> ResolveSpecialAsync(string key)
    {
        var map = await GetSpecialMailboxesAsync();
        return map.TryGetValue(key, out var mailbox) && _foldersByMailbox.TryGetValue(mailbox, out var folder)
            ? folder
            : null;
    }

    // ---- Folders ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Automation.MailFolder>> ListFoldersAsync()
    {
        using var _ = await AcquireImapLockAsync();
        var result = new List<Automation.MailFolder>();
        foreach (var folder in _foldersByMailbox.Values)
        {
            if (!folder.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                var unread = 0;
                var total = 0;
                try
                {
                    // Count alongside Unread — this is what lets the message-list pager show (and
                    // page through) a folder's real size instead of capping at whatever
                    // MailSyncEngine has synced so far.
                    await folder.StatusAsync(StatusItems.Unread | StatusItems.Count);
                    unread = folder.Unread;
                    total = folder.Count;
                }
                catch (Exception)
                {
                    // Some servers don't support STATUS on every folder — the badge just stays 0.
                }

                var depth = string.IsNullOrEmpty(folder.FullName)
                    ? 0
                    : folder.FullName.Split(folder.DirectorySeparator).Length - 1;

                result.Add(new Automation.MailFolder(folder.FullName, folder.Name, unread, Math.Max(0, depth), total));
            }
        }
        return result;
    }

    /// <summary>Creates a new top-level folder — used by the account page's Folders section.</summary>
    public async Task CreateFolderAsync(string name)
    {
        using var _ = await AcquireImapLockAsync();
        var root = _imap.GetFolder(_imap.PersonalNamespaces[0]);
        await root.CreateAsync(name, isMessageFolder: true);
        await IndexFoldersAsync();
    }

    public async Task RenameFolderAsync(string mailbox, string newName)
    {
        using var _ = await AcquireImapLockAsync();
        if (!_foldersByMailbox.TryGetValue(mailbox, out var folder))
            throw new InvalidOperationException("That folder no longer exists.");

        // Top-level folders have no ParentFolder — rename relative to the namespace root instead.
        var parent = folder.ParentFolder ?? _imap.GetFolder(_imap.PersonalNamespaces[0]);
        var wasCurrent = mailbox == _currentMailbox;
        await folder.RenameAsync(parent, newName);
        await IndexFoldersAsync();

        // Renaming the currently-open mailbox invalidates _current/_currentMailbox (the IMailFolder
        // object now points at a closed/stale name) — fall back to Inbox rather than leaving stale
        // state that would throw on the next fetch.
        if (wasCurrent)
            await SelectFolderAsync(_imap.Inbox.FullName);
    }

    public async Task DeleteFolderAsync(string mailbox)
    {
        using var _ = await AcquireImapLockAsync();
        if (!_foldersByMailbox.TryGetValue(mailbox, out var folder))
            throw new InvalidOperationException("That folder no longer exists.");

        var wasCurrent = mailbox == _currentMailbox;
        await folder.DeleteAsync();
        await IndexFoldersAsync();

        if (wasCurrent)
            await SelectFolderAsync(_imap.Inbox.FullName);
    }

    /// <summary>False only when the mailbox itself isn't known; a failure to actually open it
    /// (network, permissions, the folder having been deleted server-side) propagates instead of
    /// being swallowed, so the caller can tell the user why rather than just "couldn't open".</summary>
    public async Task<bool> SelectFolderAsync(string mailbox)
    {
        using var _ = await AcquireImapLockAsync();
        if (!_foldersByMailbox.TryGetValue(mailbox, out var folder))
            return false;

        await folder.OpenAsync(FolderAccess.ReadWrite);

        _current = folder;
        _currentMailbox = folder.FullName;
        _page = 1;
        return true;
    }

    // ---- Message list --------------------------------------------------------------------------

    /// <summary>
    /// A dropped connection (network blip, server-side idle timeout) otherwise leaves every
    /// subsequent call failing forever — nothing previously re-established it. Called at the top of
    /// the message list fetch, which runs on every 60s poll tick, so a lost connection self-heals on
    /// its own without the user having to manually sign in again. Reopens whatever folder was
    /// current before falling back to Inbox (what a fresh <see cref="ConnectAsync"/> opens).
    /// </summary>
    // A live report of the app "slowly becoming very high latency" traced back to exactly this
    // method during a flaky connection: every operation queued behind the shared lock (several
    // folders' worth of MailSyncEngine syncs, plus a manual refresh) independently found the
    // connection down and independently paid the *full* connect timeout trying to reconnect,
    // one after another — four separate ~20-second-plus attempts stacking into well over a
    // minute of cumulative blocking, all serialized behind the one lock everything else needed
    // too. _lastConnectFailureUtc/ReconnectCooldown fixes that: once one attempt fails, every
    // other caller queued up right behind it fails fast instead of repeating the same doomed
    // attempt.
    private DateTime? _lastConnectFailureUtc;
    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(10);

    private async Task EnsureConnectedAsync()
    {
        if (_imap.IsConnected && _imap.IsAuthenticated)
        {
            // The connection itself can survive while the selected mailbox doesn't — a server-side
            // deselect that isn't a full disconnect. Reopening here (rather than only reconnecting
            // from scratch below) catches that case too, instead of every subsequent command
            // failing with "the folder is not currently open" until the app is restarted.
            if (_current is { IsOpen: false } stale)
            {
                try
                {
                    await stale.OpenAsync(FolderAccess.ReadWrite);
                }
                catch (Exception)
                {
                    // Falls through to the full reconnect path below.
                }
            }
            if (_current?.IsOpen != false)
                return;
        }

        // Getting here means the live connection is gone or unusable.
        SetState(ConnectionState.Offline);

        if (_lastConnectFailureUtc is { } lastFailure && DateTime.UtcNow - lastFailure < ReconnectCooldown)
        {
            // See this method's own remarks above — the network almost certainly hasn't recovered
            // in the last few seconds, so this fails fast rather than repeating the same doomed
            // attempt every other operation queued behind the same lock would otherwise also pay
            // the full timeout for, one at a time.
            throw new IOException("The mail server was unreachable moments ago — not retrying yet.");
        }

        Log.Info("IMAP connection lost or mailbox deselected — reconnecting.");

        var wantedMailbox = _currentMailbox;
        try
        {
            if (_imap.IsConnected)
                await _imap.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            // Best-effort cleanup before reconnecting from scratch.
            Log.Debug("Disconnect before reconnect failed: " + ex.Message);
        }

        try
        {
            await ConnectAsync();
            _lastConnectFailureUtc = null;
        }
        catch (Exception)
        {
            _lastConnectFailureUtc = DateTime.UtcNow;
            throw;
        }

        if (!string.Equals(wantedMailbox, _current?.FullName, StringComparison.OrdinalIgnoreCase)
            && _foldersByMailbox.TryGetValue(wantedMailbox, out var folder))
        {
            try
            {
                await folder.OpenAsync(FolderAccess.ReadWrite);
                _current = folder;
                _currentMailbox = folder.FullName;
            }
            catch (Exception ex)
            {
                // Falls back to whatever ConnectAsync already opened (Inbox).
                Log.Warn($"Couldn't reopen '{wantedMailbox}' after reconnecting", ex);
            }
        }
    }

    /// <summary>
    /// Runs one IMAP operation, retrying it exactly once if the connection turns out to have died
    /// in the gap between EnsureConnectedAsync's own check and the operation actually reaching the
    /// server — a real, observed race (a live report showed "Couldn't open Inbox: The ImapClient is
    /// not connected" moments after a successful EnsureConnectedAsync, correlated with the server/
    /// proxy independently dropping the IDLE connection around the same time — this client's own
    /// main connection can just as easily die in that same window), not defensive paranoia.
    /// EnsureConnectedAsync's own pre-flight check narrows how often this actually has anything to
    /// retry; this only covers the specific remaining case where that check just barely lost the
    /// race. Callers still call EnsureConnectedAsync themselves at the top of the wrapped operation,
    /// same as before — calling it twice on a retry is harmless (it's a no-op once already connected).
    /// </summary>
    private static async Task<T> WithReconnectRetryAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException or IOException)
        {
            Log.Debug($"IMAP operation hit a dead connection ({ex.GetType().Name}) — retrying once after reconnecting.");
            return await operation();
        }
    }

    /// <summary>
    /// Fetches the most recent <paramref name="maxCount"/> messages' envelopes+flags for
    /// <paramref name="mailbox"/> — for <see cref="MailSyncEngine"/>'s own background sync, which
    /// runs on its own schedule independent of whatever folder the user is actively looking at.
    ///
    /// Deliberately does NOT use SelectFolderAsync+ListMessagesAsync: those mutate <see cref="_current"/>,
    /// which the UI's own GoToFolderAsync also mutates — a background caller doing the same would
    /// race it exactly the way GoToFolderAsync itself used to race against overlapping folder
    /// switches (the folder-switch bug fixed earlier this session). Instead this follows the same
    /// safe pattern already established for a genuinely cross-folder read (OpenMessageAsync's own
    /// mailbox parameter, FindConversationSiblingsAsync): temporarily select the target under the
    /// SAME lock acquisition that does the fetch, then restore whatever was selected before,
    /// without ever exposing the in-between state to another caller.
    /// </summary>
    public async Task<(IReadOnlyList<InboxRow> Rows, IReadOnlyDictionary<string, string?> MessageIds,
            IReadOnlyDictionary<string, IReadOnlyList<string>?> References)>
        FetchRecentEnvelopesAsync(string mailbox, int maxCount, CancellationToken ct = default)
    {
        using var _ = await AcquireImapLockAsync(ct);
        return await WithReconnectRetryAsync(async () =>
        {
            await EnsureConnectedAsync();
            var empty = (IReadOnlyList<InboxRow>)[];
            var emptyIds = (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>();
            var emptyRefs = (IReadOnlyDictionary<string, IReadOnlyList<string>?>)new Dictionary<string, IReadOnlyList<string>?>();
            if (!_foldersByMailbox.TryGetValue(mailbox, out var folder))
                return (empty, emptyIds, emptyRefs);

            var originalFolder = _current;
            var isOriginal = originalFolder is not null
                && string.Equals(folder.FullName, originalFolder.FullName, StringComparison.Ordinal);
            try
            {
                if (!isOriginal)
                    await folder.OpenAsync(FolderAccess.ReadOnly);
                else
                    await folder.StatusAsync(StatusItems.Count); // same fresh-count reasoning as ListMessagesAsync's own StatusAsync call

                var total = folder.Count;
                if (total == 0)
                    return (empty, emptyIds, emptyRefs);

                var start = Math.Max(0, total - maxCount);
                // References, on top of what ListMessagesAsync itself fetches: this is what lets
                // FindConversationSiblings find *replies* to a message, not just its ancestors — a
                // reply's own References header is what points back at the message it replied to.
                var summaries = await folder.FetchAsync(start, total - 1,
                    MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId |
                    MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure | MessageSummaryItems.References);

                // Mailbox stays null on every row — the sync engine already knows which mailbox this
                // batch belongs to (it's the caller's own `mailbox` argument) and upserts under that,
                // same "null means whatever folder this naturally belongs to" convention as everywhere
                // else InboxRow.Mailbox is used.
                var rows = summaries.Select(s => ToRow(s, isSentRow: IsSentFolder(folder))).ToList();
                var messageIds = summaries.ToDictionary(
                    s => s.UniqueId.Id.ToString(), s => s.Envelope?.MessageId);
                var references = summaries.ToDictionary(
                    s => s.UniqueId.Id.ToString(),
                    s => s.References is { Count: > 0 } refs ? (IReadOnlyList<string>?)[.. refs] : null);
                return (rows, messageIds, references);
            }
            finally
            {
                if (!isOriginal && originalFolder is not null)
                {
                    try { await originalFolder.OpenAsync(FolderAccess.ReadWrite); }
                    catch (Exception) { /* EnsureConnectedAsync's own reconnect path recovers next call */ }
                    _current = originalFolder;
                }
            }
        });
    }

    public async Task<MessagePage> ListMessagesAsync()
    {
        using var _ = await AcquireImapLockAsync();
        return await WithReconnectRetryAsync(async () =>
        {
            await EnsureConnectedAsync();
            if (_current is null)
                return MessagePage.Empty;

            // _current's own Count is whatever this connection last heard from the server — but new
            // mail is detected on a completely separate connection (_idleClient, opened solely to sit
            // in IDLE; see this class's own remarks on why). That connection noticing new mail and
            // firing MailboxActivity doesn't, by itself, tell *this* connection anything — its own
            // cached Count stays stale until something makes it ask again. A live report matched this
            // exactly: the sidebar badge (ListFoldersAsync, which already does its own fresh
            // StatusAsync per folder) updated the moment new mail arrived, but the message list itself
            // didn't show it until switching folders — which works only because selecting a folder
            // forces a real re-sync. StatusAsync here is that same fresh check, just without needing to
            // leave and come back to trigger it.
            try
            {
                await _current.StatusAsync(StatusItems.Count);
            }
            catch (Exception)
            {
                // Best-effort — if the server doesn't like a STATUS on the currently selected folder
                // (some don't), _current.Count below just falls back to whatever this connection last
                // knew, same as before this existed.
            }

            var total = _current.Count;
            var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
            _page = Math.Clamp(_page, 1, pageCount);

            if (total == 0)
                return new MessagePage([], _page, pageCount, 0, _currentMailbox);

            // Page 1 = the newest PageSize messages. IMAP indices run oldest (0) to newest
            // (Count - 1), so the newest page is the highest-numbered slice.
            var endExclusive = total - (_page - 1) * PageSize;
            var start = Math.Max(0, endExclusive - PageSize);
            if (start >= endExclusive)
                return new MessagePage([], _page, pageCount, total, _currentMailbox);

            var summaries = await _current.FetchAsync(start, endExclusive - 1,
                MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId |
                MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure);

            var isSent = IsSentFolder(_current);
            var rows = summaries
                .OrderByDescending(s => s.InternalDate ?? DateTimeOffset.MinValue)
                .Select(s => ToRow(s, isSentRow: isSent))
                .ToList();

            return new MessagePage(rows, _page, pageCount, total, _currentMailbox);
        });
    }

    /// <summary>
    /// Every other message in the current folder that belongs to the same conversation — the whole
    /// folder, not just whatever page happens to be loaded, and not bounded to anything recent
    /// either. Apple Mail threads a reply from months ago into a conversation even if it isn't on
    /// the currently visible page; a client that only ever looks at the loaded 50 rows, or only a
    /// recent window, would silently fail to thread anything older than that.
    ///
    /// Matches only on a real Message-ID/References link — the same mechanism Apple Mail's and
    /// Thunderbird's own conversation views are actually built on. Deliberately no subject-text
    /// fallback (unlike an earlier version of this method, and unlike Gmail's own conversation
    /// view, which does fold same-subject-different-thread messages together): two genuinely
    /// unrelated senders can share a subject by pure coincidence, or the same sender can send
    /// several genuinely independent messages that happen to reuse one subject line (a recurring
    /// same-subject notification, say) — subject-primary or subject-fallback matching merges those
    /// into one conversation with no real relationship backing it. A real header link can't produce
    /// that false positive, since it requires an actual reply/forward relationship a mail client
    /// wrote into the message itself. The tradeoff, matching Apple Mail's own accepted limitation:
    /// a message from a sender/client that never set a Message-ID/References header at all won't
    /// thread — rare in practice.
    ///
    /// Uses the server's own SEARCH instead of fetching envelope/bodystructure for the whole folder
    /// and filtering client-side (what this method used to do) — that full-folder fetch was the
    /// actual cost behind every message open visibly lagging its own header, regardless of mailbox
    /// size or whether the opened message even turned out to have any siblings at all. SEARCH lets
    /// the server return just the matching UIDs; only those get an envelope fetch afterward.
    /// </summary>
    public async Task<IReadOnlyList<InboxRow>> FindConversationSiblingsAsync(
        string excludeId, string? openedMessageId = null,
        IReadOnlyList<string>? openedReferences = null, int max = 200, CancellationToken ct = default)
    {
        using var _ = await AcquireImapLockAsync(ct);
        await EnsureConnectedAsync();
        if (_current is null)
            return [];

        // Two directions: messages that reply to the opened one (their own References header
        // contains its Message-ID) and messages the opened one is itself a reply to (their
        // Message-ID appears in its References list — its ancestry). Either makes them part of the
        // same conversation. HeaderContains is a substring search, so this works regardless of
        // whether the raw header text carries angle brackets around the id or not.
        SearchQuery? query = null;
        if (!string.IsNullOrWhiteSpace(openedMessageId))
            query = SearchQuery.HeaderContains("References", openedMessageId);
        if (openedReferences is not null)
        {
            foreach (var ancestorId in openedReferences)
            {
                if (string.IsNullOrWhiteSpace(ancestorId))
                    continue;
                var byAncestor = SearchQuery.HeaderContains("Message-Id", ancestorId);
                query = query is null ? byAncestor : query.Or(byAncestor);
            }
        }
        if (query is null)
            return [];

        // A conversation's messages routinely live in different folders — the received original
        // in Inbox (or wherever it's since been filed), your own reply in Sent — so searching only
        // the currently open folder misses the other half entirely. A live report was exactly
        // this: opening your own sent reply from the Sent folder never found the original it
        // replied to, since that original was never in Sent to begin with. Special mailboxes are
        // looked up once here (not per-folder) since GetSpecialMailboxesAsync is itself a network
        // round trip; reentrant-safe to call from inside this same lock (see AcquireImapLockAsync's
        // own remarks on that).
        var originalFolder = _current;
        var special = await GetSpecialMailboxesAsync();
        var targetMailboxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { originalFolder.FullName };
        // Inbox/Sent only — not Archive too. Every extra folder here is another OpenAsync +
        // SearchAsync + FetchAsync round trip, all serialized behind the one shared IMAP lock that
        // every other operation (the background new-mail poll, opening the next message) also
        // needs — on a slow link this backed up for well over a minute in practice and made the
        // whole app appear to stop loading mail. Inbox/Sent covers the actual reported case
        // (received-and-replied-to); Archive is comparatively rare for holding the *other* half of
        // a conversation and not worth the extra latency here.
        foreach (var key in new[] { "Inbox", "Sent" })
            if (special.TryGetValue(key, out var mailbox))
                targetMailboxes.Add(mailbox);

        var results = new List<InboxRow>();
        foreach (var mailboxName in targetMailboxes)
        {
            // Checked only here — between folders, never inside one — so the user having since
            // opened a different message or switched folders stops this from starting further
            // commands on the shared connection, without ever abandoning one already in flight
            // (see AcquireImapLockAsync's own remarks on why that specifically is unsafe).
            if (ct.IsCancellationRequested)
                break;
            if (!_foldersByMailbox.TryGetValue(mailboxName, out var folder))
                continue;

            try
            {
                var isOriginal = string.Equals(folder.FullName, originalFolder.FullName, StringComparison.Ordinal);
                if (!isOriginal)
                    await folder.OpenAsync(FolderAccess.ReadOnly);

                var uids = await folder.SearchAsync(query);
                if (uids.Count == 0)
                    continue;

                var summaries = await folder.FetchAsync(uids,
                    MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId |
                    MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure);

                // Mailbox stays null for a result from the same folder the opened message is
                // already in — matching every existing row's own assumption ("this id belongs to
                // whatever folder is currently open") exactly, so nothing downstream needs to
                // change for the common case. Only a genuinely cross-folder result carries an
                // explicit mailbox, which is what tells a later attachment click or body fetch to
                // select that folder first instead of assuming the active one.
                results.AddRange(summaries.Select(s => ToRow(s, isOriginal ? null : folder.FullName, isSentRow: IsSentFolder(folder))));
            }
            catch (Exception)
            {
                // Best-effort per folder — one folder's search failing shouldn't lose whatever the
                // others still found. A folder that's genuinely slow to answer is bounded by the
                // shared _imap client's own NetworkTimeoutMs on every command it issues (this is
                // deliberately NOT wrapped in an additional client-side WaitAsync/timeout on top of
                // that: MailKit's ImapClient issues one command at a time on one connection, and
                // walking away from an awaited call before MailKit itself considers the command
                // finished leaves that command still in flight against the shared connection —
                // whatever this method (or the next caller waiting on the same lock) issues next
                // then races an unread response still arriving from the abandoned command, which
                // is exactly the kind of protocol desync that can wedge every subsequent operation
                // on this connection until the app is restarted. NetworkTimeoutMs already aborts
                // and cleans up a genuinely stuck command safely at the MailKit/socket level, which
                // is the only place that's actually safe to do it from.
            }
        }

        // Searching the other folders above moved the connection's own selected mailbox away from
        // the one the rest of the app still thinks is active — every other method on this class
        // assumes _current reflects that. Restored unconditionally rather than only when it looks
        // like it drifted, since that's one fewer thing that can be gotten subtly wrong.
        try
        {
            await originalFolder.OpenAsync(FolderAccess.ReadWrite);
            _current = originalFolder;
        }
        catch (Exception)
        {
            // If re-selecting the original folder itself fails, EnsureConnectedAsync's own
            // reconnect path (every other method already goes through it) will recover from
            // whatever's wrong the next time anything is asked of this connection.
        }

        return results
            .Where(r => r.Id != excludeId)
            .OrderByDescending(r => r.Timestamp ?? DateTime.MinValue)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Searches the whole current folder — not just whatever page happens to be loaded.
    ///
    /// Subject/From/To are matched with a real substring compare against envelopes fetched for the
    /// entire folder, rather than trusting the server's own SEARCH SUBJECT/FROM/TO: many IMAP
    /// servers index those by whole word, not raw substring, so searching "hel" would never match
    /// "hello" server-side even though every desktop/browser Ctrl+F treats that as a match. Message
    /// bodies are too expensive to fetch in full just to substring-match client-side, so those still
    /// go through the server's own (word-indexed) SEARCH BODY — a real but narrower limitation than
    /// subject/sender search having the same gap would be.
    /// </summary>
    public async Task<IReadOnlyList<InboxRow>> SearchAsync(string query, int maxResults = 300)
    {
        using var _ = await AcquireImapLockAsync();
        await EnsureConnectedAsync();
        if (_current is null || string.IsNullOrWhiteSpace(query))
            return [];

        // Gmail-style operators (from:/to:/subject:/has:attachment/is:unread/is:starred/is:read) —
        // see SearchQueryParser for the exact syntax and its deliberate single-word-value limit.
        // Applied at two points below: once here on envelope fields alone, to narrow candidates
        // before the (potentially large) final fetch; again after that fetch, which is the only
        // point flag/attachment operators can be checked at all, and the only point that's
        // authoritative for every candidate regardless of whether it was found via free-text
        // subject/sender matching or the server's own body search below (which knows nothing about
        // operators, so a body-search hit still needs the same operator check applied to it).
        var parsed = Automation.SearchQueryParser.Parse(query);

        var total = _current.Count;
        var uids = new HashSet<UniqueId>();

        if (total > 0)
        {
            var envelopes = await _current.FetchAsync(0, total - 1,
                MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId);
            foreach (var summary in envelopes)
            {
                if (!EnvelopeOperatorsMatch(summary.Envelope, parsed))
                    continue;
                if (parsed.FreeText.Length == 0 || EnvelopeMatches(summary.Envelope, parsed.FreeText))
                    uids.Add(summary.UniqueId);
            }
        }

        if (parsed.FreeText.Length > 0)
        {
            try
            {
                foreach (var uid in await _current.SearchAsync(SearchQuery.BodyContains(parsed.FreeText)))
                    uids.Add(uid);
            }
            catch (Exception)
            {
                // Body search is a bonus on top of the subject/from/to substring match above, which
                // already covers the common case — not worth failing the whole search over.
            }
        }

        if (uids.Count == 0)
            return [];

        // Newest matches matter most when a folder has far more hits than are worth showing —
        // IMAP UIDs increase with arrival order, so the highest-numbered ones are the most recent.
        var take = uids.Count > maxResults ? uids.OrderByDescending(u => u.Id).Take(maxResults).ToList() : uids.ToList();

        var summaries = await _current.FetchAsync(take,
            MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId |
            MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure | MessageSummaryItems.Size);

        var isSentSearch = IsSentFolder(_current);
        return summaries
            .Where(s => EnvelopeOperatorsMatch(s.Envelope, parsed) && FlagOperatorsMatch(s, parsed))
            .OrderByDescending(s => s.InternalDate ?? DateTimeOffset.MinValue)
            .Select(s => ToRow(s, isSentRow: isSentSearch))
            .ToList();
    }

    private static bool EnvelopeOperatorsMatch(Envelope? envelope, Automation.SearchQueryParser.ParsedQuery parsed)
    {
        if (parsed.From is not null && !AddressListMatches(envelope?.From, parsed.From))
            return false;
        if (parsed.To is not null && !AddressListMatches(envelope?.To, parsed.To))
            return false;
        if (parsed.Subject is not null
            && envelope?.Subject?.Contains(parsed.Subject, StringComparison.OrdinalIgnoreCase) != true)
            return false;
        return true;
    }

    private static bool FlagOperatorsMatch(IMessageSummary summary, Automation.SearchQueryParser.ParsedQuery parsed)
    {
        if (parsed.Unread is { } wantUnread)
        {
            var isUnread = !(summary.Flags?.HasFlag(MessageFlags.Seen) ?? false);
            if (isUnread != wantUnread)
                return false;
        }
        if (parsed.Starred is true && !(summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false))
            return false;
        if (parsed.HasAttachment is true && !HasAttachments(summary.Body))
            return false;
        if (parsed.OlderThan is { } olderThan
            && (summary.InternalDate is not { } d1 || d1.UtcDateTime >= olderThan))
            return false;
        if (parsed.NewerThan is { } newerThan
            && (summary.InternalDate is not { } d2 || d2.UtcDateTime <= newerThan))
            return false;
        if (parsed.LargerThanBytes is { } largerThan && (summary.Size is not { } sz1 || sz1 < largerThan))
            return false;
        if (parsed.SmallerThanBytes is { } smallerThan && (summary.Size is not { } sz2 || sz2 > smallerThan))
            return false;
        return true;
    }

    private static bool EnvelopeMatches(Envelope? envelope, string query)
    {
        if (envelope is null)
            return false;

        if (envelope.Subject?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return AddressListMatches(envelope.From, query)
            || AddressListMatches(envelope.To, query)
            || AddressListMatches(envelope.Cc, query);
    }

    private static bool AddressListMatches(InternetAddressList? list, string query) =>
        list?.Mailboxes.Any(m =>
            m.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
            || m.Address.Contains(query, StringComparison.OrdinalIgnoreCase)) == true;

    /// <param name="mailbox">Null for the common case — a row from whatever folder is currently
    /// open. Set only by a caller (FindConversationSiblingsAsync) that fetched this summary from a
    /// *different* folder, so later operations on this specific row know to select that folder
    /// first — see InboxRow.Mailbox's own remarks.</param>
    private static readonly string[] SentFolderNames = ["Sent", "Sent Items", "Sent Messages"];

    /// <summary>RFC 6154 SPECIAL-USE, when the server advertises it, is authoritative; the name
    /// fallback matches SpecialFolders' own list above for a server that doesn't. Synchronous and
    /// free — Attributes/Name are already known from the folder listing, no extra round trip.</summary>
    private static bool IsSentFolder(IMailFolder folder) =>
        folder.Attributes.HasFlag(FolderAttributes.Sent)
        || SentFolderNames.Any(name => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <param name="isSentRow">True for a row from the Sent folder — every row there was written
    /// by the signed-in account itself, so Envelope.From is always just "you" and tells the list
    /// nothing useful; the recipient (Envelope.To) is what actually distinguishes one Sent row from
    /// another. A live report on a real account confirmed this wasn't handled at all: every single
    /// row in Sent showed the user's own name, indistinguishable from every other row.</param>
    private static InboxRow ToRow(IMessageSummary summary, string? mailbox = null, bool isSentRow = false)
    {
        var when = (summary.InternalDate ?? DateTimeOffset.Now).LocalDateTime;
        var party = isSentRow
            ? summary.Envelope?.To?.Mailboxes?.FirstOrDefault()
            : summary.Envelope?.From?.Mailboxes?.FirstOrDefault();
        var recipientCount = isSentRow ? summary.Envelope?.To?.Mailboxes?.Count() ?? 0 : 0;
        var label = party is null
            ? (isSentRow ? "(no recipient)" : "(unknown sender)")
            : party.Name is { Length: > 0 } ? party.Name : party.Address;
        // "and N more" for a Sent row addressed to several people — the same "who else was on
        // this" signal a real client's list gives, not just the first name with the rest silently
        // dropped.
        var display = recipientCount > 1 ? $"{label} +{recipientCount - 1}" : label;

        return new InboxRow(
            summary.UniqueId.Id.ToString(),
            display,
            summary.Envelope?.Subject is { Length: > 0 } subject ? subject : "(no subject)",
            "", // a snippet needs the body; skipped for list-fetch performance, same as before
            MailText.FormatListDate(when),
            Unread: !(summary.Flags?.HasFlag(MessageFlags.Seen) ?? false),
            Starred: summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
            HasAttachment: HasAttachments(summary.Body),
            Timestamp: when,
            SenderAddress: party?.Address ?? "",
            Mailbox: mailbox);
    }

    private static bool HasAttachments(BodyPart? part) => part switch
    {
        BodyPartMultipart multipart => multipart.BodyParts.Any(HasAttachments),
        BodyPartBasic basic => basic.IsAttachment,
        _ => false,
    };

    public Task<bool> NextPageAsync()
    {
        if (_current is null || _page * PageSize >= _current.Count)
            return Task.FromResult(false);
        _page++;
        return Task.FromResult(true);
    }

    public Task<bool> PreviousPageAsync()
    {
        if (_page <= 1)
            return Task.FromResult(false);
        _page--;
        return Task.FromResult(true);
    }

    // ---- Reading -------------------------------------------------------------------------------

    /// <summary>Raw RFC 822 source of a message — the same "View source" real mail clients (and
    /// Thunderbird's Ctrl+U) offer, and genuinely the fastest way to diagnose a message that isn't
    /// rendering the way it should: seeing the exact MIME structure the server actually sent.</summary>
    /// <summary>The exact bytes MimeKit re-serializes the message to, unmodified — deliberately
    /// not decoded to a string here. A text part re-encodes per its own declared charset (which is
    /// very often not UTF-8: ISO-8859-1, Windows-1252, ISO-2022-JP…), so forcing a UTF-8 decode on
    /// these bytes silently corrupted "View source" for any such message. Handing back the raw
    /// bytes lets the caller write them straight to disk untouched and leave charset detection to
    /// whatever opens the file, the same way saving a real .eml already works.</summary>
    public async Task<byte[]?> GetRawSourceAsync(string id)
    {
        if (_current is null || !uint.TryParse(id, out var idValue))
            return null;
        using var _ = await AcquireImapLockAsync();
        await EnsureConnectedAsync();
        var uid = new UniqueId(idValue);

        try
        {
            var message = _cachedUid == uid && _cachedMessage is not null
                ? _cachedMessage
                : await _current.GetMessageAsync(uid);
            using var stream = new MemoryStream();
            message.WriteTo(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Envelope-only Message-ID lookup for one UID — used to verify a queued offline
    /// action still points at the same message before replaying it (see
    /// MainWindow.OfflineActions.cs). A UID is only stable within one UIDVALIDITY generation; if
    /// the folder gets rebuilt between when an action was queued and when it replays, the same UID
    /// could now belong to a completely different message, and replaying blind would silently
    /// mutate the wrong one.</summary>
    public async Task<string?> GetMessageIdAsync(string id)
    {
        if (_current is null || !uint.TryParse(id, out var idValue))
            return null;
        using var _ = await AcquireImapLockAsync();
        await EnsureConnectedAsync();
        var uid = new UniqueId(idValue);

        try
        {
            var summary = (await _current.FetchAsync(new[] { uid }, MessageSummaryItems.Envelope)).FirstOrDefault();
            return summary?.Envelope?.MessageId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <param name="mailbox">Null for the common case — fetches from whichever folder is already
    /// open. Set only for a cross-folder conversation sibling (see InboxRow.Mailbox), in which
    /// case this temporarily selects that folder, fetches, and restores whatever was selected
    /// before — IMAP's GetMessageAsync always operates on the currently selected folder, not one
    /// named on the call itself.</param>
    public async Task<MessageDetail?> OpenMessageAsync(string id, string? mailbox = null, CancellationToken ct = default)
    {
        if (_current is null || !uint.TryParse(id, out var idValue))
            return null;
        using var _ = await AcquireImapLockAsync(ct);
        await EnsureConnectedAsync();
        var uid = new UniqueId(idValue);

        var originalFolder = _current;
        var switchingFolder = mailbox is not null
            && !string.Equals(mailbox, originalFolder.FullName, StringComparison.Ordinal);
        if (switchingFolder)
        {
            if (!_foldersByMailbox.TryGetValue(mailbox!, out var target))
                return null;
            try
            {
                await target.OpenAsync(FolderAccess.ReadOnly);
            }
            catch (Exception)
            {
                return null;
            }
            _current = target;
        }

        MimeMessage message;
        try
        {
            message = await _current.GetMessageAsync(uid);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (switchingFolder)
            {
                try { await originalFolder.OpenAsync(FolderAccess.ReadWrite); }
                catch (Exception) { /* EnsureConnectedAsync's own reconnect path recovers next call */ }
                _current = originalFolder;
            }
        }

        // Not cached for a cross-folder fetch — _cachedUid/_cachedMessage exist so a download
        // right after opening a message doesn't need a second round trip, but that shortcut only
        // makes sense relative to whichever folder _current represents *now*, which a cross-folder
        // fetch has already restored away from the one this message actually came from.
        if (!switchingFolder)
        {
            _cachedUid = uid;
            _cachedMessage = message;
        }
        return BuildMessageDetail(message, uid, mailbox);
    }

    private static MessageDetail BuildMessageDetail(MimeMessage message, UniqueId uid, string? mailbox = null)
    {
        var attachments = new List<MailAttachment>();
        var index = 0;
        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            var name = part.FileName ?? $"attachment{index}";
            var size = part.Content?.Stream is { } stream
                ? AttachmentViewerWindow.FormatSize(stream.Length)
                : "";
            // A different scheme ("imapx", not "imap") when a mailbox is carried, rather than a
            // 3-vs-4-part ambiguity in the same scheme — DownloadAttachmentAsync tells the two
            // apart by scheme alone, so there's nothing to misparse. Only ever set for a
            // cross-folder conversation sibling (see InboxRow.Mailbox); every attachment on a
            // message opened normally keeps the exact URL format already in use everywhere else.
            var url = mailbox is null ? $"imap:{uid.Id}:{index}" : $"imapx:{mailbox}:{uid.Id}:{index}";
            attachments.Add(new MailAttachment(name, size, url));
            index++;
        }

        var bodyHtml = (message.Body is not null ? ExtractBodyHtml(message.Body) : null)
            ?? "<p>(This message has no readable body.)</p>";
        bodyHtml = ResolveEmbeddedImages(message, bodyHtml);
        var (unsubUrl, unsubMailto) = ParseUnsubscribe(message);

        return new MessageDetail(
            message.Subject ?? "(no subject)",
            message.From.ToString(),
            MailText.FormatDetailDate(message.Date.LocalDateTime),
            bodyHtml,
            To: message.To.ToString(),
            Cc: message.Cc.ToString(),
            Attachments: attachments,
            Calendar: TryParseCalendarInvite(message),
            Timestamp: message.Date.LocalDateTime,
            MessageId: message.MessageId,
            References: message.References?.Count > 0 ? [.. message.References] : null,
            UnsubscribeUrl: unsubUrl,
            UnsubscribeMailto: unsubMailto);
    }

    /// <summary>
    /// RFC 2369's List-Unsubscribe header — one or more comma-separated "&lt;...&gt;" options a
    /// sender declares, typically an https: link and/or a mailto:. Preferring the http(s) form when
    /// both exist: it needs nothing more than opening a browser tab, where the mailto: form still
    /// requires composing and sending a message. Never inferred from body text — only ever set when
    /// the sender actually declared it, the same way real mail clients (Gmail's "Unsubscribe"
    /// pill, Apple Mail's banner) only offer this for messages that opted into RFC 2369 at all.
    /// </summary>
    private static (string? Url, string? Mailto) ParseUnsubscribe(MimeMessage message)
    {
        var header = message.Headers["List-Unsubscribe"];
        if (string.IsNullOrWhiteSpace(header))
            return (null, null);

        string? url = null, mailto = null;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(header, "<([^>]+)>"))
        {
            var value = m.Groups[1].Value.Trim();
            if (url is null && value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = value;
            else if (mailto is null && value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                mailto = value;
        }
        return (url, mailto);
    }

    /// <summary>
    /// MimeMessage.HtmlBody/TextBody return null/wrong-part for a real, common structure: a mailing
    /// list (Mailman etc.) wrapping the actual message in multipart/mixed alongside its own
    /// plain-text disclaimer banner and unsubscribe footer, e.g.
    /// mixed(text/plain disclaimer, multipart/alternative(text/plain, text/html), text/plain footer).
    /// Confirmed directly against MimeKit: HtmlBody returns null for that shape, and TextBody
    /// returns only the *first* text/plain leaf (the disclaimer) — silently dropping the real
    /// message and the footer. This walks the whole tree instead: multipart/alternative picks its
    /// richest resolvable child (last non-null, since alternatives are ordered plain-to-rich);
    /// every other multipart (mixed, related, ...) concatenates all of its children in order, since
    /// each one is separate content, not an alternative of the others.
    /// </summary>
    private static string? ExtractBodyHtml(MimeEntity entity)
    {
        if (entity is MimePart mimePart && mimePart.IsAttachment)
            return null;

        if (entity is Multipart multipart)
        {
            if (multipart.ContentType.MimeType.Equals("multipart/alternative", StringComparison.OrdinalIgnoreCase))
            {
                string? best = null;
                foreach (var child in multipart)
                {
                    if (ExtractBodyHtml(child) is { } resolved)
                        best = resolved;
                }
                return best;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var child in multipart)
            {
                if (ExtractBodyHtml(child) is { } resolved)
                    sb.Append(resolved);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        if (entity is TextPart text)
        {
            // A meeting invite's raw RFC 5545 ICS text (Outlook/Exchange, Teams, Zoom, Google
            // Calendar all send one) — TryParseCalendarInvite already parses this same part into
            // the proper CalendarInvite UI; multipart/mixed concatenating every child otherwise
            // dumped its raw "BEGIN:VCALENDAR..." text straight into the message body instead.
            if (text.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase))
                return null;

            return text.IsHtml
                ? text.Text
                : $"<p>{System.Net.WebUtility.HtmlEncode(text.Text).Replace("\n", "<br/>")}</p>";
        }

        return null;
    }

    /// <summary>
    /// A "cid:xyz" src is how HTML mail references an image embedded in the message itself (a
    /// signature logo, an inline photo) rather than fetched over the network — no browser or
    /// WebView2 knows what to do with that scheme on its own, and HtmlSanitizer strips it outright
    /// since it isn't http(s)/data. Replacing it with the actual embedded bytes as a data: URI is
    /// what turns "src stripped, image just missing" into the image actually rendering.
    /// </summary>
    private static string ResolveEmbeddedImages(MimeMessage message, string html)
    {
        if (!html.Contains("cid:", StringComparison.OrdinalIgnoreCase))
            return html;

        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || part.Content is null)
                continue;

            try
            {
                using var stream = new MemoryStream();
                part.Content.DecodeTo(stream);
                var dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(stream.ToArray())}";
                html = html.Replace($"cid:{part.ContentId}", dataUri, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Best-effort — one unreadable embedded part shouldn't block the rest of the body.
            }
        }
        return html;
    }

    /// <summary>
    /// A calendar invite (Outlook, Google Calendar, Zoom's own scheduling mail — all of them send
    /// the same RFC 5545 text/calendar part) carries real structured data: an exact start time, a
    /// location, sometimes the join link in its description. Parsed with Ical.Net rather than
    /// guessing a day/time out of the visible body text, which is all a plain message offers.
    /// </summary>
    private static CalendarInvite? TryParseCalendarInvite(MimeMessage message)
    {
        var calendarPart = message.BodyParts.OfType<MimePart>()
            .FirstOrDefault(p => p.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase));
        if (calendarPart?.Content is null)
            return null;

        try
        {
            using var stream = new MemoryStream();
            calendarPart.Content.DecodeTo(stream);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            var calendar = Ical.Net.Calendar.Load(reader.ReadToEnd());
            var ev = calendar?.Events?.FirstOrDefault();
            if (ev is null)
                return null;

            var text = $"{ev.Description} {ev.Location}";
            var joinUrl = MeetingLinkFinder.FindUrl(text);

            DateTimeOffset? start = ev.Start is { } dt ? new DateTimeOffset(dt.AsUtc, TimeSpan.Zero) : null;

            return new CalendarInvite(
                string.IsNullOrWhiteSpace(ev.Summary) ? "Meeting" : ev.Summary,
                start,
                string.IsNullOrWhiteSpace(ev.Location) ? null : ev.Location,
                joinUrl);
        }
        catch (Exception)
        {
            // A malformed or unsupported .ics part shouldn't block the rest of the message from
            // opening — it just means no meeting card.
            return null;
        }
    }

    // ---- Flags / move / delete ------------------------------------------------------------------

    private static UniqueId? TryUid(string id) =>
        uint.TryParse(id, out var value) ? new UniqueId(value) : null;

    public async Task<bool> SetReadAsync(string id, bool read)
    {
        if (_current is null || TryUid(id) is not { } uid)
            return false;
        using var _ = await AcquireImapLockAsync();
        try
        {
            await EnsureConnectedAsync();
            if (read)
                await _current.AddFlagsAsync(uid, MessageFlags.Seen, silent: true);
            else
                await _current.RemoveFlagsAsync(uid, MessageFlags.Seen, silent: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> SetFlaggedAsync(string id, bool flagged)
    {
        if (_current is null || TryUid(id) is not { } uid)
            return false;
        using var _ = await AcquireImapLockAsync();
        try
        {
            await EnsureConnectedAsync();
            if (flagged)
                await _current.AddFlagsAsync(uid, MessageFlags.Flagged, silent: true);
            else
                await _current.RemoveFlagsAsync(uid, MessageFlags.Flagged, silent: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves to Trash — the near-universal mail-client meaning of "delete". A message already in
    /// Trash is permanently expunged instead, since there's nowhere further to move it to.
    /// </summary>
    public async Task<bool> DeleteAsync(string id)
    {
        _lastMove = null; // starts clean — see MoveUndo: a stale value here would let Undo
                           // reverse the wrong (older) action instead of reporting "can't undo".
        if (_current is null || TryUid(id) is not { } uid)
            return false;
        using var _ = await AcquireImapLockAsync();
        await EnsureConnectedAsync();

        var trash = await ResolveSpecialAsync("Trash");
        if (trash is not null && trash.FullName != _current.FullName)
            return await MoveUidAsync(uid, trash);

        try
        {
            // Already in Trash — this permanently expunges it, so there's nothing to undo.
            await _current.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true);
            await _current.ExpungeAsync([uid]);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> ArchiveAsync(string id)
    {
        _lastMove = null;
        if (TryUid(id) is not { } uid)
            return false;
        using var _ = await AcquireImapLockAsync();
        await EnsureConnectedAsync();
        var archive = await ResolveSpecialAsync("Archive");
        return archive is not null && await MoveUidAsync(uid, archive);
    }

    public async Task<bool> MoveToFolderAsync(string id, string mailbox)
    {
        _lastMove = null;
        if (TryUid(id) is not { } uid || !_foldersByMailbox.TryGetValue(mailbox, out var target))
            return false;
        using var _ = await AcquireImapLockAsync();
        await EnsureConnectedAsync();
        return await MoveUidAsync(uid, target);
    }

    /// <summary>What the most recent Delete/Archive/MoveToFolder actually did, in case the caller
    /// wants to offer "Undo" — set only when the server told us the message's new UID in the
    /// destination (not every IMAP server does), since without that there's no way to move it back.</summary>
    public readonly record struct MoveUndo(string SourceMailbox, string DestinationMailbox, UniqueId NewUid);

    private MoveUndo? _lastMove;

    /// <summary>Reads and clears the pending undo info — "consume" so a second caller a moment
    /// later doesn't accidentally undo an action that already got its own chance to.</summary>
    public MoveUndo? ConsumeLastMove()
    {
        var move = _lastMove;
        _lastMove = null;
        return move;
    }

    private async Task<bool> MoveUidAsync(UniqueId uid, IMailFolder target)
    {
        if (_current is null)
            return false;
        try
        {
            var sourceMailbox = _current.FullName;
            var newUid = await _current.MoveToAsync(uid, target);
            _lastMove = newUid is { } nu ? new MoveUndo(sourceMailbox, target.FullName, nu) : null;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Moves a message back where <see cref="ConsumeLastMove"/> says it came from.
    /// Reopening the destination folder to issue the move — and reopening whatever was selected
    /// before that afterward — since IMAP only ever has one folder selected per connection at a
    /// time, and this may run well after the folder the move happened from was last the active one.</summary>
    public async Task<bool> UndoMoveAsync(MoveUndo move)
    {
        using var _ = await AcquireImapLockAsync();
        if (!_foldersByMailbox.TryGetValue(move.DestinationMailbox, out var from)
            || !_foldersByMailbox.TryGetValue(move.SourceMailbox, out var to))
            return false;

        try
        {
            await from.OpenAsync(FolderAccess.ReadWrite);
            await from.MoveToAsync(move.NewUid, to);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (_current is not null)
            {
                try { await _current.OpenAsync(FolderAccess.ReadWrite); }
                catch (Exception) { /* best-effort restore of the previously selected folder */ }
            }
        }
    }

    // ---- Sending / drafts -----------------------------------------------------------------------

    /// <summary>
    /// Builds the message plus a list of anything silently skipped along the way (a vanished
    /// attachment, a mistyped address) — the send still goes out with everything that *did* work,
    /// but the caller needs these to actually tell the user, rather than them believing the
    /// message went out complete when it didn't.
    /// </summary>
    private (MimeMessage Message, List<string> Warnings) BuildMessage(ComposeResult result)
    {
        var warnings = new List<string>();
        var message = new MimeMessage();
        // Built directly rather than via MailboxAddress.Parse(_account.Identity) — Parse re-parses a
        // plain "Display Name <email>" string, which throws if DisplayName itself contains a
        // character RFC 5322 treats specially (a comma, an angle bracket, an "@") without being
        // quoted first. The constructor form encodes the display name properly regardless of what's
        // in it, so a From header that includes the sender's own name can never fail a send outright
        // — this was the actual cause behind "Send" silently failing after the undo-send window.
        message.From.Add(new MailboxAddress(_account.DisplayName, _account.Email));
        AddAddresses(message.To, result.To, warnings);
        AddAddresses(message.Cc, result.Cc, warnings);
        AddAddresses(message.Bcc, result.Bcc, warnings);
        message.Subject = result.Subject;

        // The actual RFC 5322 threading headers — without these, a sent reply carries no link back
        // to what it replied to at all, so opening it later (in Sent, or from another client/
        // device entirely) can never find the original as part of the same conversation. Null for
        // a fresh message or a forward (see ComposeResult's own remarks on why forwards don't set
        // these).
        if (!string.IsNullOrWhiteSpace(result.InReplyTo))
            message.InReplyTo = result.InReplyTo;
        if (result.References is { Count: > 0 })
            foreach (var reference in result.References)
                message.References.Add(reference);

        var builder = new BodyBuilder();
        if (!string.IsNullOrWhiteSpace(result.BodyHtml))
        {
            builder.HtmlBody = result.BodyHtml;
            builder.TextBody = MailText.HtmlToPlainText(result.BodyHtml);
        }
        else
        {
            builder.TextBody = result.Body;
        }

        foreach (var file in result.Files)
        {
            try
            {
                builder.Attachments.Add(file.Path);
            }
            catch (Exception)
            {
                // A file that vanished between attaching and sending just doesn't go out —
                // reported below rather than the message silently arriving one attachment short.
                warnings.Add($"Couldn't attach \"{file.Name}\" — the file is no longer available.");
            }
        }

        message.Body = builder.ToMessageBody();
        return (message, warnings);
    }

    private static void AddAddresses(InternetAddressList list, string raw, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        // A real address-list parse first — respects a quoted display name's own comma ("Doe, John"
        // <j@x.com> is valid RFC 5322) the way splitting on every literal comma doesn't. Without
        // this, a recipient like that silently split into two pieces here — one a dangling,
        // unparseable fragment that got reported as an invalid address and dropped, meaning that
        // recipient never received the message at all with no clear indication why. Falls back to
        // splitting on comma/semicolon (Outlook's own convention for a pasted list) only when the
        // whole string doesn't parse cleanly as one real address list.
        if (InternetAddressList.TryParse(raw, out var parsed) && parsed.Count > 0)
        {
            list.AddRange(parsed);
            return;
        }

        foreach (var part in raw.Split(',', ';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;
            try
            {
                list.Add(MailboxAddress.Parse(trimmed));
            }
            catch (Exception)
            {
                // An address the user mistyped is skipped rather than failing the whole send —
                // ComposeWindow already required at least one recipient before allowing Send —
                // but reported below so the sender knows that recipient didn't get it.
                warnings.Add($"Skipped invalid address \"{trimmed}\".");
            }
        }
    }

    /// <summary>Sends the message and returns anything that got silently dropped along the way
    /// (a vanished attachment, an unparseable address) — empty if everything went out intact.</summary>
    public async Task<IReadOnlyList<string>> SendAsync(ComposeResult result)
    {
        var (message, warnings) = BuildMessage(result);

        // Deliberately NOT under the IMAP lock — SmtpClient is its own separate connection, and
        // holding _imapLock across the whole SMTP round trip used to make actual mail delivery wait
        // behind whatever else was mid-command on the shared IMAP connection (a background sync
        // sweep, an in-progress body backfill of hundreds of older messages) for no reason — SMTP
        // doesn't touch that connection at all.
        using (var smtp = new SmtpClient { Timeout = NetworkTimeoutMs })
        {
            var options = _account.SmtpSecurity == SmtpSecurity.SslOnConnect
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
            await smtp.ConnectAsync(_account.SmtpHost, _account.SmtpPort, options);
            await smtp.AuthenticateAsync(_account.LoginName, _account.Password);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);
        }

        // Filing a Sent copy is best-effort and genuinely secondary to delivery having already
        // succeeded above — fire-and-forget rather than awaited, so a busy shared IMAP connection
        // (the same background backfill mentioned above can hold it for a long stretch indexing
        // hundreds of older messages one at a time) can no longer make "Message sent" sit stuck for
        // minutes waiting on something that already happened. A failure here means Sent doesn't get
        // a copy this time around, not that the send itself failed — logged, not surfaced to the
        // sender or queued for a resend that would just duplicate a message that already went out.
        _ = FileSentCopyBestEffortAsync(message);

        return warnings;
    }

    private async Task FileSentCopyBestEffortAsync(MimeMessage message)
    {
        try
        {
            using var _ = await AcquireImapLockAsync();
            var sent = await ResolveSpecialAsync("Sent");
            if (sent is not null)
                await sent.AppendAsync(message, MessageFlags.Seen);
        }
        catch (Exception ex)
        {
            Log.Warn("Filing a Sent copy of a message that already sent failed", ex);
        }
    }

    public async Task<bool> SaveDraftAsync(ComposeResult result) => (await SaveDraftAsync(result, null)).Saved;

    /// <summary>
    /// Saves a draft, optionally replacing a previous autosave of the same in-progress message
    /// (IMAP has no in-place "update a message" — a draft is re-appended and the old copy removed).
    /// Returns the new draft's UID so a caller doing periodic autosave can pass it back in on the
    /// next tick — null when the server didn't hand one back (no UIDPLUS support), in which case
    /// the caller just can't track this copy for replacement, or when the save failed outright (in
    /// which case any earlier autosaved copy is left alone rather than risking losing it).
    /// </summary>
    public async Task<(bool Saved, UniqueId? NewUid)> SaveDraftAsync(ComposeResult result, UniqueId? replacingUid)
    {
        using var _ = await AcquireImapLockAsync();
        var drafts = await ResolveSpecialAsync("Drafts");
        if (drafts is null)
            return (false, null);

        try
        {
            var (message, _) = BuildMessage(result);
            var newUid = await drafts.AppendAsync(message, MessageFlags.Draft);

            if (replacingUid is { } oldUid)
            {
                try
                {
                    await drafts.AddFlagsAsync(oldUid, MessageFlags.Deleted, silent: true);
                    await drafts.ExpungeAsync([oldUid]);
                }
                catch (Exception)
                {
                    // The new copy is already saved; failing to clean up the previous autosave
                    // just leaves a harmless extra draft instead of losing anything.
                }
            }

            return (true, newUid);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    /// <summary>Removes one autosaved draft copy by UID — used once the real send/manual-save/discard
    /// makes a periodic autosave stale, independent of whatever folder is currently open in the UI.</summary>
    public async Task DeleteDraftAsync(UniqueId uid)
    {
        using var _ = await AcquireImapLockAsync();
        var drafts = await ResolveSpecialAsync("Drafts");
        if (drafts is null)
            return;
        try
        {
            await drafts.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true);
            await drafts.ExpungeAsync([uid]);
        }
        catch (Exception)
        {
            // Best-effort cleanup — a leftover autosave copy is harmless clutter, not data loss.
        }
    }

    // ---- Attachments ---------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="attachment"/>.Url is "imap:{uid}:{index}", written by
    /// <see cref="OpenMessageAsync"/>. Uses the cached message from that call when the uid still
    /// matches, otherwise re-fetches it.
    /// </summary>
    public async Task<string?> DownloadAttachmentAsync(MailAttachment attachment, string targetPath, IProgress<int>? progress = null)
    {
        // Two URL shapes: "imap:{uid}:{index}" for the common case (whatever folder is currently
        // open), and "imapx:{mailbox}:{uid}:{index}" for an attachment on a cross-folder
        // conversation sibling (see InboxRow.Mailbox and BuildMessageDetail's own remarks on why
        // the scheme itself, not just the part count, is what tells them apart).
        var parts = attachment.Url.Split(':');
        string? mailbox = null;
        uint uidValue;
        int index;
        if (parts.Length == 3 && parts[0] == "imap"
            && uint.TryParse(parts[1], out uidValue) && int.TryParse(parts[2], out index))
        {
            // mailbox stays null — falls through to _current below, exactly as before this existed.
        }
        else if (parts.Length == 4 && parts[0] == "imapx"
            && uint.TryParse(parts[2], out uidValue) && int.TryParse(parts[3], out index))
        {
            mailbox = parts[1];
        }
        else
        {
            return null;
        }

        var uid = new UniqueId(uidValue);
        // Locked before even reading _cachedUid/_cachedMessage, not just around the re-fetch below:
        // OpenMessageAsync writes both fields together while holding this same lock, so reading them
        // unlocked risked a torn read — _cachedUid already the new message's while _cachedMessage was
        // still the previous one (or vice versa) — if a message was opened while a download from the
        // previously-open one was still in flight. That silently saved the wrong message's attachment
        // bytes under the requested filename, with no error surfaced.
        using var _ = await AcquireImapLockAsync();

        // Already in memory (the message this session's own OpenMessageAsync last fetched, still
        // the currently-open one) — free, no fetch of any kind needed.
        if (mailbox is null && _cachedUid == uid && _cachedMessage is { } cached)
        {
            var cachedPart = cached.Attachments.OfType<MimePart>().ElementAtOrDefault(index);
            if (cachedPart?.Content is not { } cachedContent)
                return null;
            try
            {
                await using var stream = File.Create(targetPath);
                await cachedContent.DecodeToAsync(stream);
                return targetPath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        try
        {
            return await WithReconnectRetryAsync(async () =>
            {
                // Previously missing entirely — this whole live-fetch path went straight from the
                // lock to using _current/_imap with no connectivity check at all, so a connection
                // that had dropped just sat there returning null (logged as a generic failure) on
                // every attempt until something else happened to reconnect it. A live report of a
                // PDF simply not opening traced back to exactly this.
                await EnsureConnectedAsync();
                if (_current is null)
                    return null;

                var originalFolder = _current;
                var switchingFolder = mailbox is not null
                    && !string.Equals(mailbox, originalFolder.FullName, StringComparison.Ordinal);
                if (switchingFolder)
                {
                    if (!_foldersByMailbox.TryGetValue(mailbox!, out var target))
                        return null;
                    await target.OpenAsync(FolderAccess.ReadOnly);
                    _current = target;
                }

                try
                {
                    // Not the currently cached message — this used to fall back to GetMessageAsync(uid),
                    // fetching the ENTIRE RFC822 message (every other attachment, every embedded image,
                    // the full body) over IMAP just to extract one small part. A live report traced a PDF
                    // stuck on "Downloading…" for the better part of a minute to exactly this. BODYSTRUCTURE
                    // alone — no content, just the MIME tree shape — is enough to locate the target
                    // attachment's own section; GetStreamAsync then pulls only that one section over the
                    // wire, already decoded per its own Content-Transfer-Encoding.
                    var summaries = await _current.FetchAsync([uid], MessageSummaryItems.BodyStructure);
                    var attachmentParts = new List<BodyPartBasic>();
                    CollectAttachmentParts(summaries.FirstOrDefault()?.Body, attachmentParts);
                    var targetPart = attachmentParts.ElementAtOrDefault(index);
                    if (targetPart is null)
                    {
                        // CollectAttachmentParts' own walk over BODYSTRUCTURE didn't find as many attachment
                        // parts as the index it was asked for expects — that index was assigned once, when
                        // the message was first opened, from MimeMessage.Attachments' own enumeration over
                        // the FULL message. A live report of some (not all) attachments failing to open
                        // outright is consistent with the two orderings disagreeing for some real message
                        // structure this hasn't been tested against; logged so that can actually be confirmed
                        // instead of just failing silently every time.
                        Log.Warn($"DownloadAttachmentAsync: BODYSTRUCTURE walk found {attachmentParts.Count} attachment part(s) for uid {uid}, but index {index} was requested");
                        return null;
                    }

                    // Octets (the part's own declared size, from BODYSTRUCTURE — no extra fetch needed) is
                    // what turns MailKit's raw byte-count callback into an actual percentage; without a
                    // known total there's nothing to divide by, so progress is simply skipped rather than
                    // guessed at.
                    var transferProgress = progress is null || targetPart.Octets <= 0
                        ? null
                        : new BytesToPercentProgress(progress, targetPart.Octets);
                    await using var partStream = await _current.GetStreamAsync(uid, targetPart, CancellationToken.None, transferProgress);
                    await using var fileStream = File.Create(targetPath);
                    // GetStreamAsync hands back the section's raw wire bytes — still whatever
                    // Content-Transfer-Encoding the part declared (base64, for the overwhelming
                    // majority of real attachments), NOT decoded to the actual file content despite
                    // this method's own earlier remarks claiming otherwise. A live report of a PDF
                    // that "downloaded" fine (right size, opens the viewer) but wouldn't render
                    // traced back to exactly this: the saved file was literally the base64 text of
                    // the PDF, never decoded to bytes. MimeKit's own DecoderFilter, run over a
                    // FilteredStream wrapping the destination, is the standard way to decode a raw
                    // section stream without needing the whole message parsed into a MimePart first.
                    var encoding = ParseContentTransferEncoding(targetPart.ContentTransferEncoding);
                    if (encoding is ContentEncoding.Default or ContentEncoding.SevenBit or ContentEncoding.EightBit or ContentEncoding.Binary)
                    {
                        // Nothing to decode — already the real bytes as-is.
                        await partStream.CopyToAsync(fileStream);
                    }
                    else
                    {
                        using var filtered = new MimeKit.IO.FilteredStream(fileStream);
                        filtered.Add(MimeKit.IO.Filters.DecoderFilter.Create(encoding));
                        await partStream.CopyToAsync(filtered);
                        filtered.Flush();
                    }
                    return targetPath;
                }
                finally
                {
                    if (switchingFolder)
                    {
                        try { await originalFolder.OpenAsync(FolderAccess.ReadWrite); }
                        catch (Exception) { /* EnsureConnectedAsync's own reconnect path recovers next call */ }
                        _current = originalFolder;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"DownloadAttachmentAsync failed for uid {uid}, index {index}", ex);
            return null;
        }
    }

    /// <summary>BODYSTRUCTURE reports the transfer encoding as a bare IMAP string ("BASE64",
    /// "QUOTED-PRINTABLE", "7BIT", ...), not the typed <see cref="ContentEncoding"/> enum a MimePart
    /// would already carry — this is the same finite set MimeKit's own MimePart.ContentTransferEncoding
    /// setter recognizes, just parsed by hand since there's no BodyPartBasic-facing equivalent.</summary>
    private static ContentEncoding ParseContentTransferEncoding(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "base64" => ContentEncoding.Base64,
        "quoted-printable" => ContentEncoding.QuotedPrintable,
        "7bit" => ContentEncoding.SevenBit,
        "8bit" => ContentEncoding.EightBit,
        "binary" => ContentEncoding.Binary,
        "x-uuencode" or "uuencode" => ContentEncoding.UUEncode,
        _ => ContentEncoding.Default,
    };

    /// <summary>Same recursive shape as HasAttachments above, but collecting every matching part in
    /// document order instead of just testing for any — this order must match MimeMessage.Attachments'
    /// own enumeration exactly, since DownloadAttachmentAsync's caller identifies "which attachment"
    /// purely by index into that list (assigned once, when the message was first opened and its full
    /// MimeMessage was available) and this walks the lighter BODYSTRUCTURE-only tree instead.</summary>
    private static void CollectAttachmentParts(BodyPart? part, List<BodyPartBasic> into)
    {
        switch (part)
        {
            case BodyPartMultipart multipart:
                foreach (var child in multipart.BodyParts)
                    CollectAttachmentParts(child, into);
                break;
            case BodyPartBasic basic when basic.IsAttachment:
                into.Add(basic);
                break;
        }
    }

    /// <summary>Adapts MailKit's own byte-count progress callback (fired as GetStreamAsync reads
    /// the IMAP FETCH response) into a plain 0-100 percentage against a known total — a live report
    /// asked for real download progress instead of a static "Downloading…" with no indication of
    /// how far along it actually is. IProgress&lt;T&gt; (not a raw callback) is what lets the UI side
    /// use the framework's own System.Progress&lt;T&gt;, which marshals back to the caller's captured
    /// SynchronizationContext automatically — no manual Dispatcher.Invoke needed on the UI end.</summary>
    private sealed class BytesToPercentProgress(IProgress<int> progress, long totalBytes) : ITransferProgress
    {
        public void Report(long bytesTransferred) => Report(bytesTransferred, totalBytes);

        public void Report(long bytesTransferred, long totalSize)
        {
            if (totalSize > 0)
                progress.Report((int)Math.Clamp(bytesTransferred * 100L / totalSize, 0, 100));
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopIdleMonitor();
        try
        {
            if (_idleLoopTask is not null)
                await _idleLoopTask;
        }
        catch (Exception)
        {
            // Best-effort — the process is going away regardless.
        }

        try
        {
            if (_imap.IsConnected)
                await _imap.DisconnectAsync(true);
        }
        catch (Exception)
        {
            // Best-effort — the process is going away regardless.
        }
        _imap.Dispose();
    }
}

/// <summary>Thrown when IMAP authentication fails or the session drops mid-use.</summary>
public sealed class SessionExpiredException(string message) : Exception(message);
