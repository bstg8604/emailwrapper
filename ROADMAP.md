# Peacock — Full Feature Roadmap

## Context
The app (currently named "Peacock", to be renamed later) is a custom WPF frontend that puppets the real webmail.iitb.ac.in (Roundcube) session in a hidden WebView2, rather than reimplementing an email client — see the existing `PLAN.md` for that architecture. So far it has: a 3-pane shell, mock-data-driven inbox/sent/drafts/trash/starred, search, quick filters, compose with reply/forward prefill, star/archive/delete, tray + custom window chrome, and a `DomBridge` with Roundcube-aware (but not yet live-verified) selectors.

The user now wants "every feature/option" — explicitly benchmarking against Gmail, Apple Mail, Outlook, and Betterbird/Thunderbird. This plan inventories that full feature set, grouped by priority, so it can be worked through systematically rather than as an unbounded, undefined blob of "more features." Nothing here is committed to blindly — the **Known open risk** from `PLAN.md` still applies: DomBridge automation against the real Roundcube instance needs live verification before any backend-touching feature (send, delete, mark-read, etc.) can be trusted end-to-end.

> **Status note (2026-08-28):** items marked ✅ below are built and verified against sample
> data — see `PROGRESS.md` for the current state, what was tested, and bugs fixed.
> Everything backend-facing still depends on Tier 0.

## Priority tiers

### Tier 0 — Make the automation layer real (blocks everything backend-facing)
Currently `UseMockData = true` in `MainWindow.xaml.cs` short-circuits almost everything. Nothing in Tiers 1–3 that touches real mail is trustworthy until this is done:
1. Live-verify `DomBridge.cs` selectors against the authenticated site via the existing "Inspect webmail (DevTools)" button — rows, compose/send, preview iframe.
2. Handle session expiry mid-use (re-show `AutomationHost` for re-login, resume the pending action after).
3. Extend `DomBridge` beyond Inbox: folder switching needs to actually navigate Roundcube to Sent/Drafts/Trash/other folders and re-scrape, not just swap mock arrays (`MainWindow.LoadFolder`).
4. Pagination — Roundcube paginates the message list; `ListInboxAsync` currently only reads what's rendered on the current page.
5. Mark read/unread, delete, archive, move-to-folder as real `DomBridge` actions (click the real Roundcube controls), replacing `MainWindow.RemoveMessage`'s local-only removal.
6. Attachments: list attachments on a scraped message, trigger a real download through Roundcube's own download link/button (WebView2 download events).

### Tier 1 — Core mail-client parity (Gmail / Outlook / Apple Mail / Betterbird baseline)
- ✅ **Reply-all** — pre-fills To from sender, Cc from the original To+Cc.
- ✅ **CC/BCC fields** in `ComposeWindow.xaml`, collapsed behind a "Cc/Bcc" toggle like Gmail.
- ✅ **Multi-select in message list** — row checkboxes plus a bulk action bar (mark read / archive / delete / clear).
- ✅ **Mark unread/read toggle** — auto-read on open, explicit "mark unread" action, live Inbox unread badge.
- ✅ **Sort options** — date / sender / subject via the toolbar dropdown.
- ✅ **Image/external-content blocking** — remote `<img>` srcs are swapped for a transparent pixel with a "Show images" bar (`SanitizeRemoteImages` in `MainWindow.xaml.cs`).
- ✅ **Attachments (viewing)** — attachment chips in the reading pane; clicking opens a Save dialog. Sample data writes a placeholder file; the live path still needs Tier 0.
- ✅ **Drafts (local)** — closing compose with unsent content saves to Drafts; reopening a draft resumes editing. Real Roundcube draft-save still pending Tier 0.
- **Conversation/thread view** — group messages by subject/thread like Gmail, with expand/collapse.
- **Attachments in Compose** — add via file picker, show as chips, remove before sending; requires driving Roundcube's real attachment upload input.
- **Rich text formatting toolbar** in Compose (bold/italic/underline/lists/links) — Roundcube's HTML compose already has this; needs DomBridge to toggle it on and forward toolbar clicks, or a parallel WPF RichTextBox that gets serialized to HTML before send.
- **Remaining message actions**: flag/important marker, move to folder (drag-and-drop and a "Move to…" menu), print.
- **View options** not yet done: toggle conversation view on/off; reading-pane position (right vs. bottom vs. off, like Outlook); sort by size.
- **External link warning** — confirm before opening links from message bodies in the default browser.

