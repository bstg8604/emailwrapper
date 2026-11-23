using EmailClient.Diagnostics;

namespace EmailClient.Mail;

/// <summary>
/// Keeps <see cref="MailStore"/> current from the server on its own schedule, entirely decoupled
/// from whatever the UI happens to be doing — this is what makes the local database an actual
/// source of truth rather than just another cache the UI still has to wait on. Before this existed,
/// "what's in this folder" was answered by a live IMAP call made directly from a UI event handler;
/// now it's answered by <see cref="MailStore"/> alone, and this class is the only thing that talks
/// to <see cref="ImapMailBackend"/> to keep it fresh.
///
/// Runs its own loop (a plain Task, not a WPF DispatcherTimer — nothing here touches the UI thread)
/// rather than being driven by MainWindow's poll timer, so a slow or stuck sync cycle never has any
/// UI-thread affinity to block. <see cref="DataChanged"/>/<see cref="FoldersChanged"/> fire from
/// whatever background thread the sync loop happens to be running on — same "marshal to the UI
/// thread yourself" contract ImapMailBackend's own MailboxActivity/ConnectionStateChanged events
/// already use.
/// </summary>
public sealed class MailSyncEngine : IDisposable
{
    /// <summary>Enough for a full page or two of most folders without re-fetching a whole large
    /// mailbox's history every cycle — same "recent window is what's actually useful" reasoning
    /// ContactsIndex's own MessagesPerFolder already uses.</summary>
    private const int RecentMessagesPerFolder = 200;

    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(60);

    /// <summary>Pause between each folder's own sync within one sweep — a live report showed a
    /// connection that had just successfully reconnected dropping again within 15 seconds, repeatedly,
    /// with every folder's sync (5-6 back-to-back SELECT+FETCH round trips, zero pacing between them)
    /// clustered in that same window. imap.iitb.ac.in is a Perdition proxy, and this reads like the
    /// proxy itself dropping the connection under that immediate burst rather than a genuine network
    /// problem — spacing the folders out is a low-cost way to stop hammering it right after every
    /// reconnect, without slowing down a sweep that's otherwise already fast (this only adds up to
    /// (folder count - 1) x this pause, a couple of seconds at most for a typical mailbox).</summary>
    private static readonly TimeSpan PerFolderSyncPause = TimeSpan.FromMilliseconds(400);

    /// <summary>How many never-opened messages get their full body fetched and indexed per sync
    /// cycle. Deliberately small: a message already opened is already fully cached (see
    /// MainWindow's own SaveBody call sites) — this is purely filling in the rest of a mailbox's
    /// history for local body-text search, on a "some progress every minute, forever" cadence
    /// rather than a big upfront cost that would compete with normal usage right after connecting.
    /// A large mailbox's full history backfills gradually over many cycles; that's the intended
    /// trade, not an oversight.</summary>
    private const int BackfillPerCycle = 5;

    /// <summary>Extra pause between each backfill fetch, on top of whatever the fetch itself takes
    /// — keeps this genuinely low-priority relative to the shared IMAP connection rather than
    /// racing foreground actions (opening a message, switching folders) back-to-back.</summary>
    private static readonly TimeSpan BackfillPause = TimeSpan.FromMilliseconds(500);

    private readonly ImapMailBackend _mail;
    private readonly MailStore _store;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>Fired (background thread) after a folder's messages have been (re)synced into
    /// MailStore and there's something new to show for it.</summary>
    public event Action<string>? DataChanged;

    /// <summary>Fired (background thread) after the folder list / unread badges have been
    /// refreshed into MailStore.</summary>
    public event Action? FoldersChanged;

    /// <summary>Fired (background thread) with a short human-readable description of what the sync
    /// loop is doing right now, or null when it's gone idle. Deliberately narrower than it once
    /// was: this used to also fire "Refreshing folders…" and "Syncing {folder}…" on every single
    /// 60-second cycle regardless of whether anything actually needed catching up — a live report
    /// called this out as an unwelcome, near-constant flicker in the toolbar for the entire time the
    /// app is open, not the occasional "here's what's happening" it was meant to be. Those two
    /// (along with the ~10-minute "Refreshing contacts…") are near-instant now and don't need
    /// announcing every cycle; what's left — <see cref="BackfillOneFolderAsync"/>'s "Indexing older
    /// mail: N remaining…" — is the one case still worth surfacing, because it's the one that can
    /// genuinely take a while (a large, never-before-synced mailbox) and is naturally self-limiting
    /// (it only fires while there's real backfill work left, not every cycle forever).</summary>
    public event Action<string?>? SyncStatusChanged;

