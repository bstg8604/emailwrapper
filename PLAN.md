# IITB Webmail — Custom Frontend, Automated Backend

## Context
The user wants a custom, clean, minimalistic UI they build, that acts as a **literal middleman** in front of the real `webmail.iitb.ac.in` (Roundcube). Not a browser-in-a-box showing Roundcube's own UI, and not a new email client talking IMAP/SMTP or an API. The real webmail page keeps running in the background (hidden), and every action in the custom UI **automates the real page**: clicking a mail in the custom UI clicks the actual row in the hidden webmail DOM and pulls back what it renders; clicking Send in the custom UI drives the real compose form and clicks the real Send button. The custom UI is a puppet controller; webmail.iitb.ac.in is the puppet doing the actual work. This is a greenfield project (empty directory); `.NET 9 SDK` is already installed, so no extra toolchain is needed.

## Architecture
One process, two layers:

1. **Hidden automation host (WebView2)** — loads the real `https://webmail.iitb.ac.in/` and keeps it loaded and running for the app's entire lifetime. Shown once for the user to complete real login (IITB SSO/CAS can't be faked). After login, the WebView2 window is hidden/moved off-screen but the page keeps running live in memory — it's the actual webmail session, just not visually shown.
2. **DOM automation bridge** — JS injected into that hidden page via `CoreWebView2.ExecuteScriptAsync`, exposing operations like `listInbox()`, `openMessage(id)`, `startCompose()`, `fillCompose(to, subject, body)`, `clickSend()`, `markRead(id)`, `delete(id)`. Each of these finds real DOM elements in the live webmail page and either scrapes their content or dispatches real click/input events on them — i.e. actually interacting with Roundcube's own UI programmatically, not calling an API.
3. **Custom UI (WPF)** — the only thing the user sees: folder list, message list, reading pane, compose window, styled cleanly and minimally. Every user action calls into the bridge; every bridge result re-renders into the custom UI.
4. **Live change detection** — a `MutationObserver` injected into the hidden page's message-list container fires whenever webmail's own UI updates (new mail arrives, a send completes, etc.), and posts that event back to the WPF host via `chrome.webview.postMessage`, which refreshes the custom UI and raises a native desktop toast for new mail. This mirrors the real webmail live instead of polling on a timer.

## Why WPF (not WinForms)
WPF gives real control over styling (custom fonts, spacing, flat panels, accent colors) with no extra toolchain beyond the already-installed .NET 9 SDK — the better fit for a UI meant to look deliberately minimal, versus WinForms' dated control set.

## Project layout
```
Email Client/
  EmailClient.sln
  src/
    EmailClient.csproj
    App.xaml / App.xaml.cs          # entry point
    Automation/
      AutomationHost.cs              # hidden WebView2: loads webmail.iitb.ac.in, shows itself only for login, hides after
      DomBridge.cs                   # C# wrappers calling ExecuteScriptAsync for each puppet action
      scripts/                       # the actual injected JS: list.js, openMessage.js, compose.js, send.js, observer.js
    UI/
      MainWindow.xaml                # 3-pane shell: folder sidebar / message list / reading pane
      ComposeWindow.xaml
      TrayIcon.cs                     # NotifyIcon: minimize-to-tray, quit
    Settings/
      AppSettings.cs                  # window bounds, persisted to %LOCALAPPDATA%
  icon.ico
```

## Implementation phases

### Phase A — Automation host
- `AutomationHost` owns a `CoreWebView2` with an explicit persistent User Data Folder (`%LOCALAPPDATA%\IITBWebmailWrapper\WebView2`) so the login session survives restarts.
- On first run (or whenever the session has expired), **show** this WebView2 in a plain window so the user completes the real IITB login/SSO flow. Once the URL indicates a logged-in inbox, hide/move the window off-screen — the page itself is *not* torn down, it keeps running as a live, real webmail session in the background.
- If a later automation call detects the page has been bounced back to a login screen (session expired), re-show the window for re-login.

