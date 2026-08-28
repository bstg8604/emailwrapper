# Progress Report

**Project:** Peacock (working name) — custom desktop frontend for webmail.iitb.ac.in
**Last updated:** 2026-08-28
**Repo:** https://github.com/bstg8604/emailwrapper

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

## Not done yet

**Blocking everything backend-facing (Tier 0 in `ROADMAP.md`):**
The `DomBridge` selectors target Roundcube's real, documented markup conventions
(`tr[id^=rcmrow]`, `#button-compose`, `#messagecontframe`) but have **not been verified
against the live authenticated site**. Until that's done, nothing actually touches real mail.
This needs a live login — use the toolbar's **Inspect webmail (DevTools)** button to inspect
the real DOM, then the selector constants at the top of `Automation/DomBridge.cs` can be
corrected.

Also still open: real folder navigation, pagination, real attachments download, conversation
threading, rich-text compose, contacts autocomplete, snooze / scheduled send, signatures,
dark mode, settings window, installer, and app naming. Full list in `ROADMAP.md`.

---

## Next step

Finish **Tier 0** (needs a live login session), or continue with more Tier 1/2 UI work on
sample data — both paths are laid out in `ROADMAP.md`.
