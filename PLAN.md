# IITB Webmail — Custom IMAP/SMTP Client

## Context
The app is a custom, clean, minimalistic desktop email client for the user's IITB mailbox.

**This supersedes the original plan.** The app began as a literal middleman puppeting the real
`webmail.iitb.ac.in` (Roundcube) page inside a hidden WebView2 — see git history for that
version if it's ever needed for reference. It was rebuilt to talk directly to IITB's mail servers
over IMAP and SMTP once it was confirmed the account works from arbitrary networks in Thunderbird,
Betterbird and Gmail's "Send mail as", and that `imap.iitb.ac.in:993` answers with a real IMAP
banner from outside the campus network. That made the webmail-automation approach the wrong
choice: it required a permanently-running Chromium instance just to puppet Roundcube's own UI,
depended on scraping fragile DOM selectors that were never verified end-to-end, and re-broke on
every Roundcube update — all to reach a server that was reachable directly the whole time.

**One real trade-off from the switch, and it's already priced in.** IITB's institute-wide
"suggest anyone at IITB" autocomplete in Roundcube compose comes from an internal LDAP directory,
reachable only from inside the campus network — Roundcube can query it because it runs there;
this client, running on the user's own machine, cannot. What it has instead: recipient
autocomplete built from the user's own Sent/Inbox history (see `Mail/ContactsIndex.cs`), which
the user confirmed is an acceptable substitute. An LDAP directory setting could be added later if
institute-wide lookup becomes reachable (on campus, or over VPN).

## Architecture
One process:

1. **`Mail/ImapMailBackend.cs`** — owns one long-lived `MailKit.Net.Imap.ImapClient` connection
   for the app's lifetime (reading, flags, move/delete/archive, drafts) and opens a short-lived
   `MailKit.Net.Smtp.SmtpClient` connection per send. Method names deliberately mirror the old
   wrapper-era `DomBridge`'s surface (`ListFoldersAsync`, `SetFlaggedAsync`, `OpenMessageAsync`,
   ...) so the rest of the app changed as little as possible in the migration — only what
   answers those calls did.
2. **`Mail/AccountSettings.cs`** — IMAP/SMTP host, port and credentials, persisted encrypted via
   Windows DPAPI (`CurrentUser` scope) under `%LOCALAPPDATA%\IITBWebmailWrapper\account.dat`.
   Never plaintext, never in the repo.
3. **`Mail/LoginWindow.xaml`** — collects credentials, connects before closing, hands the caller
   back a live, authenticated backend or nothing.
4. **`Mail/ContactsIndex.cs`** — recipient autocomplete built by scanning Sent/Inbox envelope
   headers after connecting, ranked by correspondence frequency.
5. **Custom UI (WPF, unchanged in spirit)** — `MainWindow`: folder list, message list, reading
   pane, compose. `UseMockData` is now `_mail is null`: the app always starts on sample data
   (`Automation/MockData.cs`) and switches to the live backend once signed in, so UI work never
   needs a live login.

## Why WPF (not WinForms)
WPF gives real control over styling (custom fonts, spacing, flat panels, accent colors) with no
extra toolchain beyond the .NET 9 SDK — the better fit for a UI meant to look deliberately
minimal, versus WinForms' dated control set. (`UseWindowsForms` stays enabled in the `.csproj`
only for `NotifyIcon`/tray support, which WPF has no native equivalent for.)

## Project layout
```
Email Client/
  EmailClient.sln
  src/EmailClient/
    App.xaml / App.xaml.cs          # entry point
    Mail/
      AccountSettings.cs             # credentials + server settings, DPAPI-encrypted at rest
      ImapMailBackend.cs             # IMAP (MailKit, incl. a dedicated IDLE connection) for
                                      # everything read-side + move/flag/delete/folder CRUD;
                                      # SMTP (MailKit) for sending
      ContactsIndex.cs               # recipient autocomplete from mail history
      AccountWindow.xaml(.cs)        # sign-in + sidebar settings page (Profile/Signature/
                                      # Contacts/Folders/Settings) — renamed from LoginWindow
    Automation/
      MailModels.cs                  # InboxRow / MessageDetail / MailFolder / MessagePage —
                                      # shared shapes used by both the mock and live backends
      MailText.cs                    # HTML<->plain-text, snippets, quoting, date formatting
      MockData.cs / SampleAttachments.cs   # sample data + real generated sample files
    UI/
      MainWindow.xaml(.cs)            # 3-pane shell: folder sidebar / message list / reading pane
      ComposeWindow.xaml(.cs)         # rich-text (WebView2 contenteditable) compose
      AttachmentViewerWindow.xaml(.cs)  # in-app attachment preview (PDF/image/text)
      TrayIcon.cs                     # NotifyIcon: minimize-to-tray, quit
    Settings/
      AppSettings.cs                  # window bounds, persisted to %LOCALAPPDATA%
  icon.ico
```

