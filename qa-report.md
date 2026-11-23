# Purplemail QA Report

**Reviewed:** 2026-09-08
**Scope:** commit `7222146` ("updated, not working, mails not loading") plus the uncommitted working-tree changes on top of it
**Build:** `dotnet build EmailClient.sln -c Debug` — 0 warnings, 0 errors
**Tests:** `dotnet test` — 150 / 150 passing

## QA team

| Lead | Focus |
|---|---|
| Priya Rao | Data & sync layer — `MailStore.cs`, `MailSyncEngine.cs`, `ImapMailBackend.cs` |
| Devika Shah | Functional regression — folders, offline queue, compose, message list |
| Marcus Chen | UI/UX fidelity — reading pane vs. Apple Mail's conversation view |
| Owen Bright | Security & reliability — credentials, TLS, SQL, HTML sanitization |
| Lena Ortiz | Build, packaging & test coverage |
| Riya Kapoor | MIME body & charset decoding — plain/HTML rendering, inline images |
| Noah Fischer | Conversation & thread assembly — quote/forward nesting |
| Tara Singh | Sent-mail rendering — From/To framing, self-sent, reply-to-sent |
| Felix Wang | Attachments & inline media — viewer fidelity, filenames |

---

## Root cause — "mails not loading"

**Critical. Priya Rao.**

`MainWindow.xaml.cs:333` (`TryConnectAsync`) constructs `new MailStore(account.Email)` **before** the surrounding try block starts:

```csharp
var mailStore = new MailStore(account.Email);   // not inside the try block
var skipContactsScan = mailStore.ListAllContacts().Count > 0;

var backend = new ImapMailBackend(account);
try { await backend.ConnectAsync(skipContactsScan); }
catch (Exception ex) { mailStore.Dispose(); ... }
```

`MailStore`'s constructor (`MailStore.cs:56-78`) opens two SQLite connections, sets WAL mode, creates six tables, two FTS5 virtual tables, six triggers, and runs a migration — all before the `try` block exists. Any failure there (a locked or corrupt `mail.db`/`-wal`/`-shm` left over from a prior crash, a disk/permission issue) throws an exception that `TryConnectAsync` never catches.

The exception escapes the `async void MainWindow_Loaded` handler and is only caught by the global `App_DispatcherUnhandledException` handler, which shows one dialog and continues. `_mail` and `_mailStore` are never assigned, `AutoReconnectLoopAsync` never starts, and the window is left permanently on the cached inbox with no further attempt to connect — exactly the reported symptom, with no error visible after the one dialog is dismissed.

**Fix direction:** move the `MailStore` construction inside the existing try block, or give it its own catch with the same offline-fallback path used for a failed `ConnectAsync`.

---

## Data & sync layer — Priya Rao

- **[High]** `MailSyncEngine.cs` has zero direct unit tests. 150 tests exist for `MailStore` alone; nothing exercises the sync loop, its folder-priority ordering, or its call into `ImapMailBackend.FetchRecentEnvelopesAsync`.
- **[Sound]** The two-connection SQLite design (`_writeConn`/`_readConn`, per-connection locks) correctly serializes access between the UI thread and the sync engine's background task. `AcquireImapLockAsync` uses `SemaphoreSlim.WaitAsync` with `AsyncLocal` re-entrancy scoping — no deadlock path found. No empty catch blocks in the sync/store files; every failure logs via `Log.Warn`/`Log.Debug`.
- **[Low]** `async void` Dispatcher callbacks at `MainWindow.xaml.cs:423,428,433` (`Dispatcher.BeginInvoke(async () => ...)` has no `Func<Task>` overload). Not currently dangerous since both target methods wrap themselves in try/catch, but a latent footgun if that error handling is ever narrowed.
- **[Low]** `ImapMailBackend.cs:632-681` (`FetchRecentEnvelopesAsync`) — the `finally` block only restores the original folder when `originalFolder is not null`. Current call order avoids triggering it, but it's a real gap if the ordering ever changes.

