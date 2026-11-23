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

/// <summary>
/// Loading, filtering, sorting and selecting rows in the message list: the live page fetch,
/// search, the star toggle, and bulk multi-select.
/// </summary>

public partial class MainWindow
{
    /// <summary>Apple Mail-style thread-count badge data (see InboxRow.ThreadCounts) — recomputed
    /// on every _messages change rather than once per fetch, so a message arriving mid-session via
    /// the poll timer or IDLE still gets folded into its thread's count immediately. Deliberately
    /// informational only: this app already learned once that grouping by subject alone
    /// over-matches (see ConversationKey's own comment), so this never removes a row from the list,
    /// only counts how many share a key.</summary>
    private void UpdateThreadCounts() =>
        InboxRow.ThreadCounts = _messages
            .GroupBy(r => r.ConversationKey)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToDictionary(g => g.Key, g => g.Count());

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

    /// <param name="folderRequestId">Set only by GoToFolderAsync, alongside <paramref name="expectedFolder"/>
    /// — every other caller (the poll timer, IDLE push, post-send/delete/archive refreshes) omits
    /// both and gets the old unconditional-apply behavior, since those aren't racing a folder
    /// switch. When set, the fetched page is discarded — never applied to the list, never cached —
    /// if a *later* folder switch has since repointed the shared IMAP connection's selected mailbox:
    /// ListMessagesAsync reads whatever folder is selected *at fetch time*, which by the time this
    /// call's own await returns may no longer be the one this call actually asked for. Applying (or
    /// worse, caching to disk) a page fetched from the wrong folder under this call's own folder name
    /// is exactly the bug this closes — see GoToFolderAsync's own remarks.</param>
    /// <summary>Answers a page from MailStore alone, or returns null if it can't — extracted out of
    /// RefreshMessagesAsync's own non-announce branch (see the comments there) so GoToFolderAsync can
    /// call the exact same local-data check to paint a folder's mail instantly, before the network
    /// debounce and SelectFolderAsync that used to gate this from running at all. MailStore only ever
    /// holds a folder's most recent messages (MailSyncEngine's own recency cap), so a requested page
    /// whose starting offset falls beyond however much is actually synced has nothing local to answer
    /// with — that's the null case, left for the caller's own live-IMAP fallback.</summary>
    private MessagePage? TryLocalPage(string mailbox, int page)
    {
        var localPage = _mailStore?.GetMessages(mailbox, page) ?? MessagePage.Empty;
        var realTotal = _mailStore?.GetFolderTotal(mailbox) ?? 0;
        var offset = (page - 1) * MailStore.PageSize;
        if (localPage.Total == 0 || offset >= localPage.Total)
            return null;

        // Roundcube/IITB-webmail-style reply-arrow status — see AttachReplyStatus' own remarks.
        // Every caller of TryLocalPage (folder switch's instant paint, RefreshMessagesAsync) picks
        // this up for free rather than needing its own separate call.
        var rows = _mailStore is null ? localPage.Rows : _mailStore.AttachReplyStatus(localPage.Rows, mailbox, SpecialMailbox("Sent"));
        localPage = localPage with { Rows = rows };

        // Local data can answer this page, but its own Total/PageCount only reflect how much is
        // synced — substituting the folder's real total (once known) is what makes the pager show
        // "page 4 of 47", not silently cap at "4 of 4".
        return realTotal > localPage.Total
            ? localPage with
              {
                  PageCount = Math.Max(1, (int)Math.Ceiling(realTotal / (double)MailStore.PageSize)),
                  Total = realTotal,
              }
            : localPage;
    }

