# Progress Report

**Project:** Purplemail — custom desktop email client for the user's IITB mailbox
**Last updated:** 2026-08-31
**Repo:** https://github.com/bstg8604/emailwrapper

> **Everything from here to the next `---` is current as of 2026-08-31.** Sections further down
> (from "Architecture change (2026-08-29...)" onward) are older history, kept for context on how
> the app got here, but no longer describe the present state — see `ROADMAP.md` for the current
> tier-by-tier checklist.

## Current state (2026-08-31)

The app is renamed **Purplemail** and runs against the user's **real IITB mailbox over live
IMAP/SMTP** — no longer sample-data-only. `UseMockData` still exists as a fallback/demo mode, but
day-to-day use since the IMAP rewrite has been against the live account. Builds clean (0 warnings,
0 errors) throughout this pass.

### Built and working since the last full write-up
- **IMAP IDLE push** replaced the old 60-second poll — a dedicated second IMAP connection
  (`_idleClient` in `Mail/ImapMailBackend.cs`) sits in IDLE and fires `OnMailboxActivity` the
  moment new mail arrives, with a circuit breaker (`MaxConsecutiveIdleFailures = 3`) so a flaky
  IDLE session degrades instead of looping forever.
- **Conversation/thread view.** Every open message renders as one or more Apple Mail-style cards
  (a single message is just a conversation of one), with siblings found **folder-wide** via
  `FindConversationSiblingsAsync`, not just the loaded page. Nested quoted history inside a
  reply's own HTML is either collapsed behind a CSS checkbox-toggle ("•••") or cut outright when
  a sibling card already shows that same content — several rounds of regex work went into making
  this correct against real Gmail/Roundcube/Outlook quote conventions (embedded `mailto:` links
  inside the attribution line, forwarded-message markers nested inside a blockquote, a false-positive
  guard so a literal "on" inside a word like "Convocation" never gets mistaken for a quote start).
- **Apple Mail-exact attribution formatting** — `On {Month D, YYYY}, at {h:mm AM/PM}, {Name}
  <{email}> wrote:` for replies, `Begin forwarded message:` + From/Subject/Date/To(/Cc) for
  forwards, both using an always-absolute date (`MailText.FormatAbsoluteDate`) so a quote baked
  into a reply never freezes at a stale "Yesterday".
- **Account page rebuilt as a real settings surface** (`Mail/AccountWindow.xaml(.cs)`, renamed
  from the old `LoginWindow`): a left sidebar (Profile / Signature / Contacts / Folders /
  Settings) replaces the old two-tab pill switcher.
  - **Signature**: multiple named signatures, a rich-text WebView2 editor (`UI/RichHtmlEditor.cs`)
    with bold/italic/underline/strikethrough/lists/indent/quote/alignment/colour/link, **font
    family and point-size pickers with live hover preview** (hovering an option previews it on the
    selected text immediately, reverting if you move off without clicking), default-signature
    flag. In Compose, a single saved signature shows as one button named after that signature
    instead of a dropdown that only makes sense with an actual choice to make.
  - **Contacts**: manual add/edit/delete contacts (`Settings/ManualContactsStore.cs`, local JSON)
    merged with the existing auto-learned "people you've emailed" list into Compose's
    To/Cc/Bcc autocomplete.
  - **Folders**: create/rename/delete top-level folders via new `ImapMailBackend` methods
    (`CreateFolderAsync`/`RenameFolderAsync`/`DeleteFolderAsync`), disabled for Inbox and the
    resolved special folders; changes refresh the main window's own folder sidebar immediately.
  - **Settings**: undo-send delay (1–60s) and a manage-muted-senders list, both previously
    settable only through a Mute button with no way to review or undo.
- **Rich-editor line-break behaviour matches user expectation, not the browser default.** Plain
  Enter is a tight `<br>` line break (no paragraph gap); Shift+Enter explicitly inserts a visible
  blank-line gap. (The browser's native default is the reverse — Enter starts a new block with
  margin, Shift+Enter is the tight break — which is what prompted the change.)
  Implemented identically in both Compose's own editor and the shared `RichHtmlEditor.cs`.
