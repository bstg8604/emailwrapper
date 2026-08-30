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
      ImapMailBackend.cs             # IMAP (MailKit) for everything read-side + move/flag/delete;
                                      # SMTP (MailKit) for sending
      ContactsIndex.cs               # recipient autocomplete from mail history
      LoginWindow.xaml(.cs)          # sign-in dialog
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

## Known open items
- **SMTP submission host/port/security are best-guess defaults** (`smtp-auth.iitb.ac.in:587`,
  STARTTLS) pending confirmation against the user's known-working Thunderbird/Betterbird SMTP
  settings. Editable in the sign-in dialog's "Server settings" section without a rebuild.
- **Live refresh is polling (60s), not IMAP IDLE.** Simpler for a first pass; an IDLE-based
  connection would push new-mail notifications instantly instead.
- **No LDAP directory lookup.** See the trade-off note above — `ContactsIndex` covers people the
  user has actually corresponded with; add an LDAP settings slot later if institute-wide lookup
  becomes reachable.