## Functional regression — Devika Shah

- **[High]** **Move-to-folder leaves a stale row.** Both drag-and-drop (`MainWindow.Folders.cs:550-557`) and the context-menu move (`:734-742`) call `MoveToFolderAsync` then `RemoveMessageEverywhere`, but never `_mailStore?.DeleteMessage` — unlike the delete/archive path, which added exactly that call. The offline-queued branch (`:749-761`) has the same gap.
  *Repro:* drag a message out of Inbox, leave the folder and come back before the next 60s sync sweep — it's still there, read from the stale local row.
- **[High]** **Offline-action replay doesn't write through to MailStore.** `ReplayOfflineActionsAsync` (`MainWindow.OfflineActions.cs:40-75`) applies each queued delete/archive/mark/move only against live IMAP — none of the branches touch `_mailStore`. The refresh that follows reads the local store first, which still holds pre-replay state.
  *Repro:* delete or mark-read several messages offline, reconnect — the status bar says "Synced N offline actions," but the list shows the old state for up to a minute.
- **[Sound]** Folder switching (request-sequence guard against fast-click races), pagination (reads the live cursor, not a stale page number), local FTS5 search (correctly superseded by the live result), and drafts (new `SemaphoreSlim` closing a real duplicate-draft race) all hold up under review. `MailModels.cs`'s only change is a new optional field with a safe default — no break for older cached JSON.

## Apple Mail conversation-view fidelity — Marcus Chen

1. **[Critical]** The 36-token palette centralization never reached the reading pane. The card HTML (`MainWindow.Reading.cs:43-126`) has ~16 hardcoded hex values of its own, none referencing the shared tokens.
2. **[Critical]** Two different purples for the same sender's avatar. The native avatar uses `#A082D0 → #7E5DB0`; the in-card avatar (`:77`) still uses the rejected full-saturation `#8b5cf6 → #6d28d9` — the same person's avatar changes color between the row and the open message.
3. **[High]** The quote-disclosure "•••" toggle (`:109-111`) snaps open (`display:none → block`) instead of animating, unlike Apple Mail's reveal.
4. **[High]** No hover state on the card itself — sub-elements have hover states, the card body doesn't, so sibling cards in a thread read as inert.
5. **[High]** The pane visibly flashes when cross-folder sibling messages enrich in the background (`EnrichReadingPaneWithSiblingsAsync` re-navigates the whole pane with no cross-fade).
6. **[Medium]** Type hierarchy is flatter than Mail's — Segoe UI is a reasonable SF Pro stand-in, but the sender-name/body size-and-weight gap is smaller than Mail's.
7. **[Verified]** Quote/blockquote separation is a genuine DOM query (AngleSharp), correctly handling Gmail/Outlook conventions. Card radius, shadow, and per-card star/unread indicator are well-tuned.

## Security & reliability — Owen Bright

- **[Medium]** Offline-action queue and scheduled-send store aren't crash-safe. `OfflineActionQueue.cs:64-75` and `ScheduledSendStore.cs:58-69` write with plain `File.WriteAllText` instead of going through `AtomicFile` (as `MessageCache`/`AccountStore` correctly do). A crash mid-write leaves invalid JSON; `Load()` catches the parse failure and silently returns an empty list — a queued offline action or a scheduled "send later" message can vanish with no error shown.
- **[Low]** `data:` links in message HTML bypass the navigation guard. The sanitizer allows `data:` for embedded images, but `NavigationStarting` (`MainWindow.Reading.cs:363-406`) only intercepts http(s), the app's own custom schemes, and mailto — a crafted `<a href="data:text/html;base64,...">` link navigates the pane in place. Script stays disabled, so this is a local UI-spoofing/phishing risk, not code execution.
- **[Verified sound]** No TLS certificate-validation bypass anywhere; IMAP/SMTP consistently negotiate real TLS. Every `MailStore` query, including the dynamic flag-update column list, is parameterized. DPAPI-backed credential storage matches documentation, and the password is never written to the log.

