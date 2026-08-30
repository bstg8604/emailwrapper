# Purplemail — Full Feature Roadmap

## Context
The app (named **Purplemail**) is a real IMAP/SMTP desktop email client for the user's IITB
mailbox (MailKit/MimeKit), not a webmail-automation wrapper — see `PLAN.md` for the current
architecture and `PROGRESS.md` for the up-to-date state as of 2026-08-31. The paragraph below
describing a WebView2/Roundcube-puppeting `DomBridge` is history from before the IMAP rewrite;
kept for context on how the project got here.

The app began as a custom WPF frontend that puppeted the real webmail.iitb.ac.in (Roundcube) session in a hidden WebView2, rather than reimplementing an email client. So far it has: a 3-pane shell, mock-data-driven inbox/sent/drafts/trash/starred, search, quick filters, compose with reply/forward prefill, star/archive/delete, tray + custom window chrome, and a `DomBridge` with Roundcube-aware (but not yet live-verified) selectors.

The user now wants "every feature/option" — explicitly benchmarking against Gmail, Apple Mail, Outlook, and Betterbird/Thunderbird. This plan inventories that full feature set, grouped by priority, so it can be worked through systematically rather than as an unbounded, undefined blob of "more features." Nothing here is committed to blindly — the **Known open risk** from `PLAN.md` still applies: DomBridge automation against the real Roundcube instance needs live verification before any backend-touching feature (send, delete, mark-read, etc.) can be trusted end-to-end.

> **Status note (2026-08-28):** items marked ✅ below are built and verified against sample
> data — see `PROGRESS.md` for the current state, what was tested, and bugs fixed.
> Everything backend-facing still depends on Tier 0.

## Priority tiers

### Tier 0 — SUPERSEDED (2026-08-29)
The app moved from webmail-automation (WebView2 puppeting Roundcube) to a real IMAP/SMTP client
— see `PLAN.md`. Every item below was about verifying and hardening that automation layer; none
of it applies anymore, since there's no DOM to scrape or selectors to verify. What replaced it:
IMAP folder/message/flag operations and SMTP send in `Mail/ImapMailBackend.cs`, done and building,
not yet tested against a real account. Left below for history only.

<details>
<summary>Original Tier 0 (webmail-automation era, no longer applicable)</summary>

### Tier 0 (historical) — Make the automation layer real (blocks everything backend-facing)
Currently `UseMockData = true` in `MainWindow.xaml.cs` short-circuits almost everything. Nothing in Tiers 1–3 that touches real mail is trustworthy until this is done:
1. Live-verify `DomBridge.cs` selectors against the authenticated site via the existing "Inspect webmail (DevTools)" button — rows, compose/send, preview iframe. **← the only item left; needs a real login**
2. ✅ Handle session expiry mid-use (re-show `AutomationHost` for re-login, resume the pending action after). Every injected script now starts with a login-form guard; on a hit the bridge triggers re-login and retries the same script.
3. ✅ Extend `DomBridge` beyond Inbox: `ListFoldersAsync` / `SelectFolderAsync` navigate Roundcube for real, and the sidebar lists the account's actual IMAP folders. Special-folder names come from Roundcube's own `env` rather than being guessed.
4. ✅ Pagination — `ListMessagesAsync` returns a `MessagePage` with page/pageCount/total, driven by a new pager bar via Roundcube's own pager controls.
5. ✅ Mark read/unread, delete, archive, move-to-folder as real `DomBridge` actions. Each clicks the real Roundcube control first and only falls back to `rcmail.command(...)` — Roundcube's own dispatcher — if that control is absent in this skin.
6. ✅ Attachments: scraped from both the preview frame and top document; downloaded through `CoreWebView2.DownloadStarting` from a hidden iframe, so the automation page never navigates off the mailbox.

> Items 2–6 are **written and compiling, not yet live-verified** — they all run through the same
> selectors item 1 covers. See `PROGRESS.md` for the detail and for the per-user .NET SDK note.

</details>

### Tier 0 (new) — IMAP/SMTP backend
1. ✅ Connect, authenticate, list folders, list/open messages, flags, move/delete/archive, save
   draft — `Mail/ImapMailBackend.cs`. **Live-tested against the real account.**
2. ✅ Send via SMTP, with a Sent-folder copy appended afterward (plain SMTP doesn't file one on
   its own).
3. ✅ SMTP host/port confirmed working against the live account.
4. ✅ **IMAP IDLE** — a dedicated second connection (`_idleClient`) pushes new-mail notifications
   instantly instead of polling, with a circuit breaker after repeated IDLE failures.