### Phase B — DOM automation bridge
This is the core of the "middleman" behavior, and the part with real unknowns until tested live against `webmail.iitb.ac.in`:
- `listInbox()`: injected JS queries the message-list container in the live page, scrapes each row's sender/subject/snippet/date/unread state/internal row id, returns as JSON to C#.
- `openMessage(id)`: dispatches a real click event on the corresponding row element (so Roundcube's own UI actually opens it, exactly as if the user clicked it), waits for the reading pane to render, then scrapes subject/from/date/body HTML back out.
- `startCompose()` / `fillCompose(to, subject, body)` / `clickSend()`: clicks the real Compose button, waits for the compose form to appear, sets the to/subject/body fields (setting `.value` and dispatching `input`/`change` events so Roundcube's own JS framework picks up the change, since a raw property assignment alone often won't trigger framework-bound state), then dispatches a real click on the actual Send button.
- `markRead(id)` / `delete(id)`: same pattern — locate the real UI control (context-menu item or toolbar button) and click it for real, rather than calling any backend endpoint directly.
- **Confirmed: webmail.iitb.ac.in runs Roundcube.** This is a much more knowable target than a generic guess — Roundcube's DOM has used stable conventions for years (message rows as `tr[id^="rcmrow"]`, toolbar buttons `#button-compose`/`#button-send`, the preview iframe `#messagecontframe`), which `DomBridge.cs` now targets directly instead of blind heuristics. Skin customizations or version differences can still shift exact class names inside a row/preview, so the selectors are still worth confirming live via the app's "Inspect webmail (DevTools)" button before relying on them.
- `observer.js`: a `MutationObserver` on the message-list container, posting `{type: "newMail", ...}` back via `chrome.webview.postMessage` whenever Roundcube's own UI inserts a new row — this is what drives live refresh and notifications, sourced directly from the real UI changing rather than a polling timer.

### Phase C — Custom minimal UI (WPF)
- `MainWindow`: three-pane layout (folders / message list / reading pane) populated purely from `DomBridge` scrape results.
- `ComposeWindow`: to/cc/subject/body fields + Send button, calling `DomBridge.FillCompose` + `DomBridge.ClickSend` on submit.
- Minimalistic visual language: flat panels, generous whitespace, one accent color, system font — deliberately not mimicking Roundcube's own UI.
- Reading pane renders message HTML in a small scoped WebView2 (sandboxed to just that pane) so email HTML/images display safely without exposing the rest of the app to that content.

### Phase D — Packaging & previously-agreed features
- Custom icon (`icon.ico`) + window title.
- System tray via `NotifyIcon`: minimize-to-tray on close (X), true quit only via tray "Exit".
- Window size/position persisted to `%LOCALAPPDATA%\IITBWebmailWrapper\settings.json`, restored on launch.
- Desktop notifications: native Windows toast triggered directly by the `observer.js` MutationObserver event (Phase B), not a separate polling loop — reflects the real webmail UI changing live.
- Publish as a single self-contained exe: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.

## Verification
- `dotnet build` compiles cleanly.
- Live run: complete real IITB login once in the shown automation-host window; confirm it hides itself and `listInbox()` populates the custom UI's message list from the live page.
- Click a message in the custom UI; confirm the hidden page actually navigates to that message (observable by briefly un-hiding the automation window during testing) and the reading pane shows the real scraped content.
- Compose and send a real test message through the custom `ComposeWindow`; confirm it actually sends by checking Sent folder / recipient inbox in a real browser afterward.
- Mark-read/delete from the custom UI; confirm the change reflects in webmail.iitb.ac.in when checked in a real browser (proves the click really landed on Roundcube's own controls, not just local state).
- Restart the app without clearing the WebView2 profile — confirm it skips the login screen.
- Resize/move, close via X (tray-minimizes, doesn't quit), reopen from tray, quit via tray "Exit", relaunch — confirm window bounds restored.
- With the app running, receive a genuinely new email — confirm the `MutationObserver` fires and a native toast appears without any manual refresh.
- `dotnet publish` per the command above; run the resulting single exe outside the dev environment.

## Known open risk
Exact DOM structure/selectors of the live Roundcube client can only be determined by inspecting the real, authenticated `webmail.iitb.ac.in` page — not guessable in advance. Phase B's first implementation pass will need one or more live-testing iterations (with DevTools open against the real site) to nail down working selectors/event-dispatch details, and this automation approach is inherently more fragile than an API-based integration: if IITB updates Roundcube's web UI, the injected JS selectors may need updating.