## Build, packaging & coverage — Lena Ortiz

- **[Ruled out]** SQLite native-dependency packaging is correct end to end (`deps.json` targets `win-x64` and lists `e_sqlite3.dll`; all five required files are present on disk; `Purplemail.iss`'s wildcard copy pulls them into the installer automatically). This was the leading alternative hypothesis for "mails not loading" and does not hold up.
- **[High]** Confirms independently that `MailSyncEngine.cs` — the newest, largest file in the diff — has no test file (`grep -rn "MailSyncEngine" tests/` matches only compiled binaries).
- **[Low]** The runtime identifier (`win-x64`) lives in `build/publish.ps1`, not the `.csproj`. A plain `dotnet publish` run outside that script would produce a framework-dependent, no-RID build that never pulls the native SQLite assets — silently reproducing the same failure mode just ruled out above. Version numbers (1.0.39) are otherwise consistent between `Purplemail.iss` and `EmailClient.csproj`.

---

## Critical — sent mail misrenders on a real account

**Tara Singh, Sent-Mail Lead.** The fix `PROGRESS.md` claims ("Sent and Drafts rows show the recipient, not You") was only ever implemented in the mock-data/local-echo path. The real-IMAP row builder, `ToRow` (`ImapMailBackend.cs:1041-1059`), builds every row — for every folder, Sent included — from `Envelope.From` alone, with no field anywhere for "this is a Sent row, show the recipient instead." **On a live account, every row in Sent shows your own name and avatar** — exactly the "tells you nothing" failure the docs describe fixing. This also propagates into the reading-pane header and conversation-card fallback text for unfetchable siblings.

Worse: `ReplyButton_Click` (`MainWindow.Compose.cs:387-407`) unconditionally sets `To = detail.From`. Opened on a message in your own Sent folder, `From` is your own address — **plain Reply produces a compose window addressed back to yourself**, with the real recipient dropped entirely, not even carried into Cc. Reply-All keeps the recipients but wrongly demotes them all to Cc while To stays your own address.

A related, currently-masked issue: multi-recipient Sent/Drafts rows (`MailText.cs:87-98`, `DisplayName`) truncate to the first name found via `IndexOf('<')` with no "+N" indicator — invisible today only because it's on the same mock-only code path as the row-source bug above.

**What already works correctly:** Special-use Sent-folder detection uses real IMAP flags, not a hardcoded name. Bcc handling is correct — the same `MimeMessage` is both SMTP-sent and IMAP-appended to Sent, and MailKit strips the Bcc header on the wire while it survives in the local copy. `ContactsIndex` correctly scans Sent's To/Cc and excludes the user's own address. Per-message conversation cards use each message's own `From`, so a real back-and-forth thread renders each card's sender correctly — only the folder-level row list and Reply/Reply-All are affected.

**Fix direction:** thread folder/mailbox context into `ToRow` so a Sent-folder row displays `Envelope.To`; branch Reply/Reply-All on whether the opened message's From is the signed-in account before deciding which address becomes To.

---

## Body rendering & charset decoding — Riya Kapoor

- **[Medium]** Long unbroken plain-text lines are silently clipped, not wrapped. Plain-text bodies are correctly HTML-escaped and wrapped in `<p>` (`ImapMailBackend.cs:1311-1323`), but `WrapHtml`'s CSS only sets `white-space: pre-wrap; word-wrap: break-word` on `<pre>`, not on `body`/`p`, and `html`/`body` set `overflow-x: hidden`. A plain-text message with one very long unbroken token (a long URL, a base64 blob) overflows the line box and is invisibly clipped rather than wrapped.
- **[Low]** Snippet/quote text extraction (`MailText.cs:31`, `AnyTag` regex) uses regex tag-stripping rather than the AngleSharp DOM parser the reading pane itself uses — low exploitability since it's never handed straight to the WebView2 pane, but inconsistent with the codebase's own DOM-first policy elsewhere.
- **[Verified sound]** No hardcoded `Encoding.UTF8/ASCII` anywhere in the body-decode path — MimeKit's own charset-aware decode is trusted throughout, so non-Latin scripts, RTL text, and emoji are not at risk. `ResolveEmbeddedImages` (`ImapMailBackend.cs:1335-1358`) genuinely resolves `cid:` references to inline `data:` URIs before rendering. Reply/forward quoting applies exactly one encode pass with no double-encoding.

## Conversation & thread assembly — Noah Fischer

- **[High]** Cross-folder UID collision in the session caches — wrong message body can be shown. `_localBodies` and `_messageDetailCache` (`MainWindow.MessageList.cs:745`, `MainWindow.Reading.cs:736`) are keyed by UID string *alone*, unlike `MailStore.GetBody`'s correct `(mailbox, uid)` key. IMAP UIDs are only unique within a folder.
  *Repro:* open Inbox UID 42 (cached under key "42"), then open or thread-in Sent UID 42, a genuinely different message — the cache returns Inbox 42's body for the Sent 42 request.
- **[High]** A forwarded reply's quoted history can be silently deleted instead of folded. `suppressNestedQuotes` (`MainWindow.Reading.cs:1387`) strips the first blockquote/`.gmail_quote`/`hr` block it finds in any card once a conversation has more than one card, assuming it's a duplicate shown elsewhere. This app's own forward format splices the forwarded payload in flat with no wrapping tag, so a forward of a message that was itself a reply (containing a real blockquote quoting an even-earlier, unsynced message) has that inner quote deleted outright, with no toggle to recover it.
  *Repro:* Carol sends M1 → Bob replies as Y1 (quotes M1) → Bob forwards Y1 to Eve as X2 → Eve replies as Z. Opening Z shows `[Z, X2]`; X2's card loses M1's entire quoted body.
- **[Sound]** Thread identity, cross-folder body storage, and reply/forward quote sourcing are all correct. Sibling-finding matches strictly on Message-ID/References (`MailStore.cs:732-767`) — subject-normalization is used only for a cosmetic badge in mock data, never real thread assembly. Persisted cross-folder lookups are correctly `(mailbox, uid)`-keyed. Missing/deleted References fail gracefully. Reply/Reply-All/Forward always quote the single opened message's own raw body, never the assembled multi-card HTML.

## Attachments & inline media — Felix Wang

- **[Medium]** Oversized "images" bypass the safe data-URI rendering path. Under 8 MB, an image attachment is only ever placed as an inert `<img src="data:...">` value. Above that threshold (`AttachmentViewerWindow.xaml.cs:143-146`), the code instead does a direct `CoreWebView2.Navigate` to the local `file://` path — the one branch that departs from "never hand attacker content straight to the browser."
  *Repro:* rename an HTML file containing a script payload to `evil.jpg`, pad it past 8 MB, attach and open it.
- **[Low]** Identical attachment filenames on one message collide on "Download all." `MainWindow.Reading.cs:1619-1638` copies each attachment to `Path.Combine(destFolder, SafeName(attachment.Name))` with `overwrite:true` and no suffixing — two attachments both named `invoice.pdf` leave only the second on disk, with no warning.
- **[Verified sound]** PDF/image/text viewer split, filename decoding, and path-traversal protection all hold up. Text-like files (including a spoofed `.html`/`.svg`) are always HTML-encoded into a `<pre>`, never parsed as markup — the "never executed" claim holds regardless of a mismatched extension. RFC 2047/2231 filenames are handled by MimeKit with no risky re-decode. Path traversal is blocked: invalid filename characters, including `\` and `/`, are stripped before any `Path.Combine` call. Duplicate-attach prevention in Compose and correct exclusion of `cid:`-inline images from the attachment-chip list both work as documented.

---

## Destructive-action safety — found after the fact, now fixed

**Gap in QA scope, not just in the app.** None of the nine leads above were briefed to audit "every button for confirm-before-destructive-action and misclick-prone placement" — that's a distinct interaction-safety lens the original briefs didn't cover. The user caught the first instance by hand; a follow-up sweep of every `Click` handler in the app found a second.

- **[Fixed] Sign out had no confirmation and sat immediately below Settings…** with no separator. `ShowAccountMenu` (`MainWindow.xaml.cs:711-716`, original) wired `signOutItem.Click` straight to `SignOutActiveAsync()` — one misclick on the adjacent Settings item and the account was signed out instantly, unlike every other destructive action in the app (delete message, delete folder, remove account all confirm first). Fixed: added a `ConfirmDialog` prompt and a separator above the item.
- **[Fixed] Deleting a signature had no confirmation.** `DeleteSignatureButton_Click` (`AccountWindow.xaml.cs:687`, original) removed the selected signature immediately with no undo. Fixed: added the same `ConfirmDialog` pattern used by Delete Folder/Remove Account/Remove Contact.
- **[Verified sound]** Every other destructive action already funnels through `ConfirmDialog` correctly: bulk delete, single-message delete (toolbar/context-menu/keyboard shortcuts all share one `ConfirmDelete` gate), delete folder, remove account, remove contact, cancel scheduled send, closing Compose with unsaved changes, and the suspicious-link warning. Archive, clear-selection, and reset-panel-sizes are correctly left unconfirmed since they're trivially reversible.

## Performance — attachment opening

**High.** Opening an attachment (e.g. clicking a PDF chip) shows "Downloading…" for several seconds regardless of file size, for two compounding reasons:

1. **No disk-cache check.** `ResolveAttachmentAsync` (`MainWindow.Reading.cs:1471-1485`) builds the on-disk cache path but never checks `File.Exists(path)` before calling into IMAP — the exact same attachment gets fully re-downloaded from the server every time it's opened, even a second time in the same session.
2. **A single-attachment request can trigger a full-message fetch.** `DownloadAttachmentAsync` (`ImapMailBackend.cs:1798-1833`) only uses the already-in-memory cached message when the attachment belongs to the *exact* message currently held in `_cachedMessage` (true right after opening that specific message). Otherwise — a thread sibling's attachment, a reopened message, anything else — it calls `_current.GetMessageAsync(uid)`, which fetches the **entire** RFC822 message (full body, every embedded image, every other attachment) over IMAP just to extract the one small part requested.

The download also waits on `AcquireImapLockAsync`, the same lock shared with the background IMAP IDLE connection and `MailSyncEngine`'s sync loop, adding further unpredictable queuing delay.

**Fix direction:** short-circuit with a `File.Exists(path)` check before any IMAP call; fetch only the requested MIME part's content on a cache miss instead of the whole message.

## Recommended order of fixes

1. Move `MailStore` construction inside `TryConnectAsync`'s try block — this is the fix for the reported "mails not loading."
2. Make Sent/Drafts rows show the recipient, and fix Reply/Reply-All on a message you sent — currently every Sent-folder row on a real account shows your own name, and replying to your own sent mail addresses the new message back to yourself.
3. Write through to `_mailStore` on move and on offline-action replay (same pattern already used for delete/archive).
4. Key `_localBodies`/`_messageDetailCache` by `(mailbox, uid)`, not UID alone — closes the cross-folder UID collision that can show the wrong message's body.
5. Add a disk-cache check to `ResolveAttachmentAsync` and fetch only the requested MIME part on a cache miss, instead of the whole message — this is the attachment-open slowness fix.
6. Add a test file for `MailSyncEngine` — the component most likely to regress again silently.
7. Route offline-queue and scheduled-send writes through `AtomicFile`.
8. Unify the reading pane's hardcoded hex values with `Palette.Light.xaml`, starting with the avatar gradient.
9. Animate the quote disclosure, add a card hover state, and fix plain-text word-wrap.

---

*Methodology: build and test suite verified directly; all five leads performed static code review against the working tree. No live IMAP session or on-screen GUI pass was available in this environment — items needing runtime verification are noted as such above rather than claimed as confirmed.*