### Tier 1 — Core mail-client parity (Gmail / Outlook / Apple Mail / Betterbird baseline)
- ✅ **Reply-all** — pre-fills To from sender, Cc from the original To+Cc.
- ✅ **CC/BCC fields** in `ComposeWindow.xaml`, collapsed behind a "Cc/Bcc" toggle like Gmail.
- ✅ **Multi-select in message list** — row checkboxes plus a bulk action bar (mark read / archive / delete / clear).
- ✅ **Mark unread/read toggle** — auto-read on open, explicit "mark unread" action, live Inbox unread badge.
- ✅ **Sort options** — date / sender / subject via the toolbar dropdown.
- ✅ **Image/external-content blocking** — remote `<img>` srcs are swapped for a transparent pixel with a "Show images" bar (`SanitizeRemoteImages` in `MainWindow.xaml.cs`).
- ✅ **Attachments (viewing)** — attachment chips in the reading pane open an **in-app viewer** (`UI/AttachmentViewerWindow`): PDFs in Edge's own viewer, images on the app backdrop, text-like files as monospace text, HTML/SVG as source rather than executed, and an honest fallback otherwise. Save-a-copy and Open-externally are secondary actions. Sample attachments are real generated files (PDF/PNG/TXT); the live path downloads through the authenticated session into the same viewer, pending Tier 0 verification.
- ✅ **Drafts** — closing compose with unsent content saves to Drafts; reopening a draft resumes editing, formatting included. `Mail/ImapMailBackend.SaveDraftAsync` appends a real draft via IMAP; not yet live-tested.
- ✅ **Conversation/thread view** — messages render as Apple Mail-style cards, siblings found
  folder-wide via `FindConversationSiblingsAsync`; nested quoted history is collapsed or cut to
  avoid showing the same content twice.
- ✅ **Attachments in Compose** — file picker *and* drag-and-drop, chips with real sizes, remove before sending. Sent as real MIME attachments via `MailKit.BodyBuilder`; not yet live-tested.
- ✅ **Rich text formatting toolbar** in Compose — bold/italic/underline/strikethrough, bulleted and numbered lists, indent/outdent, block quote, alignment, font size, colour, links, clear formatting, plus a plain-text toggle. Implemented as a WebView2 `contenteditable` rather than a WPF RichTextBox, so the body is already the HTML a MIME message wants — no FlowDocument-to-HTML layer in between. Replies/forwards quote the original as a real `<blockquote>` that keeps its formatting.
- ✅ **Move to folder** — a "Move to…" menu (drag-and-drop still open); folders themselves can now
  be created/renamed/deleted from the account page's Folders section.
- **Remaining message actions**: flag/important marker, drag-and-drop move, print.
- **View options** not yet done: toggle conversation view on/off; reading-pane position (right vs. bottom vs. off, like Outlook); sort by size.
- **External link warning** — confirm before opening links from message bodies in the default browser.

### Tier 2 — Productivity features (what makes Gmail/Superhuman/Outlook feel fast)
- ✅ **Keyboard shortcuts**, Gmail-style: `c` compose, `r`/`a`/`f` reply/reply-all/forward, `e` archive, `#` delete, `j`/`k` next/prev, `u` back to list, `/` focus search, `g` then `i`/`s`/`d`/`t` to switch folder, `?` cheat-sheet overlay (also reachable from the toolbar "?" button). Shortcuts are suppressed while a text field has focus.
- ✅ **Undo send** — Send hands the message to `MainWindow`, which shows a 5-second snackbar with UNDO before actually dispatching; undo reopens the message in compose. Sent mail lands in the Sent folder.
- **Snooze** — hide a message from Inbox until a later time (Gmail); a local UI/state feature — no server-side snooze exists over IMAP either, so this needs local scheduling + re-surfacing regardless of backend.
- **Scheduled send** — pick a future send time; same local-scheduling approach as snooze.
- ✅ **Signature** — multiple named signatures with a rich-text editor (bold/italic/underline/
  strikethrough/lists/indent/quote/alignment/colour/link/font family/point size with live hover
  preview), a default flag, and inline insertion from Compose (a single saved signature shows as
  a named button; more than one shows a dropdown).
- ✅ **Contacts autocomplete** — `Mail/ContactsIndex.cs`'s ranked Sent/Inbox correspondence history
  merged with manually-added contacts (`Settings/ManualContactsStore.cs`), wired into Compose's
  To/Cc/Bcc as a dropdown. No institute-wide directory lookup (IITB's LDAP directory isn't
  reachable off-campus — see `PLAN.md`); an LDAP settings slot could be added later for
  on-campus/VPN use.
