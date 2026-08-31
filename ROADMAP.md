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
- ✅ **Desktop notifications for new mail** — every arrival raises a notification (on Windows 10/11
  the shell renders a `NotifyIcon` balloon as a real toast, so this needs no packaged identity).
  `Mail/NewMailNotifier.cs` decides what to announce: it tracks message ids instead of the old
  "top row changed" guess, so a burst of five announces all five, a delete-then-refresh announces
  nothing, and the first page after sign-in is treated as history rather than news. Clicking the
  notification restores the window and opens that message. Unit-tested (`NewMailNotifierTests`).
- **Notification actions** — inline "Archive"/"Reply" buttons on the toast. Still open: this needs
  `CommunityToolkit.WinUI.Notifications` plus a TFM bump and a COM activator to work unpackaged,
  which is a bigger change than the notification itself.
- ✅ **Taskbar unread badge overlay** (`UI/TaskbarBadge.cs`) — the unread count drawn onto the
  taskbar button, plus the same count on the tray icon's hover tooltip, so it stays readable while
  the window is hidden and the toast has expired.

### Tier 3 — Polish & power-user niceties
- **Dark mode** — *groundwork done, feature backed out.* The premise above was wrong: the palette
  was not centralized, it was 259 hardcoded hex values across six XAML files. Those are now 36
  semantic tokens in `Themes/Palette.Light.xaml`, referenced by `DynamicResource` everywhere, so
  an alternative palette is a drop-in. A dark palette was built on top and removed: it loaded, but
  the main window never became visible, and light is what actually gets used day to day. Whoever
  picks this up starts from a real palette rather than a search-and-replace.
- **Accent colour picker** — now a small change: one token.
- **Density toggle** — compact/comfortable/cozy message list rows (Gmail).
- **Zoom/font-size control** for the reading pane.
- **Multiple compose windows** open simultaneously (currently `ComposeWindow` is a modal `ShowDialog`, blocking the main window — Gmail/Outlook allow several drafts open at once as non-modal, possibly minimizable, windows).
- **Pop-out reading pane** to its own window (Gmail "open in new window").
- **Jump list** (right-click taskbar icon → "Compose new message" shortcut).
- ✅ **Single-instance enforcement** (`SingleInstance.cs`) — a mutex plus a named event; a second
  launch tells the running instance to surface and exits. Matters more now the window hides in the
  tray, since re-launching is the obvious way to try to get it back.
- ✅ **Local message cache for offline reading** (`Mail/MessageCache.cs`) — per-account JSON on
  disk: the first page of each folder plus the last 300 opened bodies. The list is painted from it
  before the first network call, so signing in isn't a blank window, and a failed sign-in now reads
  "Offline — showing saved mail" instead of showing nothing. Cleared on sign-out. 9 unit tests.
- ✅ **Settings page** exists on the account page (undo-send delay, manage muted senders, and a
  new Notifications & tray block: new-mail notifications on/off, close-to-tray, start-with-Windows)
  — still missing: theme, density, reading-pane position, about/version. (Sound and banner style
  are Windows' own per-app notification settings, not something the app can override.)
- ✅ **Start with Windows** toggle — `Settings/StartupRegistration.cs`, the per-user Run key. The
  registry is the source of truth rather than a cached bool, since Task Manager's Startup tab can
  turn it off without the app knowing. Launches with `--tray` so a sign-in start comes up in the
  tray instead of throwing a window onto a fresh desktop.
- ✅ **Accessibility — message rows.** Each row now announces as one sentence ("Unread message
  from Ada. Lunch?. 9:00 AM, starred") instead of four unrelated fragments, and the per-row star
  and select controls name the row they act on. Combined with the 20 icon-only buttons labelled
  earlier, the main surfaces are covered. Still open: high-contrast theme support, and full
  keyboard navigation of the 3-pane layout.

### Tier 4 — Distribution
- ✅ **App icon/branding** — the `imageres.dll` placeholder is gone; `icon.ico` is now the app's own
  purple envelope mark, generated at nine sizes (16–256) so it stays clean in the tray and taskbar.
  The exe is `Purplemail.exe` and carries real product/company/version metadata.
- ✅ **Installer** — `build/Purplemail.iss` (Inno Setup) + `build/publish.ps1`. Per-user install so
  it needs no elevation, matching the app's per-user settings and HKCU startup entry; Start Menu
  and optional desktop shortcut; a startup task writing the *same* Run key the in-app checkbox
  uses; a WebView2 runtime check up front; and an uninstall that deliberately leaves the user's
  settings, signatures and contacts alone. **Not yet run end to end** — Inno Setup isn't installed
  on this machine (`winget install --id JRSoftware.InnoSetup -e`); the publish half is verified.
- ❌ **Code signing** — **decided against (2026-08-31): not needed.** Purplemail is a personal
  client installed from a local build. SmartScreen only fires on the Mark of the Web, the zone tag
  Windows attaches to browser downloads — an installer run from disk or a USB stick carries none,
  so no warning appears. A certificate would cost ~$120–600/yr (the cheap download-a-.pfx option
  ended in June 2023, when the CA/Browser Forum began requiring FIPS-140-2 hardware keys) to solve
  a problem that doesn't occur here. Revisit only if the app is ever distributed to other people
  over the web.
- **Auto-update** — check-for-updates against a release feed (e.g. GitHub Releases, matching the
  existing `bstg8604/emailwrapper` repo). Low value while this is a single-user app installed from
  a local build — re-running `build/publish.ps1` *is* the update.
- ✅ **App naming** — settled: **Purplemail**, and now carried consistently through the executable
  name, assembly metadata, icon, installer and tray tooltip.

## Suggested working order

**Done so far:** Tier 0 (live IMAP/SMTP + IDLE) and effectively all of Tier 1 and the
signature/contacts/settings-page slice of Tier 2 — conversation view, compose attachments, rich
text with font family/size and live preview, move-to-folder, drafts, signatures, contacts
(auto + manual). See `PROGRESS.md` for the detailed, current write-up.

**Remaining, in recommended order:**
1. Rest of Tier 2 — snooze, scheduled send, flag/important marker, advanced search, filters/rules.
   (Desktop notifications and the taskbar badge are done as of 2026-08-31; only the toast's inline
   Archive/Reply *actions* remain, and those need the WinUI notifications package.)
2. Tier 3 — offline cache and accessibility are done; dark mode has its groundwork (see above).
   Remaining: density toggle, multiple compose windows, pop-out reading pane, jump list.
3. Tier 4 — naming, icon, and installer are done; code signing is decided against and auto-update
   is low value for a single-user app. The only open item is running the installer end to end once
   Inno Setup is present.

**Known gap worth fixing early in Tier 3:** ✅ done 2026-08-29 — 20 icon-only buttons now carry
`AutomationProperties.Name`. It paid off immediately: the new move-to-folder flow was verified
through UI Automation because of it. (Per-row star toggles and checkboxes live inside a
`DataTemplate` and still have none — reachable only via the UIA raw tree.)

## Verification approach
Since most of Tier 1+ is currently exercised through `UseMockData` (now `_mail is null`), each feature should be built and demoed against `MockData.cs` first (fast iteration, no login needed), the same way Compose/reply/star/delete were validated in the current build. Backend-touching pieces need a live-login pass per `PLAN.md`'s "Known open items" before being trusted.