## Verification
- `dotnet build` compiles cleanly.
- Live run: `dotnet run`, click "Sign in", enter IITB credentials. On success the sidebar shows
  the account's real folders and Inbox loads.
- Open a message, reply/reply-all/forward, send — confirm it lands in Sent and in the recipient's
  inbox.
- Star/mark-read/delete/archive/move — confirm the change is visible in Thunderbird/Betterbird
  afterward (proves it landed on the real IMAP mailbox, not just local state).
- Save a draft, reopen it, confirm formatting survived.
- Attach a file, send, confirm the recipient receives it.
- Restart the app — confirm it reconnects without re-prompting for credentials.

## Known open items (updated 2026-08-31 — see `PROGRESS.md` for full detail)
- **SMTP host/port confirmed working** against the live account — no longer a guess.
- **Live refresh now uses IMAP IDLE** (a dedicated second connection), not polling.
- **No LDAP directory lookup.** See the trade-off note above — `ContactsIndex` (auto-learned) plus
  a manual contacts store now cover this; add an LDAP settings slot later if institute-wide lookup
  becomes reachable.
- Still open: dark mode, snooze/scheduled send, filters/rules, advanced search, multiple non-modal
  compose windows, offline message cache, installer/auto-update. Full list in `ROADMAP.md`.

## Planned architecture — remaining features

Approach notes for each item still open, so implementation can start directly from here instead
of re-deriving the design each time.

### Dark mode
The palette isn't fully centralized yet — `Theme.xaml` holds `ThemeAccent`/`ThemeAccentSoft`/
`ThemeAccentDeep`/`AvatarBrush`, but most surface colours (`#FFFAFAFA` window background, white
cards, `#FF3C3C3C` text) are hardcoded per-control across `MainWindow.xaml`, `ComposeWindow.xaml`,
`AccountWindow.xaml`. Plan:
1. Move every hardcoded surface/text/border colour into named `SolidColorBrush` resources in
   `Theme.xaml` (`SurfaceBrush`, `SurfaceAltBrush`, `TextPrimaryBrush`, `TextMutedBrush`,
   `BorderBrush`, etc.), replacing literal hex in the XAML files with `{StaticResource ...}`.
2. Add a second resource dictionary `Theme.Dark.xaml` with the same keys, dark values.
3. `Settings/AppSettings.cs` gains a `Theme` enum (`Light`/`Dark`/`System`), surfaced on the
   account page's Settings section. Swapping `Application.Current.Resources.MergedDictionaries`
   at the app-resource level applies it live, no restart needed.
4. The reading pane's HTML (`WrapHtml` in `MainWindow.xaml.cs`) needs its own dark palette in the
   `<style>` block — it currently pins an explicit light palette deliberately (fixed a real
   dark-mode-follows-OS bug earlier); that pin needs to become theme-aware instead of always-light.
5. `System` option reads `SystemParameters`/`UISettings` for the OS app-mode and re-applies on
   `SystemEvents.UserPreferenceChanged`.

### Snooze
No IMAP server-side concept — purely local UI/state, like the existing star/read state pattern.
1. `Settings/SnoozedMessages.cs` — local JSON store keyed by `(mailbox, UID)` → wake-up `DateTime`,
   same shape as `ManualContactsStore.cs`.
2. A snooze action (message toolbar + keyboard shortcut) opens a small popup (preset options:
   later today, tomorrow, next week, pick a date/time) matching Gmail's own menu.
3. Snoozed messages are filtered out of the normal Inbox view (extend `FilterMessage` in
   `MainWindow.xaml.cs`) and shown in a new "Snoozed" pseudo-folder in the sidebar, sorted by
   wake time not received time.
4. A background timer (reuse the existing `DispatcherTimer` pattern already used for autosave)
   checks every minute for anything past its wake time and moves it back to Inbox, surfacing a
   toast the same way new-mail-via-IDLE does today.