- **Saved/advanced search** — filters by from/to/subject/date range/has-attachment/folder, with the ability to save a search as a quick filter (extending the existing `FilterMessage`/quick-filter-chip mechanism in `MainWindow.xaml.cs`).
- **Filters/rules** — "when mail matches X, do Y" (Outlook rules / Gmail filters) — IMAP has no built-in rule engine, so this means reimplementing rule matching locally on top of fetched mail, or driving Sieve if the server exposes ManageSieve.
- **Desktop notification actions** — Windows toast with inline "Archive"/"Reply" actions, not just a plain balloon (current `TrayIcon.ShowBalloon` is text-only).
- **Taskbar unread badge overlay** on the app's taskbar icon (Outlook/Mail app convention), in addition to the existing sidebar unread count.

### Tier 3 — Polish & power-user niceties
- **Dark mode** + accent color picker (the palette is centralized enough in `MainWindow.xaml`'s resource dictionary to theme relatively cleanly).
- **Density toggle** — compact/comfortable/cozy message list rows (Gmail).
- **Zoom/font-size control** for the reading pane.
- **Multiple compose windows** open simultaneously (currently `ComposeWindow` is a modal `ShowDialog`, blocking the main window — Gmail/Outlook allow several drafts open at once as non-modal, possibly minimizable, windows).
- **Pop-out reading pane** to its own window (Gmail "open in new window").
- **Jump list** (right-click taskbar icon → "Compose new message" shortcut).
- **Single-instance enforcement** — launching the exe again should focus the existing window/tray icon instead of opening a second instance.
- **Local message cache for offline reading** — persist fetched messages (e.g. SQLite or a simple JSON store) keyed by IMAP `UIDVALIDITY`+UID, so recently read mail is viewable without a live connection.
- ✅ **Settings page** exists on the account page (undo-send delay, manage muted senders) —
  still missing: notifications on/off, sound, theme, density, reading-pane position,
  keyboard-shortcut reference, start-with-Windows toggle, about/version.
- **Start with Windows** toggle (registry Run key or a Startup shortcut).
- **Accessibility** — screen-reader labels on icon-only buttons (compose/star/reply/etc. currently rely on glyphs + tooltip only), high-contrast theme support, full keyboard navigation of the 3-pane layout.

### Tier 4 — Distribution
- **App icon/branding** — replace the placeholder `icon.ico` (currently extracted from `imageres.dll`) once a name/identity is chosen.
- **Installer** — MSIX or an Inno Setup/WiX installer instead of the current `dotnet publish` single-exe, so Start Menu entry, uninstall, and auto-start registration work properly.
- **Auto-update** — check-for-updates against a release feed (e.g. GitHub Releases, matching the existing `bstg8604/emailwrapper` repo).
- **App naming** — still deferred per earlier conversation; needs to happen before icon/installer/branding work.

## Suggested working order

**Done so far:** Tier 0 (live IMAP/SMTP + IDLE) and effectively all of Tier 1 and the
signature/contacts/settings-page slice of Tier 2 — conversation view, compose attachments, rich
text with font family/size and live preview, move-to-folder, drafts, signatures, contacts
(auto + manual). See `PROGRESS.md` for the detailed, current write-up.

**Remaining, in recommended order:**
1. Rest of Tier 2 — snooze, scheduled send, flag/important marker, advanced search, filters/rules,
   rich toast notifications, taskbar unread badge.
2. Tier 3 — dark mode first (the settings page already exists, just needs a theme toggle wired to
   it), then density, multiple compose windows, offline cache, accessibility.
3. Tier 4 — naming/branding (app is now named Purplemail; icon/installer still use a placeholder),
   then icon, installer, auto-update.

**Known gap worth fixing early in Tier 3:** ✅ done 2026-08-29 — 20 icon-only buttons now carry
`AutomationProperties.Name`. It paid off immediately: the new move-to-folder flow was verified
through UI Automation because of it. (Per-row star toggles and checkboxes live inside a
`DataTemplate` and still have none — reachable only via the UIA raw tree.)

## Verification approach
Since most of Tier 1+ is currently exercised through `UseMockData` (now `_mail is null`), each feature should be built and demoed against `MockData.cs` first (fast iteration, no login needed), the same way Compose/reply/star/delete were validated in the current build. Backend-touching pieces need a live-login pass per `PLAN.md`'s "Known open items" before being trusted.