### Tier 2 — Productivity features (what makes Gmail/Superhuman/Outlook feel fast)
- ✅ **Keyboard shortcuts**, Gmail-style: `c` compose, `r`/`a`/`f` reply/reply-all/forward, `e` archive, `#` delete, `j`/`k` next/prev, `u` back to list, `/` focus search, `g` then `i`/`s`/`d`/`t` to switch folder, `?` cheat-sheet overlay (also reachable from the toolbar "?" button). Shortcuts are suppressed while a text field has focus.
- ✅ **Undo send** — Send hands the message to `MainWindow`, which shows a 5-second snackbar with UNDO before actually dispatching; undo reopens the message in compose. Sent mail lands in the Sent folder.
- **Snooze** — hide a message from Inbox until a later time (Gmail); purely a local UI/state feature since Roundcube has no native snooze — would need local scheduling + re-surfacing.
- **Scheduled send** — pick a future send time; same local-scheduling approach as snooze.
- **Signature** — configurable per-account signature auto-inserted into new/reply/forward compose bodies.
- **Contacts/address book** — autocomplete recipients in To/Cc/Bcc from Roundcube's real address book (scrape/search via DomBridge) or a local contacts cache built from message history.
- **Saved/advanced search** — filters by from/to/subject/date range/has-attachment/folder, with the ability to save a search as a quick filter (extending the existing `FilterMessage`/quick-filter-chip mechanism in `MainWindow.xaml.cs`).
- **Filters/rules** — "when mail matches X, do Y" (Outlook rules / Gmail filters) — would need to either drive Roundcube's own filter settings UI or reimplement rule matching locally on top of scraped mail.
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
- **Local message cache for offline reading** — persist scraped messages (e.g. SQLite or a simple JSON store) so recently read mail is viewable without a live Roundcube session.
- **Settings window** — a real preferences UI (currently there is none) covering: notifications on/off, sound, signature editor, theme, density, reading-pane position, keyboard-shortcut reference, start-with-Windows toggle, about/version.
- **Start with Windows** toggle (registry Run key or a Startup shortcut).
- **Accessibility** — screen-reader labels on icon-only buttons (compose/star/reply/etc. currently rely on glyphs + tooltip only), high-contrast theme support, full keyboard navigation of the 3-pane layout.

### Tier 4 — Distribution
- **App icon/branding** — replace the placeholder `icon.ico` (currently extracted from `imageres.dll`) once a name/identity is chosen.
- **Installer** — MSIX or an Inno Setup/WiX installer instead of the current `dotnet publish` single-exe, so Start Menu entry, uninstall, and auto-start registration work properly.
- **Auto-update** — check-for-updates against a release feed (e.g. GitHub Releases, matching the existing `bstg8604/emailwrapper` repo).
- **App naming** — still deferred per earlier conversation; needs to happen before icon/installer/branding work.

## Suggested working order

**Done so far:** the first Tier 1 slice (reply-all, Cc/Bcc, multi-select, mark read/unread,
sort, image blocking, attachments, local drafts) and the Tier 2 quick wins (keyboard
shortcuts, undo send). See `PROGRESS.md`.

**Remaining, in recommended order:**
1. **Tier 0** — needs a live login session; can't be completed without the user. This is now
   the main blocker: every feature above works against sample data only.
2. Rest of Tier 1 — conversation view, compose attachments, rich text, move-to-folder, flags.
3. Rest of Tier 2 — snooze, scheduled send, signatures, contacts autocomplete, advanced
   search, rich toast notifications, taskbar unread badge.
4. Tier 3 — settings window + dark mode first (gives every preference a home), then density,
   multiple compose windows, offline cache, accessibility.
5. Tier 4 — naming/branding, then icon, installer, auto-update.

**Known gap worth fixing early in Tier 3:** icon-only buttons currently expose no accessible
name (they rely on glyph + tooltip), which hurts screen readers *and* makes UI automation
testing awkward — adding `AutomationProperties.Name` is cheap and pays off twice.

## Verification approach
Since most of Tier 1+ is currently exercised through `UseMockData`, each feature should be built and demoed against `MockData.cs` first (fast iteration, no login needed), the same way Compose/reply/star/delete were validated in the current build. Backend-touching pieces (Tier 0, and any Tier 1+ feature once wired to real `DomBridge` calls) need a live-login pass per the "Known open risk" section of `PLAN.md` before being trusted.
