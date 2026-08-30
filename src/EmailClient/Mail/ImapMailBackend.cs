using System.IO;
using EmailClient.Automation;
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
    private readonly AccountSettings _account;
    private readonly ImapClient _imap = new();
    private readonly Dictionary<string, IMailFolder> _foldersByMailbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly ContactsIndex _contacts = new();

    private IMailFolder? _current;
    private string _currentMailbox = "INBOX";
    private int _page = 1;
    private const int PageSize = 50;

    // ---- IMAP IDLE (push new-mail notifications) -------------------------------------------
    private ImapClient? _idleClient;
    private CancellationTokenSource? _idleCts;
    private Task? _idleLoopTask;

    /// <summary>Raised (on a background thread — marshal to the UI thread before touching
    /// controls) whenever the watched mailbox's message count changes: a new arrival or an
    /// expunge the server told us about without being asked.</summary>
    public event Action? MailboxActivity;

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
                client = new ImapClient();
                await client.ConnectAsync(_account.ImapHost, _account.ImapPort, SecureSocketOptions.SslOnConnect, token);
                await client.AuthenticateAsync(_account.LoginName, _account.Password, token);
                var folder = await client.GetFolderAsync(mailbox, token) ?? client.Inbox;
                await folder.OpenAsync(FolderAccess.ReadOnly, token);
                _idleClient = client;

                // Reached IDLE at all, so the connection/login/open sequence genuinely works —
                // resets the breaker so a later transient blip doesn't inherit an unrelated streak.
                consecutiveFailures = 0;

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
            catch (Exception)
            {
                // Transient failure (network blip, server hiccup, or the server just doesn't allow
                // a second session) — back off and reconnect rather than letting the whole loop
                // die immediately; the 60-second poll timer still covers new mail regardless. But
                // give up for good once MaxConsecutiveIdleFailures is hit (see its own comment).
                consecutiveFailures++;
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
    }

    // Caches the last opened message so downloading one of its attachments doesn't need a second
    // round trip to re-fetch the whole thing.
    private UniqueId? _cachedUid;
    private MimeMessage? _cachedMessage;

    public string SelfAddress => _account.Email;
    public ContactsIndex Contacts => _contacts;

    public ImapMailBackend(AccountSettings account) => _account = account;

    // ---- Connection -----------------------------------------------------------------------

    public async Task ConnectAsync()
    {
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
        await _contacts.BuildAsync(_imap, _account.Email);
    }

    private async Task IndexFoldersAsync()
    {
        _foldersByMailbox.Clear();
        foreach (var folder in await AllFoldersAsync())
            _foldersByMailbox[folder.FullName] = folder;
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

    public Task<IReadOnlyDictionary<string, string>> GetSpecialMailboxesAsync()
    {
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

        return Task.FromResult<IReadOnlyDictionary<string, string>>(map);
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
        var result = new List<Automation.MailFolder>();
        foreach (var folder in _foldersByMailbox.Values)
        {
            if (!folder.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                var unread = 0;
                try
                {
                    await folder.StatusAsync(StatusItems.Unread);
                    unread = folder.Unread;
                }
                catch (Exception)
                {
                    // Some servers don't support STATUS on every folder — the badge just stays 0.
                }

                var depth = string.IsNullOrEmpty(folder.FullName)
                    ? 0
                    : folder.FullName.Split(folder.DirectorySeparator).Length - 1;

                result.Add(new Automation.MailFolder(folder.FullName, folder.Name, unread, Math.Max(0, depth)));
            }
        }
        return result;
    }

    /// <summary>Creates a new top-level folder — used by the account page's Folders section.</summary>
    public async Task CreateFolderAsync(string name)
    {
        var root = _imap.GetFolder(_imap.PersonalNamespaces[0]);
        await root.CreateAsync(name, isMessageFolder: true);
        await IndexFoldersAsync();
    }

    public async Task RenameFolderAsync(string mailbox, string newName)
    {
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

        var wantedMailbox = _currentMailbox;
        try
        {
            if (_imap.IsConnected)
                await _imap.DisconnectAsync(true);
        }
        catch (Exception)
        {
            // Best-effort cleanup before reconnecting from scratch.
        }

        await ConnectAsync();

        if (!string.Equals(wantedMailbox, _current?.FullName, StringComparison.OrdinalIgnoreCase)
            && _foldersByMailbox.TryGetValue(wantedMailbox, out var folder))
        {
            try
            {
                await folder.OpenAsync(FolderAccess.ReadWrite);
                _current = folder;
                _currentMailbox = folder.FullName;
            }
            catch (Exception)
            {
                // Falls back to whatever ConnectAsync already opened (Inbox).
            }
        }
    }

    public async Task<MessagePage> ListMessagesAsync()
    {
        await EnsureConnectedAsync();
        if (_current is null)
            return MessagePage.Empty;

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

        var rows = summaries
            .OrderByDescending(s => s.InternalDate ?? DateTimeOffset.MinValue)
            .Select(ToRow)
            .ToList();

        return new MessagePage(rows, _page, pageCount, total, _currentMailbox);
    }

    /// <summary>
    /// Every other message in the current folder that belongs to the same conversation — the whole
    /// folder, not just whatever page happens to be loaded. Apple Mail threads a reply from months
    /// ago into a conversation even if it isn't on the currently visible page, and a client that
    /// only ever looks at the loaded 50 rows would silently fail to thread anything older than that.
    /// Envelope-only fetch (no body octets) keeps this cheap even for a large folder, matching
    /// SearchAsync's own approach.
    /// </summary>
    public async Task<IReadOnlyList<InboxRow>> FindConversationSiblingsAsync(string conversationKey, string excludeId, int max = 8)
    {
        await EnsureConnectedAsync();
        if (_current is null || string.IsNullOrWhiteSpace(conversationKey))
            return [];

        var total = _current.Count;
        if (total == 0)
            return [];

        var envelopes = await _current.FetchAsync(0, total - 1,
            MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId |
            MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure);

        return envelopes
            .Select(ToRow)
            .Where(r => r.Id != excludeId && r.ConversationKey == conversationKey)
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
        await EnsureConnectedAsync();
        if (_current is null || string.IsNullOrWhiteSpace(query))
            return [];

        var total = _current.Count;
        var uids = new HashSet<UniqueId>();

        if (total > 0)
        {
            var envelopes = await _current.FetchAsync(0, total - 1,
                MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId);
            foreach (var summary in envelopes)
            {
                if (EnvelopeMatches(summary.Envelope, query))
                    uids.Add(summary.UniqueId);
            }
        }

        try
        {
            foreach (var uid in await _current.SearchAsync(SearchQuery.BodyContains(query)))
                uids.Add(uid);
        }
        catch (Exception)
        {
            // Body search is a bonus on top of the subject/from/to substring match above, which
            // already covers the common case — not worth failing the whole search over.
        }

        if (uids.Count == 0)
            return [];

        // Newest matches matter most when a folder has far more hits than are worth showing —
        // IMAP UIDs increase with arrival order, so the highest-numbered ones are the most recent.
        var take = uids.Count > maxResults ? uids.OrderByDescending(u => u.Id).Take(maxResults).ToList() : uids.ToList();

        var summaries = await _current.FetchAsync(take,
            MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId |
            MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure);

        return summaries
            .OrderByDescending(s => s.InternalDate ?? DateTimeOffset.MinValue)
            .Select(ToRow)
            .ToList();
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

    private static InboxRow ToRow(IMessageSummary summary)
    {
        var when = (summary.InternalDate ?? DateTimeOffset.Now).LocalDateTime;
        var from = summary.Envelope?.From?.Mailboxes?.FirstOrDefault();
        var sender = from is null ? "(unknown sender)" : from.Name is { Length: > 0 } ? from.Name : from.Address;

        return new InboxRow(
            summary.UniqueId.Id.ToString(),
            sender,
            summary.Envelope?.Subject is { Length: > 0 } subject ? subject : "(no subject)",
            "", // a snippet needs the body; skipped for list-fetch performance, same as before
            MailText.FormatListDate(when),
            Unread: !(summary.Flags?.HasFlag(MessageFlags.Seen) ?? false),
            Starred: summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
            HasAttachment: HasAttachments(summary.Body),
            Timestamp: when,
            SenderAddress: from?.Address ?? "");
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
    public async Task<string?> GetRawSourceAsync(string id)
    {
        if (_current is null || !uint.TryParse(id, out var idValue))
            return null;
        await EnsureConnectedAsync();
        var uid = new UniqueId(idValue);

        try
        {
            var message = _cachedUid == uid && _cachedMessage is not null
                ? _cachedMessage
                : await _current.GetMessageAsync(uid);
            using var stream = new MemoryStream();
            message.WriteTo(stream);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<MessageDetail?> OpenMessageAsync(string id)
    {
        if (_current is null || !uint.TryParse(id, out var idValue))
            return null;
        await EnsureConnectedAsync();
        var uid = new UniqueId(idValue);

        MimeMessage message;
        try
        {
            message = await _current.GetMessageAsync(uid);
        }
        catch (Exception)
        {
            return null;
        }

        _cachedUid = uid;
        _cachedMessage = message;
        return BuildMessageDetail(message, uid);
    }

    private static MessageDetail BuildMessageDetail(MimeMessage message, UniqueId uid)
    {
        var attachments = new List<MailAttachment>();
        var index = 0;
        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            var name = part.FileName ?? $"attachment{index}";
            var size = part.Content?.Stream is { } stream
                ? AttachmentViewerWindow.FormatSize(stream.Length)
                : "";
            attachments.Add(new MailAttachment(name, size, $"imap:{uid.Id}:{index}"));
            index++;
        }

        var bodyHtml = (message.Body is not null ? ExtractBodyHtml(message.Body) : null)
            ?? "<p>(This message has no readable body.)</p>";
        bodyHtml = ResolveEmbeddedImages(message, bodyHtml);

        return new MessageDetail(
            message.Subject ?? "(no subject)",
            message.From.ToString(),
            MailText.FormatDetailDate(message.Date.LocalDateTime),
            bodyHtml,
            To: message.To.ToString(),
            Cc: message.Cc.ToString(),
            Attachments: attachments,
            Calendar: TryParseCalendarInvite(message),
            Timestamp: message.Date.LocalDateTime);
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
        await EnsureConnectedAsync();
        var archive = await ResolveSpecialAsync("Archive");
        return archive is not null && await MoveUidAsync(uid, archive);
    }

    public async Task<bool> MoveToFolderAsync(string id, string mailbox)
    {
        _lastMove = null;
        if (TryUid(id) is not { } uid || !_foldersByMailbox.TryGetValue(mailbox, out var target))
            return false;
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
        message.From.Add(MailboxAddress.Parse(_account.Identity));
        AddAddresses(message.To, result.To, warnings);
        AddAddresses(message.Cc, result.Cc, warnings);
        AddAddresses(message.Bcc, result.Bcc, warnings);
        message.Subject = result.Subject;

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

        using var smtp = new SmtpClient();
        var options = _account.SmtpSecurity == SmtpSecurity.SslOnConnect
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;
        await smtp.ConnectAsync(_account.SmtpHost, _account.SmtpPort, options);
        await smtp.AuthenticateAsync(_account.LoginName, _account.Password);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);

        // Plain SMTP submission doesn't file a copy anywhere on its own — without this the
        // message would go out but never appear in Sent.
        var sent = await ResolveSpecialAsync("Sent");
        if (sent is not null)
        {
            try
            {
                await sent.AppendAsync(message, MessageFlags.Seen);
            }
            catch (Exception)
            {
                // The send already succeeded; a failed Sent-folder copy isn't worth surfacing as
                // a send failure.
            }
        }

        return warnings;
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
    public async Task<string?> DownloadAttachmentAsync(MailAttachment attachment, string targetPath)
    {
        var parts = attachment.Url.Split(':');
        if (parts.Length != 3 || parts[0] != "imap" || !uint.TryParse(parts[1], out var uidValue)
            || !int.TryParse(parts[2], out var index))
            return null;

        var uid = new UniqueId(uidValue);
        var message = _cachedUid == uid ? _cachedMessage : null;
        if (message is null)
        {
            if (_current is null)
                return null;
            try
            {
                message = await _current.GetMessageAsync(uid);
            }
            catch (Exception)
            {
                return null;
            }
        }

        var part = message.Attachments.OfType<MimePart>().ElementAtOrDefault(index);
        if (part?.Content is not { } content)
            return null;

        try
        {
            await using var stream = File.Create(targetPath);
            await content.DecodeToAsync(stream);
            return targetPath;
        }
        catch (Exception)
        {
            return null;
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