    private async Task RefreshMessagesAsync(
        bool announceNewMail = false, int? folderRequestId = null, string? expectedFolder = null, bool forceLive = false)
    {
        if (_refreshInFlight)
        {
            // Diagnostic for a live "notification took 1-2 minutes" complaint — if an announcing
            // refresh (poll/IDLE) ever bails out here because another refresh was already running,
            // that mail-check is simply skipped for this cycle rather than queued/retried, which
            // could itself explain a multi-minute delay if refreshes were overlapping often enough.
            // Remove once that's actually diagnosed.
            if (announceNewMail)
                Log.Info("Announcing refresh skipped — another refresh was already in flight");
            return;
        }
        if (_mail is null)
        {
            ApplyCurrentFolderView();
            return;
        }

        _refreshInFlight = true;
        try
        {
            MessagePage page;
            if (announceNewMail)
            {
                // New-mail detection needs a genuinely fresh server round trip — reading from
                // MailStore here would race MailSyncEngine's own same-moment background write with
                // no ordering guarantee between them, and a missed "new mail" notification is a far
                // worse failure than this one path (the poll tick / IDLE activity) staying at IMAP
                // speed. Every other caller below doesn't need that freshness guarantee.
                page = await _mail.ListMessagesAsync();
            }
            else
            {
                var mailbox = SpecialMailbox(expectedFolder ?? _currentFolder);
                // ImapMailBackend's own page cursor, not this class's _page.Page — ChangePageAsync
                // already advances it (via NextPageAsync/PreviousPageAsync) *before* calling this
                // method, so it's the single authoritative "which page is actually being asked
                // for" value. _page here still holds the *previous* call's result until the
                // assignment further down — reading _page.Page instead of this used to silently
                // re-request whatever page was last shown, so Next/Previous never actually
                // advanced what MailStore was asked for.
                var requestedPage = _mail.CurrentPage;
                // forceLive skips the cache entirely — used by the explicit Refresh button, where
                // "there's already something cached for this page" is exactly the wrong reason to
                // skip asking the server: the whole point of clicking Refresh is to check for
                // something new, and a folder that already has any cached page (Sent, say, right
                // after sending) would otherwise never take this branch at all, no matter how many
                // times Refresh is clicked — a real, live "why isn't my just-sent mail showing up"
                // bug, not just a cosmetic staleness gap.
                if (!forceLive && TryLocalPage(mailbox, requestedPage) is { } localAnswer)
                {
                    page = localAnswer;
                }
                else
                {
                    // Real IMAP pagination — same as this always used before MailStore existed, and
                    // it already computes the correct true page count itself (from the live
                    // folder's own Count, not anything synced). Caching the result here is what
                    // makes browsing back through older mail organically grow local coverage over
                    // time, rather than the sync window being a hard, permanent ceiling.
                    page = await _mail.ListMessagesAsync();
                    if (page.Rows.Count > 0)
                        _mailStore?.UpsertMessages(mailbox, page.Rows);
                }
            }

            // A later folder switch may have already repointed the shared IMAP connection's
            // selected mailbox while the fetch above was in flight — ListMessagesAsync reads
            // whatever's selected *at fetch time*, so what just came back can genuinely be a
            // different folder's messages than the one this call actually asked for. Applying (or
            // caching to disk, further down) a result like that under this call's own folder name
            // is exactly the bug this guards against — see RefreshMessagesAsync's own remarks.
            if (folderRequestId is { } fid && fid != _folderRequestSeq)
                return;

            _page = page;

            // A poll tick or IDLE push landing while search results are on screen must not clobber
            // them back to the plain folder listing — the very bug this guard fixes: search results
            // silently reverting to "recent mail" a few seconds to a minute after searching, with no
            // action from the user. Folder/badge bookkeeping below still runs regardless; only the
            // visible list and its status line are held back while a search is showing.
            if (!_serverSearchActive)
            {
                ReplaceMessages(SortRows(page.Rows));
                UpdatePagerBar();
                UpdateListEmptyState();
                StatusText.Text = DescribePage(page);
            }

            await RefreshFoldersAsync();

            AnnounceNewMail(page.Rows, announceNewMail);

            // Only the first page: it's what the app opens on, and caching deeper pages would
            // trade a lot of disk for a view the user has to navigate to anyway.
            if (page.Page == 1)
            {
                var cacheFolder = expectedFolder ?? _currentFolder;
                _cache?.SaveRows(cacheFolder, page.Rows);
                // Inbox only, and only the row list's own folder — not worth quietly prefetching
                // every folder someone happens to pass through, since Inbox is overwhelmingly where
                // "can I read this offline" actually comes up.
                if (cacheFolder == "Inbox")
                    _ = PrefetchBodiesAsync(page.Rows);
            }
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing the message list failed", ex);
            StatusText.Text = $"Refresh failed: {MailErrors.Friendly(ex)}";
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private bool _prefetchInFlight;

    /// <summary>
    /// Best-effort background fetch of whatever's on this page that isn't cached yet, so more of
    /// the inbox is actually readable offline than just what's been manually clicked into — the
    /// "proactive caching" half of offline mode, the manually-opened-only cache being the other
    /// half that already existed. Throttled to a handful of messages with a short pause between
    /// each: this runs after every refresh (as often as every 60s), and a large unthrottled batch
    /// would otherwise make every foreground action (opening a message, deleting one) wait behind
    /// it for the same IMAP connection lock.
    /// </summary>
    private async Task PrefetchBodiesAsync(IReadOnlyList<InboxRow> rows)
    {
        if (_mail is null || _mailStore is null || _prefetchInFlight)
            return;

        // Only ever called for Inbox (see the caller) — mailbox is fixed for the whole batch.
        var mailbox = SpecialMailbox("Inbox");
        const int maxPrefetch = 15;
        var toFetch = rows.Where(r => _mailStore.GetBody(mailbox, r.Id) is null).Take(maxPrefetch).ToList();
        if (toFetch.Count == 0)
            return;

        _prefetchInFlight = true;
        try
        {
            foreach (var row in toFetch)
            {
                try
                {
                    var detail = await _mail.OpenMessageAsync(row.Id);
                    if (detail is not null)
                        _mailStore.SaveBody(mailbox, row.Id, detail);
                }
                catch (Exception)
                {
                    // Best-effort — one message that can't be prefetched (deleted server-side
                    // mid-batch, a transient error) shouldn't stop the rest, and isn't worth
                    // surfacing to the user at all since nothing they did prompted this.
                }
                await Task.Delay(200);
            }
        }
        finally
        {
            _prefetchInFlight = false;
        }
    }

    /// <summary>
    /// Raises a desktop notification for mail that arrived since the last look. Only the Inbox
    /// counts: the same page-changed signal in Trash or Sent means a message was moved there, not
    /// that any arrived, which is why deleting a message used to pop a "New mail" toast for it.
    ///
    /// A real bug this used to have: every refresh — including a plain folder-switch back to
    /// Inbox, or the tray icon's "reset to a fresh view" — called <see cref="NewMailNotifier.Collect"/>
    /// unconditionally, which permanently marks whatever it sees as "seen". If mail arrived while
    /// the user was on a different folder (so the IDLE-triggered refresh's own announce attempt
    /// bailed out above), the *next* time they navigated back to Inbox — even just to look, not a
    /// real mail-check — that navigation's non-announcing refresh silently consumed the same ids,
    /// and the notification for mail that had never actually been shown was gone for good. Only
    /// the periodic poll and IDLE push (both call this with announce:true) are genuine mail-checks
    /// now; any other refresh leaves not-yet-announced mail untouched once the baseline is primed,
    /// so the next real check still catches it. The very first refresh ever still has to prime
    /// regardless (skipping it would flood-announce the whole inbox as "new" on the next check).
    /// </summary>
    private void AnnounceNewMail(IReadOnlyList<InboxRow> rows, bool announce)
    {
        // Diagnostic for a live "notification took 1-2 minutes" complaint — every early-return
        // path below is logged so the next occurrence shows exactly which check held it back
        // (wrong folder, not yet primed, nothing actually fresh, or notifications disabled) instead
        // of the log staying silent about an announcing refresh that ran but chose not to notify.
        // Remove once that's actually diagnosed.
        if (_currentFolder != "Inbox")
        {
            if (announce)
                Log.Info($"Announcing refresh completed, but current folder is '{_currentFolder}', not Inbox — no notification");
            return;
        }
        if (!announce && _notifier.IsPrimed)
            return;

        var fresh = _notifier.Collect(rows);
        if (!announce)
            return;
        if (fresh.Count == 0)
        {
            Log.Info("Announcing refresh completed — no unseen unread mail found");
            return;
        }
        if (!_settings.NotificationsEnabled)
        {
            Log.Info($"Announcing refresh found {fresh.Count} fresh message(s), but notifications are disabled in settings");
            return;
        }

        var (title, text) = NewMailNotifier.Describe(fresh);
        Log.Info($"Notifying: {title} — {text}");
        _tray?.Notify(title, text, fresh[0].Id);
    }

    /// <summary>
    /// Brings the app back and opens the message a clicked notification was about. A new-mail
    /// notification only ever announces something in Inbox, but the window can be sitting on any
    /// folder/page when it's clicked (the tray-restore-resets-to-Inbox behavior only applies to a
    /// plain tray restore, not this specific "go to this exact message" gesture) — previously this
    /// just looked the id up in whatever `_messages` already happened to hold, which silently found
    /// nothing unless Inbox page 1 was already on screen, and the message only actually appeared
    /// once the next scheduled poll/IDLE sweep happened to refresh it — reading as "nothing happened
    /// for several seconds" instead of jumping straight there.
    /// </summary>
    private async void OpenNotifiedMessage(string id)
    {
        if (_currentFolder != "Inbox")
            await GoToFolderAsync("Inbox");

        var row = _messages.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            // Not on screen yet (still on a stale page, or the sync sweep hasn't caught up) — force
            // an immediate refresh rather than waiting on whatever's next scheduled.
            await RefreshMessagesAsync();
            row = _messages.FirstOrDefault(r => r.Id == id);
        }
        if (row is not null)
            MessageList.SelectedItem = row;
    }

    /// <summary>
    /// Back to Inbox, nothing open, scrolled to the top — what a plain tray-icon restore now does
    /// (see TrayIcon.RestoreFresh), instead of silently resuming exactly wherever the window was
    /// left, which is what actually happens by default since minimizing to tray never destroys the
    /// window or its state in the first place.
    /// </summary>
    private async Task ResetToFreshViewAsync()
    {
        await GoToFolderAsync("Inbox");
        if (_messages.Count > 0)
            MessageList.ScrollIntoView(_messages[0]);
    }

    /// <summary>Guards against the exact bug UpdateInboxBadge's fire-and-forget call introduced:
    /// MailKit's ImapClient isn't safe to issue a second command on while one is already in
    /// flight, and this method can now be triggered from a plain UI action (marking a message
    /// read) with no relationship to whatever else might already be mid-request — a second call
    /// arriving while the first awaits the server used to throw "the ImapClient is currently busy
    /// processing a command in another thread" instead of just... waiting its turn.</summary>
    private bool _foldersRefreshInFlight;

    private async Task RefreshFoldersAsync()
    {
        if (_mail is null || _foldersRefreshInFlight)
            return;

        _foldersRefreshInFlight = true;
        try
        {
            // MailStore first — instant, no network wait. Only falls through to the live IMAP call
            // (same as this always did) the very first time for an account, before MailSyncEngine's
            // own first sweep has had a chance to populate anything; the live result primes the
            // store so every subsequent call this session is the fast path.
            var folders = _mailStore?.GetFolders() ?? [];
            if (folders.Count == 0)
            {
                var live = await _mail.ListFoldersAsync();
                folders = live;
                foreach (var f in live)
                    _mailStore?.UpsertFolder(f);
            }
            _liveFolders.Clear();
            _liveFolders.AddRange(folders);
            RenderLiveFolders();

            var inboxMailbox = SpecialMailbox("Inbox");
            var inbox = folders.FirstOrDefault(f => f.Mailbox.Equals(inboxMailbox, StringComparison.OrdinalIgnoreCase));
            SetInboxBadge(inbox?.Unread ?? 0);
        }
        catch (Exception ex)
        {
            // Folder chrome is cosmetic — never let a sidebar refresh failure break the mail list.
            Log.Warn("Refreshing the folder sidebar failed", ex);
        }
        finally
        {
            _foldersRefreshInFlight = false;
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

    private List<FolderView> _lastRenderedFolderViews = [];

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

        // This runs on every folder switch *and* every 60-second poll tick, not just when a
        // folder's unread count or the folder set actually changed — resetting ItemsSource (and
        // the visibility/FadeIn bookkeeping below) for identical data is a pointless re-layout on a
        // timer, not something any user action caused. FolderView is a record, so SequenceEqual is
        // real structural equality here, not reference comparison.
        if (views.SequenceEqual(_lastRenderedFolderViews))
            return;
        _lastRenderedFolderViews = views;

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
            // A live report: scrolled partway down page 1, then paging forward landed on page 2
            // already scrolled to roughly the same spot — WPF's virtualizing ListBox doesn't reset
            // its own scroll offset just because ItemsSource's contents changed underneath it, only
            // if the SelectedItem/keyboard focus moves there. Every page should start at its own
            // top, the same way clicking into a folder already does.
            if (_messages.Count > 0)
                MessageList.ScrollIntoView(_messages[0]);
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
        await RefreshMessagesAsync(forceLive: true);
    }

    private void ComposeButton_Click(object sender, RoutedEventArgs e) => OpenCompose(bodyHtml: NewMessageBodyHtml);

    /// <summary>
    /// Non-modal by design: reading, switching folders and archiving stay usable while composing,
    /// and — unlike a modal dialog — nothing stops opening a second compose window (a reply from
    /// one message while another is still being written), which used to be blocked outright by
    /// the previous <c>ShowDialog</c>.
    /// </summary>
    private void OpenCompose(string to = "", string subject = "", string body = "", string cc = "", string bcc = "",
        string? replacesDraftId = null, string bodyHtml = "", IReadOnlyList<ComposeAttachment>? attachments = null,
        string? inReplyTo = null, IReadOnlyList<string>? references = null)
    {
        var compose = new ComposeWindow(to, subject, body, cc, bcc, bodyHtml, attachments, inReplyTo, references) { Owner = this };
        compose.SuggestContacts = query => SuggestAllContacts(query);
        // Exact lookup (not the substring search SuggestContacts does) for the recipient chips'
        // own "is this actually someone I've mailed before" coloring and click-for-details popover.
        compose.FindContact = address => _mailStore?.FindContact(address) ?? _mail?.Contacts.FindContact(address);
        // Live lookup (not a snapshot at open time) so editing signatures while a compose window
        // is already open still offers the current set, not whatever existed a minute ago.
        compose.GetSignatures = () =>
            _settings.Signatures.Select(s => new ComposeWindow.SignatureOption { Name = s.Name, Html = s.BodyHtml }).ToList();

        // Periodic autosave (real mailboxes only — sample data has nothing at stake if a demo
        // window is lost). autosavedUid tracks the current autosave copy so each tick replaces the
        // previous one instead of littering Drafts with a new message every 30 seconds; both
        // closures below share it directly since they're declared in this same method scope.
        MailKit.UniqueId? autosavedUid = null;
        // Serializes the two closures below against each other (and against overlapping ticks of
        // the same one) — without this, a slow save still in flight when the next periodic tick (or
        // an explicit "Save Draft" click) fires meant both calls read the same stale autosavedUid,
        // both appended a *new* draft copy, and only whichever finished last ever updated
        // autosavedUid — the other's copy was silently orphaned in Drafts forever, never tracked
        // again by anything that could clean it up. A real lock (not just a bool flag) is needed
        // specifically so the save that was waiting runs *after* the first one updates autosavedUid,
        // and therefore replaces that copy instead of creating yet another one alongside it.
        var draftSaveLock = new SemaphoreSlim(1, 1);
        if (!UseMockData)
        {
            compose.AutoSaveDraft = async draft =>
            {
                if (_mail is null || !draft.HasContent)
                    return;
                await draftSaveLock.WaitAsync();
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
                finally
                {
                    draftSaveLock.Release();
                }
            };
        }

        // The explicit "Save Draft" button, and the Save choice on the close-confirmation prompt —
        // unlike AutoSaveDraft above, wired for mock data too, since an explicit user action should
        // give real feedback either way. Shares autosavedUid/localDraftId with the periodic and
        // on-close paths so repeated saves replace the same draft instead of piling up duplicates.
        string? localDraftId = null;
        compose.SaveDraftNow = async draft =>
        {
            if (!draft.HasContent)
                return false;

            if (UseMockData)
            {
                if (localDraftId is not null)
                    RemoveMessageEverywhere(localDraftId);
                localDraftId = AddLocalMessage("Drafts", draft, "Saved to Drafts");
                return true;
            }

            if (_mail is null)
                return false;
            await draftSaveLock.WaitAsync();
            try
            {
                var (saved, newUid) = await _mail.SaveDraftAsync(draft, autosavedUid);
                if (saved)
                    autosavedUid = newUid ?? autosavedUid;
                return saved;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                draftSaveLock.Release();
            }
        };

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
            // The message is being sent for real now (or queued to, which makes the autosaved copy
            // just as stale) — any autosaved copy in Drafts is no longer needed either way.
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

    /// <returns>The id assigned to the new row — callers that need to replace it later (repeated
    /// draft saves) use this instead of guessing at <see cref="_localIdSeq"/>'s internal state.</returns>
    private string AddLocalMessage(string folder, ComposeResult message, string status)
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
        return id;
    }

    private readonly Dictionary<string, MessageDetail> _localBodies = [];


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
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
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

        // Local results paint on every keystroke, not gated behind the debounce below — a
        // MailStore.Search call is a small indexed local read (a few ms), nothing here needs
        // protecting from running too often. Only the live IMAP fallback (RunFolderSearchAsync,
        // still reached via the debounce/Enter key) needs that protection.
        ShowLocalSearchResults(query, SpecialMailbox(_currentFolder), FolderDisplayName(_currentFolder));

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

    /// <summary>Operator, then a one-line plain-English description — shown in the hint popup in
    /// this order, and also what clicking a row inserts (just the operator half).</summary>
    private static readonly (string Operator, string Description)[] SearchOperatorHints =
    [
        ("from:", "messages from someone"),
        ("to:", "messages sent to someone"),
        ("subject:", "words in the subject"),
        ("has:attachment", "only messages with a file attached"),
        ("is:unread", "only unread messages"),
        ("is:starred", "only starred messages"),
        ("older_than:7d", "older than 7 days (also m, y)"),
        ("newer_than:1m", "newer than 1 month"),
        ("larger:5M", "bigger than 5 MB (also K, G)"),
    ];

    /// <summary>A hover tooltip alone meant nobody without a mouse sitting there for a beat would
    /// ever learn this syntax exists — this pops the same operators up as a real, clickable list
    /// the moment the box gets focus while still empty (an active search already speaks for
    /// itself, so the hint would just be clutter over real results).</summary>
    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(SearchBox.Text))
            return;

        if (SearchHintList.Children.Count == 0)
        {
            foreach (var (op, description) in SearchOperatorHints)
            {
                var row = new System.Windows.Controls.Button
                {
                    Tag = op,
                    Padding = new Thickness(8, 6, 8, 6),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                row.Content = new System.Windows.Controls.StackPanel
                {
                    Children =
                    {
                        new System.Windows.Controls.TextBlock
                        {
                            Text = op, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                        },
                        new System.Windows.Controls.TextBlock
                        {
                            Text = description, FontSize = 11,
                            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                        },
                    },
                };
                row.Click += SearchHintRow_Click;
                SearchHintList.Children.Add(row);
            }
        }

        SearchHintPopup.IsOpen = true;
    }

    private void SearchHintRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string op })
            return;
        SearchBox.Text = op;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        SearchHintPopup.IsOpen = false;
        SearchBox.Focus();
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e) => SearchHintPopup.IsOpen = false;

    /// <summary>Paints whatever MailStore's own instant index already knows about this query — a
    /// small indexed local read, no network wait — so it can run directly from every keystroke
    /// (see SearchBox_TextChanged) instead of being stuck behind the same debounce that exists only
    /// to protect the live IMAP fallback in RunFolderSearchAsync from firing on every keystroke.
    /// Returns whether anything local matched (RunFolderSearchAsync uses this to decide whether to
    /// show its own "Searching…" status instead).</summary>
    private bool ShowLocalSearchResults(string query, string mailbox, string folderLabel)
    {
        var localResults = _mailStore?.Search(query, mailbox) ?? [];
        // The box may have changed since this was kicked off — never paint a stale query's results
        // over whatever the user has since typed.
        if (localResults.Count == 0 || query != SearchBox.Text.Trim())
            return false;

        if (_mailStore is not null)
            localResults = _mailStore.AttachReplyStatus(localResults, mailbox, SpecialMailbox("Sent"));

        _serverSearchActive = true;
        ReplaceMessages(SortRows(localResults));
        PagerBar.Visibility = Visibility.Collapsed;
        UpdateListEmptyState();
        StatusText.Text = $"{localResults.Count} match{(localResults.Count == 1 ? "" : "es")} for \"{query}\" in {folderLabel} — checking message content…";
        return true;
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
        var mailbox = SpecialMailbox(_currentFolder);

        // A match that's purely in the message body (not indexed locally — see MailStore's own
        // remarks on why) or in a message not yet synced locally won't show up from the instant
        // local pass — that's exactly what the server round trip below is still for.
        if (!ShowLocalSearchResults(query, mailbox, folderLabel))
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

            // The server's own full-folder search is authoritative for completeness (it also
            // catches body-text matches and anything not yet synced locally) — its results replace
            // whatever the instant local pass above showed, which was only ever a head start.
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

        _mailStore?.UpdateFlags(SpecialMailbox(_currentFolder), row.Id, starred: starred);
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

        // Same reasoning as the single-message path in MainWindow.Compose.cs's RemoveMessageAsync —
        // a non-permanent bulk delete already gets an Undo toast below, so the modal is reserved
        // for the one case Undo can't cover: deleting from Trash itself.
        if (verb == "Deleted" && _currentFolder == "Trash" && !ConfirmDelete(ids.Count))
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
        // One undo per message, not just the last — ConsumeLastMove only ever holds the single
        // most recent move, so it has to be read right after each id's own liveAction call, before
        // the next iteration overwrites it. Collecting all of them is what makes a bulk archive/
        // delete of N messages get the same Undo safety net a single one already had, instead of
        // silently losing recoverability past the first message the moment a second one moved.
        var undos = new List<Mail.ImapMailBackend.MoveUndo>();
        try
        {
            foreach (var id in ids)
            {
                if (!await liveAction(id))
                    continue;
                applyLocally(id);
                done++;
                if (_mail!.ConsumeLastMove() is { } move)
                    undos.Add(move);
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

        if (undos.Count > 0)
        {
            ShowActionUndo(verb, async () =>
            {
                var restored = 0;
                foreach (var move in undos)
                    if (await _mail!.UndoMoveAsync(move))
                        restored++;
                if (restored > 0)
                    await RefreshMessagesAsync();
                StatusText.Text = restored == undos.Count
                    ? $"Undid {restored}"
                    : $"Undid {restored} of {undos.Count} — the rest may have moved again since";
            });
        }

        ClearSelection();
        await RefreshMessagesAsync();
    }

}