    public MailSyncEngine(ImapMailBackend mail, MailStore store)
    {
        _mail = mail;
        _store = store;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncAllFoldersAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A whole sweep failing (offline, auth expired mid-session) must not kill the loop
                // permanently — the next interval tries again, same "keep going" reasoning as the
                // IDLE loop's own retry logic.
                Log.Warn("Background mail sync cycle failed", ex);
            }

            try
            {
                await Task.Delay(SyncInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One full sweep: folder list/badges, then each folder's recent messages in turn,
    /// Inbox and Sent first (the two overwhelmingly most likely to actually be looked at), each
    /// step checked against <paramref name="ct"/> between folders — never mid-fetch, same
    /// cooperative-cancellation reasoning as FindConversationSiblingsAsync's own per-folder check.
    /// </summary>
    public async Task SyncAllFoldersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Automation.MailFolder> folders;
        try
        {
            folders = await _mail.ListFoldersAsync();
            foreach (var folder in folders)
                _store.UpsertFolder(folder);
            FoldersChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Folder sync failed", ex);
            SyncStatusChanged?.Invoke(null);
            return; // no folder list means no mailbox names to sync messages for either
        }

        var displayNames = folders.ToDictionary(f => f.Mailbox, f => f.Name, StringComparer.OrdinalIgnoreCase);
        var special = await SafeGetSpecialMailboxesAsync();
        var priorityOrder = OrderByPriority(folders, special).ToList();
        var first = true;
        foreach (var mailbox in priorityOrder)
        {
            if (ct.IsCancellationRequested)
                break;
            if (!first)
            {
                try { await Task.Delay(PerFolderSyncPause, ct); }
                catch (OperationCanceledException) { break; }
            }
            first = false;
            await SyncOneFolderAsync(mailbox, ct);
        }

        // One folder's worth of backfill per cycle, after the real sync work above — never lets
        // filling in old history delay the part of a sweep that actually keeps the visible list
        // current.
        if (!ct.IsCancellationRequested)
            await BackfillOneFolderAsync(priorityOrder, displayNames, ct);

        // Every ~10 minutes, not every cycle — see RescanContactsAsync's own remarks: this is what
        // keeps contacts from ever going properly stale after ConnectAsync's own initial scan was
        // skipped (see ImapMailBackend.ConnectAsync's skipContactsScan), without paying that same
        // ~800-envelope cost on every single 60-second sweep.
        if (!ct.IsCancellationRequested && ++_cyclesSinceContactsRescan >= ContactsRescanEveryNCycles)
        {
            _cyclesSinceContactsRescan = 0;
            await RescanContactsAsync(ct);
        }

        SyncStatusChanged?.Invoke(null);
    }

    private const int ContactsRescanEveryNCycles = 10;
    private int _cyclesSinceContactsRescan;

    private async Task RescanContactsAsync(CancellationToken ct)
    {
        try
        {
            await _mail.RescanContactsAsync(ct);
            // Same seed step MainWindow does once after a fresh (non-skipped) sign-in scan — SET,
            // not increment, and never regresses a higher score organic correspondence (mark-read,
            // reply, etc.) already recorded since the last seed. See MailStore.SeedContact's own
            // remarks.
            foreach (var contact in _mail.Contacts.ListAll())
                _store.SeedContact(contact.Address, contact.HasName ? contact.DisplayName : null, contact.Score);
        }
        catch (OperationCanceledException)
        {
            // The app's shutting down, most likely — not an error. The counter was already reset
            // before this ran either way, so a failed/cancelled attempt just means the next one is
            // another ~10 minutes out rather than retried immediately; fine for a low-stakes
            // background nicety like this, not worth the complexity of a shorter retry path.
        }
        catch (Exception ex)
        {
            Log.Debug($"Periodic contacts rescan failed: {ex.Message}");
        }
    }

    /// <summary>Finds the first folder (in priority order) that still has messages without a body
    /// cached, and fetches a small batch for it — see <see cref="BackfillPerCycle"/>'s own remarks
    /// on why this is deliberately slow and incremental rather than a one-shot bulk fetch.</summary>
    private async Task BackfillOneFolderAsync(
        IReadOnlyList<string> priorityOrder, IReadOnlyDictionary<string, string> displayNames, CancellationToken ct)
    {
        foreach (var mailbox in priorityOrder)
        {
            if (ct.IsCancellationRequested)
                return;

            IReadOnlyList<string> missing;
            int totalMissing;
            try
            {
                missing = _store.GetUidsMissingBody(mailbox, BackfillPerCycle);
                totalMissing = missing.Count == 0 ? 0 : _store.CountUidsMissingBody(mailbox);
            }
            catch (Exception ex)
            {
                Log.Debug($"Backfill candidate lookup failed for '{mailbox}': {ex.Message}");
                continue;
            }
            if (missing.Count == 0)
                continue; // this folder's fully backfilled already — try the next one this cycle

            var name = displayNames.GetValueOrDefault(mailbox, mailbox);
            var done = 0;
            foreach (var uid in missing)
            {
                if (ct.IsCancellationRequested)
                    return;
                SyncStatusChanged?.Invoke($"Indexing older mail in {name}: {totalMissing - done} remaining…");
                try
                {
                    var detail = await _mail.OpenMessageAsync(uid, mailbox, ct);
                    if (detail is not null)
                        _store.SaveBody(mailbox, uid, detail);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A message that vanished server-side, or one transient error, shouldn't stop
                    // backfilling the rest — there's always a next cycle regardless.
                    Log.Debug($"Body backfill failed for {mailbox}:{uid}: {ex.Message}");
                }
                done++;

                try { await Task.Delay(BackfillPause, ct); }
                catch (OperationCanceledException) { return; }
            }

            // Only one folder's worth per cycle — once we've found (and worked) a folder with
            // something missing, stop; the next cycle picks up wherever this left off, in the same
            // priority order, checking Inbox/Sent again first (which matters if new mail arrived
            // there since — recent mail in the folders people actually look at stays the priority,
            // not whichever folder happened to be mid-backfill).
            return;
        }
    }

    /// <summary>Syncs just one folder — used both by the full sweep above and for an immediate,
    /// out-of-cycle refresh (see <see cref="TriggerImmediateSync"/>) rather than waiting up to
    /// <see cref="SyncInterval"/> for IMAP IDLE's own push notification to be reflected locally.</summary>
    public async Task SyncOneFolderAsync(string mailbox, CancellationToken ct = default)
    {
        try
        {
            var (rows, messageIds, references) = await _mail.FetchRecentEnvelopesAsync(mailbox, RecentMessagesPerFolder, ct);
            if (rows.Count == 0)
                return;
            _store.UpsertMessages(mailbox, rows, messageIds, references);
            DataChanged?.Invoke(mailbox);
        }
        catch (OperationCanceledException)
        {
            // The caller (or the next navigation) decided this sync no longer matters — not an error.
        }
        catch (Exception ex)
        {
            Log.Warn($"Sync failed for folder '{mailbox}'", ex);
        }
    }

    /// <summary>Called from MainWindow's existing IMAP-IDLE-activity handler, which already knows
    /// which mailbox just changed — lets a push notification be reflected in MailStore right away
    /// instead of waiting for the next periodic sweep.</summary>
    public void TriggerImmediateSync(string mailbox) => _ = SyncOneFolderAsync(mailbox);

    private async Task<IReadOnlyDictionary<string, string>> SafeGetSpecialMailboxesAsync()
    {
        try
        {
            return await _mail.GetSpecialMailboxesAsync();
        }
        catch (Exception)
        {
            return new Dictionary<string, string>();
        }
    }

    private static IEnumerable<string> OrderByPriority(
        IReadOnlyList<Automation.MailFolder> folders, IReadOnlyDictionary<string, string> special)
    {
        var priority = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (special.TryGetValue("Inbox", out var inbox)) priority.Add(inbox);
        if (special.TryGetValue("Sent", out var sent)) priority.Add(sent);

        return folders.Select(f => f.Mailbox)
            .OrderByDescending(m => priority.Contains(m));
    }

    public void Dispose() => Stop();
}