- **Recipient lists (To/Cc) are expandable, not a dead-end "+6 more".** A CSS checkbox-toggle
  reveals the rest with a "show less" control that appears after the revealed names, not before.
  Sender/recipient addresses are real `mailto:` links (address only, not the display name,
  styled to inherit the surrounding text colour rather than the theme's purple) that open a
  pre-addressed Compose window.
- **Message-open responsiveness**: selecting a row now updates the header instantly from data
  already in hand and shows a lightweight loading state while the body fetches, with a
  staleness guard (`_openRequestSeq`) so a fast second click can't have an earlier fetch clobber
  it. A session-lifetime cache (`_messageDetailCache`) makes reopening an already-fetched message
  free. An earlier attempt at background prefetching over a second dedicated IMAP connection was
  tried and then **removed** — it added connection overhead without a proven latency win, and its
  WebView2 "Loading…" placeholder was doubling per-open navigation cost; the loading state is now
  a plain native WPF overlay instead of a second WebView2 navigation.
- **Message-list scrolling**: virtualization (`VirtualizingPanel.IsVirtualizing`) had regressed to
  off entirely — re-enabled with `ScrollUnit="Pixel"` and a larger cache window
  (`CacheLength="2,2"`), which fixed both jitter and dropped frames. A custom smooth-scroll
  animation was tried in between and made things worse on precision-touchpad input (miscalibrated
  for touchpad delta sizes vs. full mouse-wheel notches) — reverted in favour of WPF's own
  built-in pixel scrolling.
- **Transitions audit**: several Visibility toggles that used to snap instantly now animate via
  the existing `UI/Motion.cs` helpers (PagerBar, the live-folder sidebar section, Compose's
  Cc/Bcc rows and its link-insert bar), gated so they only animate a real collapsed↔visible
  transition, not every refresh.
- **Bug fixes this pass**: the toolbar's signed-in avatar circle was rendering its initial letter
  through the icon font it inherited from the button style (`Segoe Fluent Icons`), producing an
  unrelated glyph instead of the letter — fixed with an explicit `FontFamily` on that TextBlock.
  `QuickLookWindow` (image viewer) corner-rounding and cursor-anchored zoom fixed. Added
  "Helvetica" (falls back to Arial/sans-serif — Windows has no Helvetica font file) to the font
  pickers.

### Not yet done (unchanged from before, still open)
- Dark mode, density toggle, settings for theme/notifications/start-with-Windows.
- Snooze, scheduled send, filters/rules, saved/advanced search.
- Multiple non-modal compose windows, pop-out reading pane, offline/local message cache.
- Installer, auto-update, single-instance enforcement, app icon/branding.
- Full list with tier priorities is in `ROADMAP.md`.

---

> **Architecture change (2026-08-29, later same day):** the app was rewritten from a
> webmail-automation wrapper (WebView2 puppeting Roundcube's own UI) to a real IMAP/SMTP client
> (MailKit). The user confirmed their IITB credentials already work in Thunderbird, Betterbird and
> Gmail over IMAP/SMTP from off-campus, and a direct probe confirmed `imap.iitb.ac.in:993`
> answers with a live IMAP banner from outside the campus network — which made the wrapper's
> WebView2-puppeting approach unnecessary complexity for a server that was reachable directly the
> whole time. See `PLAN.md` for the full rationale and the new architecture; the section below is
> superseded except where it describes UI/UX still shared with the new build.

> **Build environment note (2026-08-29):** this machine had **no .NET SDK** — only the .NET 6
> runtime under `C:\Program Files\dotnet`, despite `PLAN.md` claiming .NET 9 was installed.
> The .NET 9 SDK (9.0.317) is now installed **per-user** at `%LOCALAPPDATA%\Microsoft\dotnet`.
> Consequences:
> - Build with `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe build EmailClient.sln`
>   (the `dotnet` on PATH is the old .NET 6 host and will say "No .NET SDKs were found").
> - To **run** the built exe, set `DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet` first, or the
>   apphost looks in `C:\Program Files\dotnet`, finds only .NET 6, and exits immediately.
> - Double-clicking the exe won't work until either the .NET 9 Desktop Runtime is installed
>   machine-wide or the app is published self-contained (see Tier 4 in `ROADMAP.md`).
>
> `git` is also not on PATH; GitHub Desktop bundles one at
> `%LOCALAPPDATA%\GitHubDesktop\app-3.6.4\resources\app\git\cmd\git.exe`.

---

## Current state (IMAP rewrite)

Builds clean: **0 warnings, 0 errors.** Runs on sample data by default; a "Sign in" button in the
toolbar (where "Inspect webmail" used to be) opens a credentials dialog, connects over IMAP/SMTP
via MailKit, and switches the whole UI to the real mailbox on success.

**What's new:**
- `Mail/ImapMailBackend.cs` — real IMAP (list folders, list/open messages, flags, move/delete/
  archive, save draft) and SMTP (send, with a Sent-folder copy appended afterward since plain
  SMTP doesn't file one itself).
- `Mail/AccountSettings.cs` — credentials persisted via Windows DPAPI, never plaintext.
- `Mail/LoginWindow.xaml` — sign-in dialog with an expandable "Server settings" section for
  IMAP/SMTP host and port.
- `Mail/ContactsIndex.cs` — recipient autocomplete built from Sent/Inbox headers (no institute
  LDAP directory is reachable off-campus — see `PLAN.md`), wired into `ComposeWindow`'s To/Cc/Bcc
  fields as a dropdown.
- `Automation/DomBridge.cs` and `Automation/AutomationHost.cs` (the WebView2-puppeting layer) are
  **deleted**. Their row/message/folder record types moved to `Automation/MailModels.cs`, which
  both the mock and live backends share.

**Not yet done:**
- SMTP host/port are best-guess defaults (`smtp-auth.iitb.ac.in:587`, STARTTLS) — need
  confirming against the user's known-working Thunderbird/Betterbird settings, editable in the
  sign-in dialog without a rebuild.
- Live refresh polls every 60s rather than using IMAP IDLE for instant push.
- Not yet tested against a real account (needs the user's own credentials, at the keyboard).

Everything below this point describes the earlier webmail-wrapper architecture and is retained
for history; see `PLAN.md`'s "Known open items" for the current list.

---

## What this is

A WPF app that puts a custom, minimal UI in front of the real IITB webmail (Roundcube).
It is *not* a new email client — the real webmail page runs hidden in a WebView2, and the
custom UI acts as a middleman that drives it. See `PLAN.md` for the architecture and
`ROADMAP.md` for the full feature backlog.

---

## Current state

The app **builds clean (0 warnings, 0 errors)** and runs. It currently operates on
**sample data** (`UseMockData = true` in `MainWindow.xaml.cs`) so the UI can be built and
tested without a live login.

### Working features

**Shell & window**
- Custom borderless window chrome (purple theme) with working minimize / maximize / close
- Maximize respects the taskbar work area (`WM_GETMINMAXINFO` fix in `UI/MaximizeBoundsFix.cs`)
- System tray icon; close (X) minimises to tray, tray "Exit" actually quits
- Window size/position persisted between launches (`Settings/AppSettings.cs`)
- App-wide unhandled-exception handler so one bad handler can't kill the app

**Mail list**
- Folders: Inbox / Sent / Drafts / Trash / Starred, with session-persistent state
  (star, read, delete, archive survive switching folders)
- Live unread badge on Inbox
- Search (sender + subject)
- Quick filter chips: All / Unread / Starred
- Sort: newest first / sender A–Z / subject A–Z
- Per-row star toggle, unread dot, attachment indicator, avatar initials
- Multi-select checkboxes with a bulk action bar (mark read / archive / delete / clear)

**Reading**
- Card-style reading pane with sender avatar, subject, date
- Auto mark-as-read on open; explicit "mark unread" action
- Reply / Reply-all / Forward (pre-fills To, Cc, subject, quoted body)
- Archive / Delete
- **Remote images blocked by default** (tracking-pixel protection, like Gmail/Outlook/Apple
  Mail) with a "Show images" bar
- Attachment chips; clicking one opens a Save dialog and writes the file

**Compose**
- Custom-chrome compose window matching the main window
- Collapsible Cc / Bcc fields
- Recipient validation before send
- Ctrl+Enter to send, Esc to close
- **Undo send** — a 5-second cancellable window before the message actually goes out
- Sent messages appear in Sent; closing with unsent content saves to Drafts;
  opening a draft resumes editing it

**Keyboard shortcuts (Gmail-style)**
- `c` compose · `r` reply · `a` reply-all · `f` forward · `e` archive · `#` delete
- `j` / `k` next / previous · `u` back to list · `/` search
- `g` then `i`/`s`/`d`/`t` — go to Inbox / Sent / Drafts / Trash
- `?` — shortcuts cheat sheet (also available via the toolbar "?" button)

---

## Verified

Tested end-to-end via UI Automation (`SelectionItemPattern` / `InvokePattern` /
`ValuePattern`) plus screenshots:

- Message open → auto mark-read → reading pane renders
- Image-blocked message → "Show images" reveals the image
- Reply-all → compose opens pre-filled with To / Cc / quoted body
- Compose → Send → undo window elapses → "Message sent"
- `?` → shortcuts overlay renders correctly
- Maximize → respects screen work area at 4K / 150% DPI

### Bugs found and fixed along the way
- Reading pane crashed if a message was clicked before WebView2 finished initialising
- **Inspect (DevTools) button null-ref'd and killed the entire app** — this was the "clicking
  any option crashes it" report; one unhandled exception took the whole process down
- Star toggle also selected/opened the row underneath it (WPF fires selection on mouse-down)
- Refresh in sample mode silently reverted star/delete/archive changes
- Login detection only reacted to full page navigations, missing an AJAX login — now polls
- Folder switching reset per-message state (star/read) — now session-persistent

---

## Tier 0 — automation layer (added 2026-08-29)

Everything Tier 0 needs is now **written and compiling**; what remains is live verification
against the authenticated site, which needs a real login.

**`Automation/DomBridge.cs`** — rewritten from a scrape-and-click stub into the full
operation surface, with a deliberate **two-tier strategy**: every action first dispatches a
real click on the actual Roundcube control (the middleman behaviour `PLAN.md` specifies), and
only falls back to `rcmail.command(...)` — Roundcube's own client-side dispatcher, the exact
function its buttons call — when that control isn't present in this skin.

- `ListFoldersAsync` / `SelectFolderAsync` / `GetSpecialMailboxesAsync` — real folder
  navigation. Special-folder names come from Roundcube's own `env` (`sent_mailbox`,
  `drafts_mailbox`, …) rather than being guessed, since servers disagree
  ("Sent" vs "INBOX.Sent" vs "Sent Items").
- `ListMessagesAsync` — returns a `MessagePage` (rows + page/pageCount/total/mailbox), so the
  UI knows where it is in a folder instead of just seeing whatever rows are rendered.
- `NextPageAsync` / `PreviousPageAsync` — pagination via Roundcube's own pager.
- `SetReadAsync` / `SetFlaggedAsync` — click the row's real status/flag icon, verify the row
  class actually changed, fall back to the mark command if it didn't.
- `DeleteAsync` / `ArchiveAsync` / `MoveToFolderAsync` — archive degrades to a plain move into
  the Archive mailbox when the archive plugin isn't installed.
- `ClickSaveDraftAsync` — drafts now reach the real Drafts folder.
- `OpenMessageAsync` — waits for the preview frame to actually be showing *that* uid instead
  of a fixed 500 ms guess, and finds attachments in both the frame and the top document.
- **Session-expiry recovery**: every injected script starts with a login-form guard. On a hit,
  the bridge asks `AutomationHost` to re-show itself for a real re-login, then **retries the
  same script** — so an expiry mid-action resumes instead of losing the user's click.
  Dismissing the login window resolves it cleanly as `SessionExpiredException`.
- Waiting is done by polling from C#, because WebView2 won't unwrap a Promise returned from
  an injected script.

**`Automation/AutomationHost.cs`** — `RecoverSessionAsync` (shared by concurrent callers),
a `SessionExpired` event, and real attachment downloads via `CoreWebView2.DownloadStarting`.
The download is issued from a **throwaway hidden iframe** inside the live page rather than by
navigating it: an attachment URL that unexpectedly rendered inline would otherwise navigate the
automation page off the mailbox and break the session the whole app depends on.

**`MainWindow`** — every action now routes through the bridge in live mode instead of being
local-only: folder switching, pagination (new pager bar), star, mark read/unread, delete,
archive, move-to-folder (new menu, scoped to checked rows or the open message), bulk actions,
and attachment saving. The sidebar gained a live section listing the account's **real** IMAP
folders (indented by depth, with unread badges) below the fixed five. "Starred" has no
Roundcube equivalent, so in live mode it means "flagged messages in Inbox" rather than a fake
folder. Failures say *which* selector to check with Inspect (DevTools) instead of failing silently.

### Also fixed
- **Reading pane was unreadable on a dark-mode PC** — WebView2 followed the OS theme and
  painted the message background black behind dark message text. The wrapper now pins an
  explicit light palette and `color-scheme`, plus width rules so wide tables/images don't force
  sideways scrolling.
- **Icon-only buttons had no accessible name** (the "cheap, pays off twice" gap called out in
  `ROADMAP.md`) — 20 of them now carry `AutomationProperties.Name`, which helps screen readers
  and made the new move-to-folder flow testable via UI Automation.

### Verified this pass (sample data, via UI Automation + screenshots)
- Builds clean: 0 warnings, 0 errors.
- Launches, renders, and stays stable unattended.
- Message open → reading pane renders **on a white background with readable text**.
- Remote-image blocking still fires ("Images are hidden to protect your privacy" + Show images).
- Folder switching via `g`+`s` → Sent loads, reading pane resets.
- Move-to-folder: menu lists the right targets (current folder excluded), message leaves the
  list, lands in the target, status confirms, reading pane resets.

---

## Sample-data polish pass (2026-08-29)

The dummy data and the options acting on it were inconsistent with each other. Fixed:

**Reply / reply-all / forward actually carry the original.** They used to insert a bare
`---- Original message ----` line with nothing under it. Now:
- Reply quotes the original beneath an attribution line (`On <date>, <sender> wrote:`) with
  `> ` prefixes, and opens the caret **above** the quote where the reply gets written.
- Reply-all builds Cc from the original To+Cc, dropping the user's own address *and* the
  original sender (who has been promoted to To) — previously it copied you on your own mail.
- Forward carries a real header block (From / Date / Subject / To / Cc / attachment names)
  followed by the original text.
- Subjects no longer stack up: replying to "Re: x" stays "Re: x", not "Re: Re: x".

**`Automation/MailText.cs` (new)** — one HTML→plain-text implementation shared by list
snippets, reply/forward quoting and the draft round-trip, so a row can't preview text that
differs from what the quote produces. Handles block structure, lists, entities and
whitespace collapsing.

**`MockData.cs` rewritten** around a single `MockMessage` record that yields *both* the list
row and the reading-pane body, so snippets, dates and attachment markers can't drift from the
message they describe. Also:
- Dates are **relative to now** ("8:42 AM", "Yesterday", "Wed") instead of hardcoded strings
  that would age into nonsense.
- 17 messages across Inbox / Sent / Drafts / Archive / Trash, with To and Cc populated so
  reply-all has something real to do, threaded subjects, multiple attachments, a long message,
  a remote-image message, and a no-subject draft.
- Sent and Drafts rows show the **recipient**, not "You" — showing your own name on every row
  of your own Sent folder tells you nothing.

**Options that now actually work on sample data**
- **Sort by date** sorts for real. `InboxRow` gained a nullable `Timestamp`; live rows leave it
  null and keep Roundcube's own ordering rather than being scrambled by an unparseable label.
- **Archive is no longer a black hole** — there's a real Archive folder in the sidebar (`g` `a`),
  and archiving files messages there instead of deleting them.
- **Search** matches the body snippet, not just sender and subject.
- **Seeded drafts open for editing** like locally saved ones did, instead of opening read-only.
- **Drafts keep their Bcc** across a save/reopen (`MessageDetail` gained a `Bcc`).
- **Starred** excludes Trash, so deleted mail doesn't resurface there.
- Avatar initials skip punctuation instead of rendering "(" for "(no recipient)".

**Attachments open inside the app** (`UI/AttachmentViewerWindow.xaml`). Clicking a chip used to
jump straight to a Save dialog; it now opens a viewer window matching the app's chrome, with
Save-a-copy and Open-externally as secondary actions.
- **PDFs** render in Edge's own viewer (paging, zoom, search, print) — free, and better than
  anything hand-rolled.
- **Images** are inlined as a data URI onto the app's light backdrop. Navigating straight to the
  file would use WebView2's built-in image view, which follows the OS theme and drops a black
  page into the middle of an otherwise light app. Files over 8 MB skip the inlining.
- **Text-like files** (txt, csv, md, json, xml, log, source) render as escaped monospace text.
- **HTML and SVG attachments are shown as source, never executed** — attachment content is
  untrusted, and rendering its markup to preview it is exactly what a malicious mail wants.
- Anything else gets an honest "no preview for X files" with Save still available.
- Files land in `%LOCALAPPDATA%\IITBWebmailWrapper\attachments`, not the user's Downloads folder
  — opening a mail shouldn't quietly litter it.
- Live attachments flow through the same viewer: `ResolveAttachmentAsync` downloads via the
  authenticated session first, then opens the identical window.

**`Automation/SampleAttachments.cs` (new)** generates *real* files behind the sample
attachments — a genuine single-page PDF (cross-reference offsets computed as the file is
written, since a wrong offset is what makes a hand-built PDF fail to open), a real PNG drawn
with System.Drawing, and text files with content matching the message that carries them. A
sample PDF that isn't a real PDF would only prove the viewer can show an error. Chip sizes are
taken from the generated file rather than a hardcoded string, so they can't disagree.

**Compose is now a real editor** (`UI/ComposeWindow`). The body was a plain `TextBox` with no
formatting whatsoever. It is now a WebView2 `contenteditable` surface — chosen over a WPF
`RichTextBox` because the message has to *leave* as HTML (that is what Roundcube's compose form
accepts and what the reading pane renders), so a contenteditable produces it directly instead of
needing a FlowDocument-to-HTML conversion layer. Spell-check, undo/redo and paste handling come
with it.

- **Formatting toolbar**: bold / italic / underline / strikethrough, bulleted and numbered lists,
  indent and outdent, block quote, alignment, font size, text colour, insert link, clear
  formatting. Ctrl+B/I/U work natively.
- **Rich quoting**: replies and forwards now put the original in a real `<blockquote>` that keeps
  its own bold, lists and structure, rather than flattening it to `> ` lines. Images are stripped
  from quotes — a remote image would phone home the moment the compose window opened.
- **Link insertion is scheme-checked**: only http, https and mailto. A `javascript:` href typed in
  here would otherwise be handed straight to the recipient.
- **Attachments**: Attach-files button *and* drag-and-drop onto the window, chips with real sizes,
  per-chip remove. `AllowExternalDrop="False"` on the editor so dropped files become attachments
  instead of navigating the editor.
- **Plain-text toggle** — switching down converts the HTML to text and hides the toolbar;
  switching back re-wraps it.
- **Explicit Save draft and Discard.** Discard confirms, then genuinely discards; previously any
  close with content silently became a draft with no way to refuse.
- Falls back to a plain `TextBox` if WebView2 fails to start, rather than leaving a compose window
  that can't be typed into.

`ComposeResult` now carries `BodyHtml` and the staged attachments alongside the plain-text body.
Sample sends stage their files into the attachment cache so a "sent" message can still open its
own attachments afterwards. `DomBridge.FillComposeAsync` takes both bodies and only uses the HTML
where TinyMCE is active — dropping markup into a plain-text textarea would send the tags as
literal text. `DomBridge.AttachFileAsync` uploads through Roundcube's own file input via a
`DataTransfer` (the one way script can populate a file input in Chromium) — **not yet live-verified.**

### Verified (UI Automation, reading the actual compose fields)
- Reply-all on a Cc'd message → To = sender, Cc = original Cc with self removed, subject not
  re-prefixed, body carries the full quoted original.
- Forward → complete header block including both attachment names, body intact.
- Reply → quoted bullet list renders one line per item.
- Inbox shows 8 messages with live relative dates and derived snippets; Archive folder present.
- Attachment viewer opens a PDF (Edge viewer, correct content), a PNG (on the app backdrop) and
  a .txt (monospace) — each showing the real file size.
- Reply-all → compose opens with the quoted original as a formatted blockquote; Send → the copy
  in Sent still carries the blockquote, bold names and bullet list, proving the HTML round-trips
  editor → result → message store → reading pane.

---

## Not done yet

**Tier 0's one remaining item — live selector verification.** The selectors target Roundcube's
real, documented markup conventions (`tr[id^=rcmrow]`, `a[rel="MBOX"]`, `#button-compose`,
`#messagecontframe`) but have **not been checked against the live authenticated site**. Until
that's done, nothing has been proven to touch real mail. Use the toolbar's **Inspect webmail
(DevTools)** button after signing in, then correct the selector constants at the top of
`Automation/DomBridge.cs` — they are all grouped there for exactly this.

Note `UseMockData` in `MainWindow.xaml.cs` is still `true`; flip it to `false` for the live pass.

Also still open: conversation threading, compose attachments, rich-text compose, contacts
autocomplete, snooze / scheduled send, signatures, dark mode, settings window, installer, and
app naming. Full list in `ROADMAP.md`.

---

## Next step

The live-login pass. Set `UseMockData = false`, sign in once, and walk the bridge operations
with DevTools open — that converts every Tier 0 item above from "written" to "working".