### Scheduled send
Same local-scheduling shape as snooze, applied to outgoing mail instead of incoming.
1. Compose gains a "Send later" split-button next to Send (Gmail's own convention) with the same
   preset/pick-a-time popup as snooze.
2. `Settings/ScheduledSends.cs` stores the composed `MimeMessage` (serialized to a temp `.eml` file
   under `%LOCALAPPDATA%`, not kept in memory) plus its send time.
3. The same background timer used for snooze also checks scheduled sends each tick; due ones are
   sent through the existing `ImapMailBackend` SMTP path and the temp file is deleted.
4. A "Scheduled" pseudo-folder (like Snoozed) lists pending sends with a Cancel/Edit action —
   editing pulls it back into Compose and removes the pending entry.

### Filters / rules
IMAP itself has no rule engine reachable here (ManageSieve isn't confirmed available on IITB's
server) — plan is client-side matching against fetched mail, evaluated at IDLE-triggered refresh
time, not server-side:
1. `Settings/MailRules.cs` — ordered list of `MailRule { Conditions, Actions }`; conditions match
   the existing `FilterMessage`-style fields (from/to/subject contains, has attachment); actions
   reuse existing single-message operations already implemented (`SetFlaggedAsync`, `ArchiveAsync`,
   `MoveToFolderAsync`, mark read).
2. Rules run once per newly-arrived message (hook into the same `OnMailboxActivity` callback IDLE
   already fires), not retroactively over the whole mailbox, to avoid a rules pass silently
   reorganizing old mail nobody asked to touch.
3. A rules editor page added to the account sidebar (next to Settings), reusing `AccountWindow`'s
   existing row-list-with-add/edit/delete pattern already built for Contacts and Folders.
4. If ManageSieve does turn out to be reachable later, rules could optionally push to the server
   instead (rules would then apply even when the app isn't running) — worth a quick capability
   probe before building the client-side engine, since it would change the design significantly.

### Advanced / saved search
Extends the existing quick-filter-chip mechanism rather than replacing it:
1. A "More search options" affordance next to the search box opens a small panel: from, to,
   subject, date range, has-attachment, folder scope — mirrors Gmail's own advanced search sheet.
2. Building the query reuses `ImapMailBackend.SearchAsync`'s existing combination of client-side
   envelope substring matching (subject/from/to) and server-side `SearchQuery` predicates
   (`BodyContains`, and new ones for date range/attachment presence via `SearchQuery.And`/`Or`).
3. "Save this search" persists the built query (`Settings/SavedSearches.cs`) and adds it as a new
   quick-filter chip alongside All/Unread/Starred, so a saved search becomes a one-click filter
   from then on — no new UI surface needed beyond the existing chip row.

### Multiple non-modal compose windows
`ComposeWindow` currently opens via `ShowDialog` (modal, blocks `MainWindow`). Plan:
1. Switch `OpenCompose` in `MainWindow.xaml.cs` to `Show()` instead of `ShowDialog()`, tracking
   open windows in a `List<ComposeWindow>` on `MainWindow` so they aren't garbage-collected while
   open and so the app can enumerate them (e.g. to warn before quitting with unsent drafts).
2. Autosave/draft-replace logic already keyed per-window (`autosavedUid` is a local closure
   variable per `OpenCompose` call already) — this should carry over unchanged since each window
   already owns its own state.
3. `Owner = this` currently ties the compose window's taskbar presence to `MainWindow`; with
   non-modal windows this is still fine (owned windows still get their own taskbar-adjacent
   behavior in Windows), just no longer blocks input to the owner.
4. Minimizable "compose bar" at the bottom of `MainWindow` (Gmail's own convention for multiple
   drafts) is a nice-to-have on top of this, not required for the core non-modal behavior.

### Offline / local message cache
1. A local SQLite store (`Microsoft.Data.Sqlite`, no extra native deps needed on Windows) keyed by
   `(UIDVALIDITY, UID)` per mailbox — matches the plan's own recommendation, since UIDs are only
   stable per UIDVALIDITY epoch and a validity change means the whole per-folder cache must be
   dropped and rebuilt.
2. `BuildMessageDetail` in `ImapMailBackend.cs` already produces the exact shape to persist
   (`MessageDetail`) — persisting is a matter of writing that record after every successful
   `OpenMessageAsync`/`FindConversationSiblingsAsync` fetch, alongside the existing in-memory
   `_messageDetailCache` in `MainWindow.xaml.cs` (which stays as the fast path; SQLite is the
   fallback when there's no live connection).
3. On launch, if the IMAP connection fails, fall back to listing/opening from the local cache in a
   clearly-marked "offline" mode rather than failing outright — folders/messages not yet cached
   simply show as unavailable rather than the whole app being unusable.
4. Cache eviction: keep it simple — no eviction at all initially (mirrors the existing in-memory
   cache's own "never evicted, worst case a few hundred entries" reasoning), revisit only if it
   turns out to matter in practice.

### Installer / auto-update / branding
1. **Icon**: replace the placeholder `icon.ico` (extracted from `imageres.dll`) with a real
   Purplemail-branded icon now that the name is settled — matches the purple accent already used
   throughout the UI (`AvatarBrush`, `ThemeAccent`).
2. **Installer**: switch from a bare `dotnet publish` single-exe to a proper installer — Inno Setup
   is the lighter-weight option (a single `.iss` script, no separate packaging toolchain) versus
   MSIX (needs a signing certificate and app-identity registration); Inno Setup is the pragmatic
   default unless Store distribution is ever wanted. Handles Start Menu entry, uninstall entry,
   and optionally a Start-with-Windows registration.
3. **Auto-update**: check-for-updates against the GitHub Releases feed for `bstg8604/emailwrapper`
   (the repo already in use) — a simple version-compare against the latest release tag on launch,
   prompting to download rather than a fully silent auto-updater as a first pass.
4. Do icon/installer/branding only after the app's name is fully settled everywhere (it already
   reads "Purplemail" in the title bar; confirm the same name is used in the `.csproj`
   `AssemblyName`/`Product` metadata before cutting an installer around it).
