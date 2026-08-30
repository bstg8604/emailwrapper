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
/// One IMAP connection is held open and reused for the app's lifetime (a second, idle connection
/// would need its own login and doubles server-side session usage for no benefit); a dedicated
/// SMTP connection is opened per send, since sends are infrequent and short-lived.
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
        await _imap.AuthenticateAsync(_account.Email, _account.Password);

        await IndexFoldersAsync();

        var inbox = _imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        _current = inbox;
        _currentMailbox = inbox.FullName;

        // Fire-and-forget: contacts are a "nice to have" for autocomplete, not something the rest
        // of the app should wait on before it's usable.
        _ = _contacts.BuildAsync(_imap, _account.Email);
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
            return;

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
    /// Searches the whole current folder server-side (subject/body/from/to) — not just whatever
    /// page happens to be loaded — using real IMAP SEARCH rather than the client-side substring
    /// filter over the loaded page that quick-narrows within it. Results are capped and newest
    /// first, the same as the normal page view.
    /// </summary>
    public async Task<IReadOnlyList<InboxRow>> SearchAsync(string query, int maxResults = 300)
    {
        await EnsureConnectedAsync();
        if (_current is null || string.IsNullOrWhiteSpace(query))
            return [];

        var terms = SearchQuery.Or(
            SearchQuery.Or(SearchQuery.SubjectContains(query), SearchQuery.BodyContains(query)),
            SearchQuery.Or(SearchQuery.FromContains(query), SearchQuery.ToContains(query)));

        var uids = await _current.SearchAsync(terms);
        if (uids.Count == 0)
            return [];

        // Newest matches matter most when a folder has far more hits than are worth showing —
        // IMAP UIDs increase with arrival order, so the tail of the list is the most recent.
        var take = uids.Count > maxResults ? uids.Skip(uids.Count - maxResults).ToList() : uids;

        var summaries = await _current.FetchAsync(take,
            MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId |
            MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure);

        return summaries
            .OrderByDescending(s => s.InternalDate ?? DateTimeOffset.MinValue)
            .Select(ToRow)
            .ToList();
    }

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
            Timestamp: when);
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

    public async Task<MessageDetail?> OpenMessageAsync(string id)
    {
        if (_current is null || !uint.TryParse(id, out var idValue))
            return null;
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

        var bodyHtml = message.HtmlBody
            ?? (message.TextBody is { } text
                ? $"<p>{System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br/>")}</p>"
                : "<p>(This message has no readable body.)</p>");

        return new MessageDetail(
            message.Subject ?? "(no subject)",
            message.From.ToString(),
            MailText.FormatDetailDate(message.Date.LocalDateTime),
            bodyHtml,
            To: message.To.ToString(),
            Cc: message.Cc.ToString(),
            Attachments: attachments);
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
        var archive = await ResolveSpecialAsync("Archive");
        return archive is not null && await MoveUidAsync(uid, archive);
    }

    public async Task<bool> MoveToFolderAsync(string id, string mailbox)
    {
        _lastMove = null;
        if (TryUid(id) is not { } uid || !_foldersByMailbox.TryGetValue(mailbox, out var target))
            return false;
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
        await smtp.AuthenticateAsync(_account.Email, _account.Password);
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
