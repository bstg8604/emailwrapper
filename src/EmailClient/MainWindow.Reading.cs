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
/// The reading pane: opening a message, building the conversation cards, and the sanitising
/// that has to happen before any of that untrusted HTML reaches the WebView2.
/// </summary>

public partial class MainWindow
{
    // ---- Reading ------------------------------------------------------------------------

    /// <summary>Shared rather than a fresh HttpClient per image click (that also leaks a socket
    /// per click under load), and explicitly timed out — same reasoning as ImapMailBackend's own
    /// NetworkTimeoutMs: the default HttpClient timeout is 100 seconds, long enough that a stalled
    /// host (a dead link, a server that accepts the connection but never answers) reads as the
    /// click having done nothing at all rather than a fetch still in progress.</summary>
    private static readonly HttpClient InlineImageHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Wraps message HTML for the reading pane. The explicit light palette and color-scheme are
    /// load-bearing: without them WebView2 follows the OS dark theme and renders the default page
    /// background black, leaving dark message text unreadable on a dark-mode PC.
    /// </summary>
    private static string WrapHtml(string bodyHtml) => $$"""
        <html><head><meta name="color-scheme" content="light"><style>
        :root { color-scheme: light; }
        html, body { background: #ffffff; color: #1f1f1f; }
        /* A trackpad left/right swipe otherwise pans the whole rendered page sideways (Edge's own
           elastic overscroll gesture) before springing back — jarring and pointless here, since a
           message body never has anything to reveal off to the side. Killing horizontal overscroll
           and overflow removes the gesture instead of just tolerating a page wide enough to need it. */
        html { overflow-x: hidden; overscroll-behavior-x: none; }
        body { font-family: 'Segoe UI', system-ui, sans-serif; font-size: 14px; line-height: 1.55; margin: 0;
          overflow-x: hidden; overscroll-behavior-x: none; }
        /* Chromium's "scroll anchoring" tries to keep whatever's at the top of the viewport from
           visually jumping when layout above it changes size — useful for a page that's still
           loading content, but this page never changes size after it's rendered once. With several
           bordered/shadowed .qcard blocks stacked back to back, the anchor can land right on a card
           boundary and make the touchpad feel like it "resists" there before releasing — this turns
           the heuristic off everywhere instead of fighting it card by card. */
        * { overflow-anchor: none; }
        img, video { max-width: 100%; height: auto; }
        /* SanitizeRemoteImages marks a blocked image's transparent-pixel replacement with this
           attribute — !important is what actually wins over the original tag's own width/height
           attributes or inline style (both common on a real marketing/newsletter image), which a
           plain style="" swap on the tag itself couldn't safely override (the tag may already have
           its own style attribute, and a tag can't have two). Collapsed to nothing rather than left
           at its original declared size, matching how real webmail hides a blocked image entirely
           instead of leaving a same-sized blank gap where it would have been. */
        img[data-blocked] { display: none !important; width: 0 !important; height: 0 !important;
          margin: 0 !important; padding: 0 !important; border: 0 !important; }
        /* Apple Mail's own export marks certain line breaks with this class purely as an internal
           bookkeeping hint for its own app to collapse back down when re-editing a quoted thread —
           Mail.app's own stylesheet hides them, but a generic renderer (this one) rendered them as
           real, visible <br> line breaks. In a thread with several nested Apple Mail replies these
           stack up literally, showing as a growing block of empty vertical space right before the
           quoted content — a live report traced this exact gap to these markers. */
        br.Apple-interchange-newline { display: none; }
        /* Not forcing visible borders/padding onto every table the way the compose editor does for
           pasted spreadsheet data — a huge share of real-world HTML mail (newsletters, marketing)
           uses borderless tables purely as a legacy layout grid, and gridlines on those would make
           an intentionally clean email look broken. border-collapse is safe regardless: it only
           changes how a table *that already has borders* joins them (single shared lines instead of
           doubled ones with gaps), so it can't add a visual border where the sender didn't put one. */
        table { max-width: 100%; border-collapse: collapse; }
        pre { white-space: pre-wrap; word-wrap: break-word; }
        a { color: #6d28d9; }
        .qcard { margin: 12px 0 0; border: 1px solid #ebebef; border-radius: 12px; background: #ffffff;
          box-shadow: 0 1px 4px rgba(20,20,30,0.04); overflow: hidden; }
        .qcard:first-child { margin-top: 0; }
        .qcard .qhead { display: flex; align-items: flex-start; gap: 10px; padding: 11px 14px;
          background: #fafafa; border-bottom: 1px solid #f0f0f0; }
        .qcard .qavatar { flex: none; width: 32px; height: 32px; border-radius: 50%;
          background: linear-gradient(135deg, #8b5cf6, #6d28d9); color: #fff; font-size: 12.5px;
          font-weight: 600; display: flex; align-items: center; justify-content: center; }
        .qcard .qwho { flex: 1; min-width: 0; }
        .qcard .qname { font-size: 13.5px; font-weight: 600; color: #2a2a2e; }
        .qcard .qto { font-size: 11.5px; color: #9a9aa2; margin-top: 2px; white-space: normal; word-wrap: break-word; }
        /* The address (not the name) is the real clickable mailto: link — matching whatever text
           color it's already sitting in (dark in the sender line, muted gray in To/Cc) rather than
           standing out as its own colored link, since it's still plain selectable/copyable text
           first and a link second. */
        .qaddr { color: inherit; text-decoration: none; }
        .qaddr:hover { text-decoration: underline; }
        .qcard .qdate { flex: none; font-size: 11px; color: #a3a3ab; padding-top: 2px; white-space: nowrap; }
        .qunread { display: inline-block; width: 7px; height: 7px; border-radius: 50%; background: #6d28d9;
          margin-right: 6px; vertical-align: middle; }
        .qstar { color: #d4a017; font-size: 12px; margin-right: 5px; vertical-align: middle; }
        .qcard .qbody { padding: 14px; color: #333; }
        .qcard blockquote { border: none; margin: 0; padding: 0; }
        .qattachments { display: flex; flex-wrap: wrap; align-items: center; gap: 8px;
          padding: 0 16px 16px; }
        .qattach { display: inline-flex; align-items: center; gap: 7px; background: #f4f4f6;
          border-radius: 7px; padding: 7px 10px; font-size: 12px; color: #333; text-decoration: none; }
        .qattach:hover { background: #ebebee; }
        .qaicon { display: inline-flex; align-items: center; justify-content: center; width: 22px;
          height: 22px; border-radius: 6px; flex: none; }
        .qaicon svg { display: block; }
        .qasize { color: #aaa; font-size: 11px; }
        .qattach-all { font-size: 12px; color: #6d28d9; text-decoration: none; padding: 7px 4px; }
        .qattach-all:hover { text-decoration: underline; }
        /* .qtoggle-cb is the same pure-CSS checkbox-toggle trick BuildRecipientLine's own "and N
           more" (.rtoggle-*, below) uses — a message's own quoted/forwarded history no longer gets
           this treatment (see BuildConversationHtml's own remarks: shown as-is now, matching real
           Apple Mail instead of Gmail's collapse-behind-a-toggle convention), but the checkbox class
           itself is still shared infrastructure for that unrelated toggle. */
        .qtoggle-cb { display: none; }
        /* Same checkbox-toggle trick, styled inline instead of as a block pill — "and N more" on a
           To/Cc line reads as a simple text link, expanding in place rather than opening a panel.
           Two labels sharing one checkbox (only one visible at a time) is what lets clicking it a
           second time actually say "show less" and collapse back, instead of "and N more" just
           sitting there looking unclickable once everything's already expanded. */
        .rtoggle-content, .rtoggle-less { display: none; }
        .qtoggle-cb:checked + .rtoggle-content { display: inline; }
        .qtoggle-cb:checked + .rtoggle-content + .rtoggle-more { display: none; }
        .qtoggle-cb:checked + .rtoggle-content + .rtoggle-more + .rtoggle-less { display: inline; }
        .rtoggle-label { color: #6d28d9; cursor: pointer; text-decoration: underline; }
        </style></head><body>{{bodyHtml}}</body></html>
        """;


    private static readonly Regex RemoteImageSrc =
        new("""(<img\b[^>]*\bsrc\s*=\s*["'])(https?://[^"']+)(["'])""", RegexOptions.IgnoreCase);

    private const string TransparentPixel = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==";


    // ---- Untrusted HTML hardening -------------------------------------------------------------
    // A received message's HTML comes straight from whoever sent it. Two layers of defense: script
    // execution is turned off entirely for ReadingPane's WebView2 (see ConfigureReadingPane, the
    // strong guarantee), and HtmlSanitizer (Ganss.Xss, AngleSharp-based — an actual DOM parse, not a
    // regex pass over tag soup) strips everything else that doesn't need script to do damage:
    // <iframe>/<object>/<embed>/<form> still fetch or submit to a remote URL, <meta
    // http-equiv="refresh"> still redirects, and inline event-handler attributes/javascript: hrefs
    // are gone regardless of how they're spelled or obfuscated — a regex pass over raw tag text
    // can be fooled by things a real parser can't (broken attribute quoting, HTML entities inside a
    // tag name, duplicate attributes).
    private static readonly Ganss.Xss.HtmlSanitizer BodySanitizer = CreateBodySanitizer();

    private static Ganss.Xss.HtmlSanitizer CreateBodySanitizer()
    {
        var sanitizer = new Ganss.Xss.HtmlSanitizer();
        // Base64-embedded images (signatures, inline logos) are common in real mail and were never
        // blocked by the old regex pass — only SanitizeRemoteImages' http(s) tracking-pixel check
        // applies to images, so this keeps that same behavior instead of silently dropping them.
        sanitizer.AllowedSchemes.Add("data");
        return sanitizer;
    }

    private static string SanitizeHtmlForDisplay(string html) => BodySanitizer.Sanitize(html);

    /// <summary>
    /// Locks down the reading pane's WebView2 once, at startup: no DevTools, and every link click or
    /// attempted popup is redirected to the OS default browser instead of navigating in place.
    /// Script execution is on (see the remark by IsScriptEnabled below) only to support the
    /// link-hover preview in the status bar — HtmlSanitizer still strips every &lt;script&gt; tag and
    /// inline event-handler attribute from a message's own HTML before it ever reaches here, and
    /// nothing hands page script any host object or capability beyond the one-way hover postMessage
    /// this file's own InjectLinkHoverTracking sends.
    /// </summary>
    private void ConfigureReadingPane()
    {
        // WrapHtml's own "color-scheme: light" + white background only takes effect once its CSS
        // has actually painted — before that (and for whatever the control draws while idle,
        // between navigations, or if a paint stalls) it falls back to its own native default, which
        // follows the OS theme. On a dark-mode Windows PC that default is black, and a reading pane
        // that's solid black until content paints is indistinguishable from one that's stuck. This
        // removes the OS theme as a factor entirely instead of relying on the page's own CSS to
        // race it every time.
        ReadingPane.DefaultBackgroundColor = System.Drawing.Color.White;

        var core = ReadingPane.CoreWebView2;
        core.Profile.PreferredColorScheme = Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;
        // See ConfigureReadingPane's own remarks above for why this is on despite the message HTML
        // being untrusted.
        core.Settings.IsScriptEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenInDefaultBrowser(e.Uri);
        };
        // Every NavigateToString call (a new message, or a re-rendered conversation) tears down and
        // repaints the whole document, so the hover listeners need reattaching every time rather
        // than once at startup.
        core.NavigationCompleted += async (_, e) =>
        {
            if (e.IsSuccess)
                await InjectLinkHoverTrackingAsync();
        };
        core.WebMessageReceived += ReadingPane_LinkHoverMessageReceived;
        core.NavigationStarting += (_, e) =>
        {
            // Our own WrapHtml() output arrives via NavigateToString, not an http(s) URI — only a
            // real link click (or a meta-refresh regex missed) looks like this, and neither should
            // ever navigate the reading pane itself.
            if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenInDefaultBrowser(e.Uri);
            }
            // In-card attachment chips — a fake scheme instead of real script, since the reading
            // pane has JavaScript turned off entirely (mail HTML has no legitimate need for it, and
            // this way that guarantee never has to be relaxed just to open an attachment).
            else if (e.Uri.StartsWith("appattach://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                HandleAttachmentLink(e.Uri);
            }
            // A link flagged by FlagMismatchedLinks — same fake-scheme trick as appattach://,
            // routed to a confirmation dialog instead of straight to the browser.
            else if (e.Uri.StartsWith("applink://warn", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                HandleSuspiciousLink(e.Uri);
            }
            // An inline body image, wrapped by WrapClickableImages so clicking it opens a full-size
            // preview instead of doing nothing — <img> has no click handler of its own to give it
            // with script disabled, so the wrapping <a> is what makes it clickable at all.
            else if (e.Uri.StartsWith("applink://viewimage", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                _ = HandleImageLinkAsync(e.Uri);
            }
            // A sender/recipient name clicked in the reading pane (see FormatAddressLink) — opens a
            // new message addressed to them, the same as clicking a name in Apple Mail's own header.
            else if (e.Uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                var address = Uri.UnescapeDataString(e.Uri["mailto:".Length..].Split('?')[0]);
                if (!string.IsNullOrWhiteSpace(address))
                    OpenCompose(address, bodyHtml: NewMessageBodyHtml);
            }
        };
    }

    // The status text showing just before the mouse entered a link, so hovering off can restore
    // exactly what was there rather than clearing the bar to blank — null means nothing is being
    // previewed right now (so a WebMessage race from a link that's already been left doesn't stomp
    // a status update made in between).
    private string? _statusBeforeLinkHover;

    /// <summary>
    /// Adds a mouseover/mouseout listener for the whole document, reporting the hovered link's
    /// destination back over WebMessage — this is the entire reason script is enabled for this
    /// WebView2 (see ConfigureReadingPane's own remarks). Internal fake-scheme links (appattach://,
    /// applink://...) are translated to a human label here rather than shown raw.
    /// </summary>
    private async Task InjectLinkHoverTrackingAsync()
    {
        const string script = """
            (function() {
                function describe(href) {
                    if (href.indexOf('applink://warn') === 0) {
                        try { return decodeURIComponent(new URL(href).searchParams.get('url') || href); }
                        catch (e) { return href; }
                    }
                    if (href.indexOf('appattach://') === 0) return 'Attachment';
                    if (href.indexOf('applink://viewimage') === 0) return 'Image';
                    return href;
                }
                document.addEventListener('mouseover', function(e) {
                    var a = e.target.closest('a[href]');
                    if (!a) return;
                    window.chrome.webview.postMessage({ type: 'linkhover', url: describe(a.getAttribute('href')) });
                }, true);
                document.addEventListener('mouseout', function(e) {
                    var a = e.target.closest('a[href]');
                    if (!a) return;
                    window.chrome.webview.postMessage({ type: 'linkhover', url: '' });
                }, true);
            })();
            """;
        try
        {
            await ReadingPane.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn("Injecting the reading pane's link-hover tracking script failed", ex);
        }
    }

    private void ReadingPane_LinkHoverMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string url;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "linkhover"
                || !root.TryGetProperty("url", out var urlProp))
                return;
            url = urlProp.GetString() ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't parse the reading pane's link-hover message", ex);
            return;
        }

        if (url.Length > 0)
        {
            _statusBeforeLinkHover ??= StatusText.Text;
            StatusText.Text = url;
        }
        else if (_statusBeforeLinkHover is not null)
        {
            StatusText.Text = _statusBeforeLinkHover;
            _statusBeforeLinkHover = null;
        }
    }

    /// <returns>False when the link was rejected (not a well-formed http/https URL) or the OS
    /// couldn't hand it to a browser — callers that show status text use this to say so, rather
    /// than the click silently doing nothing.</returns>
    private bool OpenInDefaultBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return false;

        return BrowserLauncher.Open(_settings, parsed.AbsoluteUri);
    }

    // Matches a plain `<a href="...">text</a>` — no nested tags in the link text. Real phishing
    // mail almost always disguises a link this simple way ("click here" or, more convincingly, a
    // fake URL as the visible text); a link whose text is itself formatted HTML is vanishingly
    // rare and not worth the complexity of a real DOM walk just to also cover it.
    private static readonly Regex PlainLinkTag = new(
        """<a\b([^>]*?)\bhref\s*=\s*(["'])(.*?)\2([^>]*)>([^<]*)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Thunderbird's "link mismatch" check: when a link's own visible text is itself a URL (the
    /// classic phishing trick of showing "https://your-bank.com" as the text while the real href
    /// points somewhere else entirely), compare the two hosts. A mismatch gets routed through
    /// applink://warn instead of the real destination, so ConfigureReadingPane can show exactly
    /// where it actually goes before the user's browser opens it. A link whose visible text isn't
    /// itself a URL (i.e. the vast majority of ordinary mail links) is left completely alone —
    /// there's nothing to compare it against.
    /// </summary>
    private static string FlagMismatchedLinks(string html) => PlainLinkTag.Replace(html, m =>
    {
        var href = System.Net.WebUtility.HtmlDecode(m.Groups[3].Value);
        var visibleText = System.Net.WebUtility.HtmlDecode(m.Groups[5].Value).Trim();

        if (!Uri.TryCreate(href, UriKind.Absolute, out var hrefUri)
            || (hrefUri.Scheme != Uri.UriSchemeHttp && hrefUri.Scheme != Uri.UriSchemeHttps))
            return m.Value;

        var looksLikeUrl = visibleText.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || visibleText.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || visibleText.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeUrl)
            return m.Value;

        var textForParsing = visibleText.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? "https://" + visibleText
            : visibleText;
        if (!Uri.TryCreate(textForParsing, UriKind.Absolute, out var textUri))
            return m.Value;

        if (HostsMatch(hrefUri.Host, textUri.Host))
            return m.Value;

        var warnHref = $"applink://warn?url={Uri.EscapeDataString(href)}&label={Uri.EscapeDataString(visibleText)}";
        return $"""<a{m.Groups[1].Value}href="{warnHref}"{m.Groups[4].Value}>{m.Groups[5].Value}</a>""";
    });

    private static bool HostsMatch(string a, string b)
    {
        string Strip(string h) => h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        return Strip(a).Equals(Strip(b), StringComparison.OrdinalIgnoreCase);
    }

    private void HandleSuspiciousLink(string uri)
    {
        var query = new Uri(uri).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        if (!query.TryGetValue("url", out var realUrl))
            return;
        var label = query.GetValueOrDefault("label", realUrl);

        var choice = EmailClient.UI.ConfirmDialog.Show(this, "Suspicious link",
            $"This link's text says it goes to:\n{label}\n\nBut it actually opens:\n{realUrl}\n\n" +
            "This is a common phishing trick. Open it anyway?",
            warningIcon: true, new EmailClient.UI.ConfirmChoice("Cancel"),
            new EmailClient.UI.ConfirmChoice("Open anyway", Destructive: true));

        if (choice == "Open anyway")
            OpenInDefaultBrowser(realUrl);
    }

    private static readonly Regex ImgTag = new("""<img\b[^>]*\bsrc\s*=\s*["']([^"']+)["'][^>]*>""", RegexOptions.IgnoreCase);

    /// <summary>
    /// Wraps every real inline image in an `applink://viewimage` link so clicking it opens a
    /// full-size preview — plain &lt;img&gt; has no click behavior of its own to give it with
    /// script disabled, so a wrapping &lt;a&gt; plus a fake scheme (same trick as appattach://) is
    /// what makes an inline image clickable at all. The transparent tracking-pixel placeholder
    /// SanitizeRemoteImages substitutes isn't real content, so it's deliberately left unwrapped.
    /// </summary>
    private string WrapClickableImages(string html, string messageId) => ImgTag.Replace(html, m =>
    {
        var src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        if (src == TransparentPixel)
            return m.Value;

        var images = _conversationImages.TryGetValue(messageId, out var list) ? list : _conversationImages[messageId] = [];
        var idx = images.Count;
        images.Add(src);

        return $"""<a href="applink://viewimage?id={Uri.EscapeDataString(messageId)}&idx={idx}">{m.Value}</a>""";
    });

    private async Task HandleImageLinkAsync(string uri)
    {
        var query = new Uri(uri).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        if (!query.TryGetValue("id", out var id) || !query.TryGetValue("idx", out var idxStr)
            || !int.TryParse(idxStr, out var idx)
            || !_conversationImages.TryGetValue(id, out var images) || idx < 0 || idx >= images.Count)
            return;

        // A remote (non-data-URI) image has to actually be fetched before it can open, and with
        // InlineImageHttp's own 20s timeout that's long enough to otherwise look like the click
        // just didn't register.
        if (!images[idx].StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            StatusText.Text = "Opening image…";

        var path = await MaterializeImageAsync(images[idx]);
        if (path is null)
        {
            StatusText.Text = "Couldn't open the image";
            return;
        }

        var name = Path.GetFileName(path);
        if (QuickLookWindow.CanPreview(name))
            new QuickLookWindow(path, name) { Owner = this }.Show();
        else
            new AttachmentViewerWindow(path, name) { Owner = this }.Show();
    }

    /// <summary>
    /// QuickLookWindow (like AttachmentViewerWindow) needs a real file on disk — an inline image's
    /// "source" is either a data: URI (already-decoded bytes, from an embedded cid: image) or a
    /// remote http(s) URL, neither of which it can open directly.
    /// </summary>
    private async Task<string?> MaterializeImageAsync(string src)
    {
        try
        {
            Directory.CreateDirectory(AttachmentCacheDirectory);
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma < 0)
                    return null;
                var header = src[5..comma]; // "image/png;base64"
                var ext = header.Split(';')[0].Split('/').ElementAtOrDefault(1) ?? "png";
                var bytes = Convert.FromBase64String(src[(comma + 1)..]);
                var path = Path.Combine(AttachmentCacheDirectory, $"inline-{Guid.NewGuid():N}.{ext}");
                await File.WriteAllBytesAsync(path, bytes);
                return path;
            }
            if (Uri.TryCreate(src, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var ext = Path.GetExtension(uri.LocalPath) is { Length: > 1 } e ? e : ".img";
                var path = Path.Combine(AttachmentCacheDirectory, $"inline-{Guid.NewGuid():N}{ext}");
                var bytes = await InlineImageHttp.GetByteArrayAsync(uri);
                await File.WriteAllBytesAsync(path, bytes);
                return path;
            }
        }
        catch (Exception)
        {
            // Falls through to null — StatusText tells the user it couldn't be opened.
        }
        return null;
    }

    // Gmail/Outlook/Apple Mail all block remote images by default (they can be used to confirm
    // an email was opened, i.e. a tracking pixel) and offer a one-click "show images" bar.
    private static (string html, bool blocked) SanitizeRemoteImages(string html)
    {
        var blocked = false;
        var result = RemoteImageSrc.Replace(html, m =>
        {
            blocked = true;
            // Only the src attribute is touched here — every other attribute the original <img>
            // had (width/height, an inline style with explicit dimensions, both common for a real
            // marketing/newsletter image) survives untouched, so the transparent-pixel replacement
            // used to still render at the original image's full declared size: no visible picture,
            // but a real, sized blank gap left sitting in the layout — a live report caught exactly
            // this ("webmail doesn't render and simply makes it look like there's no image there at
            // all", unlike this app). data-blocked is picked up by a dedicated CSS rule
            // (WrapHtml's own stylesheet) that collapses it to nothing with !important, which is
            // what actually wins over the original tag's own plain (non-!important) sizing — adding
            // a competing style="display:none" attribute here directly isn't safe, since the
            // original tag may already have its own style="" attribute and a tag can't have two.
            return $"{m.Groups[1].Value}{TransparentPixel}{m.Groups[3].Value} data-blocked=\"1\"";
        });
        return (result, blocked);
    }

    private void ShowImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_blockedImagesHtml is null)
            return;
        ImagesBlockedBar.SlideUpHide();
        NavigateReadingPane(WrapHtml(_blockedImagesHtml));
        _blockedImagesHtml = null;
    }

    /// <summary>
    /// NavigateToString silently caps out at roughly 1.5M characters and throws
    /// ArgumentException ("Value does not fall within the expected range") instead of truncating
    /// or wrapping — a real crash seen live, from a message whose rendered HTML (a long quoted
    /// thread, several inline images resolved to base64 data URIs) pushed past that. There's no
    /// stream-based navigation API to fall back to here, so this writes the content to a temp
    /// .html file and navigates there instead — a real file has no such size limit.
    /// </summary>
    /// <summary>
    /// Re-parses the fully assembled document and serializes it back out — a real DOM round trip
    /// (AngleSharp, the same engine HtmlSanitizer is built on), not another regex pass. This is
    /// belt-and-suspenders structural well-formedness, not security sanitization (every message
    /// body in here already went through BodySanitizer before assembly): the conversation view
    /// builds its multi-card markup from each message's own raw body, which a sender's mail client
    /// can hand over with a genuinely unclosed tag — that doesn't error on its own, it just
    /// silently swallows every sibling .qcard appended after it into the browser's own
    /// auto-correction of that unclosed tag, which is exactly what
    /// showed up as message cards nesting inside one another instead of stacking as separate,
    /// visually flat cards the way Apple Mail's own conversation view does. A DOM round trip can't
    /// leave anything unbalanced — the parser closes whatever the input left open, the same way a
    /// browser's own parser would, just before that auto-correction has a chance to nest a sibling
    /// under it.
    /// </summary>
    private static string BalanceHtmlDocument(string html)
    {
        try
        {
            var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
            using var writer = new StringWriter();
            document.ToHtml(writer, AngleSharp.Html.HtmlMarkupFormatter.Instance);
            return writer.ToString();
        }
        catch (Exception)
        {
            return html; // best-effort — worst case, the pre-existing (possibly unbalanced) output
        }
    }

    private void NavigateReadingPane(string html)
    {
        html = BalanceHtmlDocument(html);
        try
        {
            ReadingPane.NavigateToString(html);
        }
        catch (ArgumentException)
        {
            // One fixed path, reused (overwritten) every time this fallback fires, rather than a
            // fresh Guid-named file per call — the first version of this left one orphaned .html
            // file in %TEMP% per oversized message opened, for the rest of the process's life.
            // Overwriting is safe: Navigate reads the file once as part of loading it, and rewriting
            // it afterward (for the *next* oversized message) doesn't touch whatever already
            // finished rendering from the previous read.
            File.WriteAllText(_overflowHtmlPath, html, System.Text.Encoding.UTF8);
            ReadingPane.CoreWebView2.Navigate(new Uri(_overflowHtmlPath).AbsoluteUri);
        }
    }

    private readonly string _overflowHtmlPath =
        Path.Combine(Path.GetTempPath(), $"purplemail-msg-overflow-{Environment.ProcessId}.html");

    private bool _syncingSelection;
    // Bumped on every selection change so a slow fetch that's still in flight when a newer one
    // starts (or finishes first) knows it's stale and doesn't overwrite what's now actually
    // selected — without this, clicking through messages quickly could show mail B's content
    // arriving late and stomping over mail C's, which had already loaded and rendered correctly.
    private int _openRequestSeq;

    // Cancels whatever background IMAP work (right now, only conversation-sibling enrichment) was
    // started for the message/folder the user was on a moment ago, whenever they move on to a
    // different one — opening another message or switching folders. This can't abort a command
    // already in flight (MailKit's ImapClient allows exactly one at a time per connection, and
    // walking away from one mid-flight corrupts the connection for everything after it — see
    // AcquireImapLockAsync's own remarks), but it does stop that background work from queuing for
    // the shared connection lock at all once it's no longer wanted, and from starting its next
    // step once whatever step is currently running finishes. Without this, background sibling
    // search for a message the user has already left kept competing with — and sometimes visibly
    // delaying — the very next folder switch or message open for a connection that can only ever
    // do one thing at a time.
    private CancellationTokenSource _backgroundWorkCts = new();

    /// <summary>Cancels whatever background mail work was running for wherever the user just was,
    /// and returns a token for whatever they're doing now. Call this at the start of any action
    /// that means "the user has moved on" — opening a message, switching folders — before kicking
    /// off new background work of your own.</summary>
    private CancellationToken RestartBackgroundMailWork()
    {
        _backgroundWorkCts.Cancel();
        _backgroundWorkCts.Dispose();
        _backgroundWorkCts = new CancellationTokenSource();
        return _backgroundWorkCts.Token;
    }

    // Once a real message body has actually been fetched over IMAP, keep it — reopening the same
    // mail later in the session is then a free in-memory lookup instead of another network round
    // trip. Never evicted: a folder page tops out at 50-ish rows, so worst case this is a few
    // hundred cached bodies over a long session, not an unbounded leak.
    //
    // Keyed by (mailbox, uid), not a bare UID string — matching MailStore.GetBody's own composite
    // key for exactly the same reason: a UID is only unique within its own folder, so Inbox UID 42
    // and Sent UID 42 are unrelated messages. A bare-UID key here meant opening one, then the other,
    // could return the first message's cached body for the second's request.
    private readonly Dictionary<(string Mailbox, string Uid), MessageDetail> _messageDetailCache = new();

    private async void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
            return;
        if (MessageList.SelectedItem is not InboxRow row)
            return;

        // Opening a draft resumes editing it, rather than showing it as a read-only message.
        if (UseMockData && _currentFolder == "Drafts")
        {
            var draft = _localBodies.GetValueOrDefault(row.Id) ?? MockData.MessageBodies.GetValueOrDefault(row.Id);
            if (draft is not null)
            {
                MessageList.SelectedItem = null;
                OpenCompose(
                    draft.To,
                    draft.Subject == "(no subject)" ? "" : draft.Subject,
                    MailText.HtmlToPlainText(draft.BodyHtml),
                    draft.Cc,
                    draft.Bcc,
                    replacesDraftId: row.Id,
                    bodyHtml: draft.BodyHtml);
                return;
            }
        }

        var requestId = ++_openRequestSeq;
        var backgroundCt = RestartBackgroundMailWork();
        ShowReadingPaneLoading(row);

        var openMailbox = SpecialMailbox(_currentFolder);
        var wasCached = _localBodies.ContainsKey(row.Id) || _messageDetailCache.ContainsKey((openMailbox, row.Id))
            || _mailStore?.GetBody(openMailbox, row.Id) is not null;

        // Debounce before the real IMAP fetch starts — not before the header above, which should
        // always update instantly, and not for a message already sitting in local storage, which
        // costs nothing to abandon and needs no protecting. Clicking through several *uncached*
        // messages quickly used to queue one full live fetch per click: the requestId check further
        // down already discards a stale one's *result* once it finishes, but nothing stopped it from
        // actually running first — and since MailKit serializes every command behind the one shared
        // connection lock, a burst of clicks meant dutifully completing every earlier,
        // already-abandoned fetch before the one the user actually landed on ever got a turn. On a
        // slow or proxied connection that showed up as the body staying stuck on an old message for a
        // minute or more after the header had long since moved on. A short pause here lets a fast run
        // of clicks collapse into just the last one — negligible next to how long a live fetch
        // already takes. This used to run unconditionally (including for cached messages, despite a
        // comment here claiming otherwise), which is why even clicking through already-read mail
        // never felt instant — every single open paid this 150ms regardless of local data being
        // sitting right there.
        if (!UseMockData && !wasCached)
        {
            await Task.Delay(150);
            if (requestId != _openRequestSeq)
                return;
        }

        MessageDetail? detail;
        try
        {
            detail = _localBodies.GetValueOrDefault(row.Id)
                ?? _messageDetailCache.GetValueOrDefault((openMailbox, row.Id))
                // MailStore next — an indexed local read, instant and durable across restarts
                // (unlike _messageDetailCache, which is only this session's in-memory dictionary).
                ?? (!UseMockData ? _mailStore?.GetBody(openMailbox, row.Id) : null)
                ?? (UseMockData
                    ? MockData.MessageBodies.GetValueOrDefault(row.Id)
                    : await _mail!.OpenMessageAsync(row.Id));
            // A message read once stays readable with no connection. Deliberately not gated on
            // UseMockData: a failed sign-in leaves that true, and that is precisely the case this
            // fallback exists for. _cache is null until an account is known, so genuine sample-data
            // mode can't reach it. Kept as a fallback behind MailStore rather than removed outright:
            // an account synced before this session's database existed has its history in the old
            // cache only, and MailStore starts empty for it until a fresh sync/open repopulates it.
            detail ??= _cache?.LoadDetail(row.Id);

            if (!UseMockData && detail is not null)
            {
                _messageDetailCache[(openMailbox, row.Id)] = detail;
                _mailStore?.SaveBody(openMailbox, row.Id, detail);
            }
        }
        catch (SessionExpiredException)
        {
            ReadingPaneLoadingOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = "Signed out — sign in again to continue";
            return;
        }
        catch (Exception ex)
        {
            ReadingPaneLoadingOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Couldn't open the message: {ex.Message}";
            return;
        }
        if (detail is null)
        {
            ReadingPaneLoadingOverlay.Visibility = Visibility.Collapsed;
            if (!UseMockData)
                StatusText.Text = "Couldn't read the message";
            return;
        }

        // Selection moved on again while this fetch was still in flight — applying it now would
        // stomp over whatever's already loaded (or still loading) for the message actually
        // selected, which is exactly the "selected and opened don't match" symptom this guards.
        if (requestId != _openRequestSeq)
            return;

        await ReadingPane.EnsureCoreWebView2Async();

        var wasUnread = row.Unread;
        // Fire-and-forget, not awaited — the read-flag reaching the server doesn't gate anything
        // the user actually sees: the local unread state right below is already optimistic (see
        // UpdateInboxBadge's own comment), and a failed flag isn't worth surfacing either (per the
        // comment that used to sit on this same try/catch). Awaiting it here meant every unread
        // message open paid for a full second IMAP round trip — on top of OpenMessageAsync's own —
        // before the body could render, which is exactly what showed up as the reading pane
        // visibly lagging behind the header (which updates immediately, before either fetch).
        if (!UseMockData && wasUnread)
            _ = MarkReadInBackgroundAsync(row.Id);

        _openRow = UpdateRow(row.Id, r => r with { Unread = false }) ?? row;
        _openDetail = detail;
        // Opening an unread message is common enough that even the brief round trip
        // UpdateInboxBadge's own refresh takes would read as the count lagging behind what the
        // reading pane already shows — so it also drops by one immediately here. The refresh that
        // follows still reconciles against the server's actual count regardless.
        if (!UseMockData && wasUnread)
        {
            // MailStore's own local unread_count for this folder must be adjusted right here,
            // synchronously — not inside MarkReadInBackgroundAsync above, which awaits a live IMAP
            // round trip first. UpdateInboxBadge (right below) fires a fire-and-forget
            // RefreshFoldersAsync that reads this same table; without updating it here first, that
            // read still saw the pre-decrement count and stomped the optimistic SetInboxBadge call
            // right back to the old number — a live report: the badge visibly ticked down and
            // immediately jumped back up. Applying the same flag write MarkReadInBackgroundAsync
            // would otherwise apply later (after the network call) keeps the two paths consistent.
            var openMailboxForBadge = SpecialMailbox(_currentFolder);
            _mailStore?.UpdateFlags(openMailboxForBadge, row.Id, unread: false);
            _mailStore?.AdjustFolderUnread(openMailboxForBadge, -1);
            if (int.TryParse(InboxUnreadCount.Text, out var shownUnread) && shownUnread > 0)
                SetInboxBadge(shownUnread - 1);
        }
        UpdateInboxBadge();

        // UpdateRow just replaced this row's object in _messages (InboxRow is an immutable record,
        // so marking it read created a new instance) — the ListBox's own SelectedItem still points
        // at the old, now-absent instance, which orphans the selection: SelectionChanged already
        // fired (that's how we got here), but no container is left marked IsSelected, so the "this
        // is the open message" highlight silently never shows. Re-pointing SelectedItem at the
        // replacement re-syncs it.
        if (!ReferenceEquals(MessageList.SelectedItem, _openRow))
        {
            _syncingSelection = true;
            try
            {
                MessageList.SelectedItem = _openRow;
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        EmptyState.Visibility = Visibility.Collapsed;
        // Only the empty-state-to-first-message transition fades in; switching between two
        // messages that are both already showing skips the animation; re-fading on every click
        // would add perceived latency rather than smoothness there.
        if (ReadingCard.Visibility != Visibility.Visible)
            ReadingCard.FadeIn(180);
        else
            ReadingCard.Visibility = Visibility.Visible;

        ReadingSubject.Text = detail.Subject;
        ReadingFrom.Text = detail.From;
        ReadingDate.Text = detail.Date;
        ReadingAvatarInitial.Text = row.Initial;
        UpdateReadingAvatar(row.SenderAddress);

        if (IsExternalSender(detail.From))
            ExternalSenderBar.SlideDownReveal();
        else
            ExternalSenderBar.Visibility = Visibility.Collapsed;

        _unsubscribeUrl = IsIitbSender(detail.From) ? null : detail.UnsubscribeUrl;
        _unsubscribeMailto = IsIitbSender(detail.From) ? null : detail.UnsubscribeMailto;
        if (_unsubscribeUrl is not null || _unsubscribeMailto is not null)
            UnsubscribeBar.SlideDownReveal();
        else
            UnsubscribeBar.Visibility = Visibility.Collapsed;

        UpdateVipToggleUi(row.SenderAddress);

        // Every message renders the same way — as one or more Apple Mail-style cards, each fully
        // self-contained with its own avatar/sender/to/date — rather than switching between a plain
        // layout for a lone message and cards only once there's a real conversation. A single
        // message is just a conversation of one. Siblings are found by subject across every folder,
        // matching Apple Mail's own default of including related messages from other mailboxes.
        ReadingFromRow.Visibility = Visibility.Collapsed;

        string cleanHtml;
        try
        {
            // The opened message's own body is already in hand at this point — render it right
            // away rather than making the reading pane wait on sibling discovery too. Finding
            // conversation siblings means real IMAP round trips across multiple folders, which can
            // take several seconds on a slow connection; a pane that stays blank that whole time is
            // indistinguishable from one that's stuck. Siblings are searched for afterward, in the
            // background, and folded in once found (see EnrichReadingPaneWithSiblingsAsync below).
            var primaryOnly = new List<(InboxRow Row, MessageDetail Detail)> { (_openRow, detail) };
            cleanHtml = FlagMismatchedLinks(BuildConversationHtml(primaryOnly));

            if (detail.Calendar is { } invite)
            {
                // A real calendar invite beats guessing — an exact start time (and location, when the
                // invite has one) instead of scraping "tomorrow at 5 PM" out of prose.
                _meetingLinkUrl = invite.JoinUrl;
                MeetingBarTitle.Text = invite.Title;
                var when = invite.Start is { } start ? start.LocalDateTime.ToString("ddd, MMM d · h:mm tt") : null;
                MeetingBarSubtext.Text = (when, invite.Location) switch
                {
                    (not null, not null) => $"{when} · {invite.Location}",
                    (not null, null) => when,
                    (null, not null) => invite.Location,
                    _ => "Calendar invite",
                };
                JoinMeetingButton.Visibility = invite.JoinUrl is null ? Visibility.Collapsed : Visibility.Visible;
                MeetingBar.SlideDownReveal();
            }
            else if (FindMeetingLink(cleanHtml) is { } meetingLink)
            {
                _meetingLinkUrl = meetingLink.Url;
                // A Zoom invite's own "Topic: ..." line is the meeting's real name when present —
                // otherwise the mail subject is almost always the meeting's real name too ("Weekly
                // sync", "Thesis review"), far more useful as the card's title than repeating "Zoom
                // meeting" or the raw URL, which is all the link itself carries.
                MeetingBarTitle.Text = meetingLink.Title ?? detail.Subject;
                MeetingBarSubtext.Text = meetingLink.When is { } linkWhen
                    ? $"{meetingLink.Label} · {linkWhen}"
                    : $"{meetingLink.Label} meeting";
                JoinMeetingButton.Visibility = Visibility.Visible;
                MeetingBar.SlideDownReveal();
            }
            else
            {
                _meetingLinkUrl = null;
                MeetingBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception)
        {
            // The conversation-card/meeting-detection pipeline is all best-effort presentation on
            // top of the real body — a message with some unusual structure that trips one of those
            // regexes/parsers must still show its plain body, not a blank reading pane forever.
            cleanHtml = SanitizeHtmlForDisplay(detail.BodyHtml);
            _meetingLinkUrl = null;
            MeetingBar.Visibility = Visibility.Collapsed;
        }

        // IITB department/course notices lean on inline images (posters, timetables) more than most
        // mail does, and the whole institute domain is about as trusted a sender as this app can
        // reason about — so remote images load straight through instead of being blocked pending a
        // click, the same trust boundary IsIitbSender already draws for the unsubscribe bar.
        var (safeHtml, hadRemoteImages) = IsIitbSender(detail.From)
            ? (cleanHtml, false)
            : SanitizeRemoteImages(cleanHtml);
        _blockedImagesHtml = hadRemoteImages ? cleanHtml : null;
        if (hadRemoteImages)
            ImagesBlockedBar.SlideDownReveal();
        else
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
        NavigateReadingPane(WrapHtml(safeHtml));
        ReadingPaneLoadingOverlay.Visibility = Visibility.Collapsed;

        // Fire-and-forget: the primary message is already showing above. Guarded by requestId so a
        // slow search for a message the user has since navigated away from can't overwrite whatever
        // is showing now, and by backgroundCt so it stops competing for the shared connection at
        // all once that happens (see RestartBackgroundMailWork's own remarks).
        _ = EnrichReadingPaneWithSiblingsAsync(requestId, _openRow, detail, backgroundCt);
    }

    /// <summary>
    /// Finds this message's conversation siblings in the background and, if any turn up, redraws
    /// the reading pane with the full multi-card conversation — without making the caller (the
    /// message-open flow above) wait for it. Cross-folder sibling search means several extra IMAP
    /// round trips (searching Inbox and Sent, then fetching each sibling's own body, each behind
    /// the one shared connection lock every other mail operation also needs), which is real,
    /// user-visible latency on a slow or proxied connection — that cost is worth keeping for what
    /// it shows, but not worth making the reading pane sit blank for.
    ///
    /// Two passes, not one: a live report showed why a single await-then-render isn't enough even
    /// though MailStore is fast — a message can have every one of its ancestors already synced and
    /// every sibling's body already cached (zero network needed for any of that), and *still* sit
    /// on the single-card "•••" view for up to a minute, because the one remaining network step
    /// (the missing-ancestor live fallback inside GatherConversationAsync) happened to queue behind
    /// the shared IMAP connection during MailSyncEngine's own initial post-launch sync burst. The
    /// fast local-only pass below renders whatever's already on disk immediately, with no such
    /// dependency; the fuller pass afterward adds anything only a live lookup can find.
    ///
    /// This is also what gives a long thread its staged reveal: FindConversationSiblings itself no
    /// longer caps at 8 (see its own remarks — that was silently dropping a real thread's older
    /// messages once it grew past that, with no way to see them), but the *first* render here still
    /// shows only the opened message plus its 8 most recent siblings — instant, and enough to read
    /// immediately — while whatever's beyond that renders in this same background pass, gated by the
    /// same requestId check as the live-fallback case: it only ever lands if the user is still on
    /// this message when it finishes, never overwriting whatever they've since navigated to.
    /// </summary>
    private const int FastPassCardLimit = 9; // the opened message + 8 siblings

    private async Task EnrichReadingPaneWithSiblingsAsync(
        int requestId, InboxRow row, MessageDetail detail, CancellationToken ct)
    {
        var localOnly = GatherLocalConversation(row, detail);
        var shown = 0;
        if (requestId == _openRequestSeq && localOnly.Count > 1)
        {
            var firstPass = localOnly.Count > FastPassCardLimit ? localOnly.Take(FastPassCardLimit).ToList() : localOnly;
            RenderConversation(firstPass, detail);
            shown = firstPass.Count;
        }

        List<(InboxRow Row, MessageDetail Detail)> conversation;
        try
        {
            conversation = await GatherConversationAsync(row, detail, ct);
        }
        catch (OperationCanceledException)
        {
            return; // the user moved on before this got anywhere — nothing to show
        }
        catch (Exception ex)
        {
            // Logged for the same reason as RenderConversation's own catch — a live report of a
            // long thread's card count staying stuck at the fast pass's 9 with no way to tell why.
            Log.Warn($"Gathering the fuller conversation failed (fast pass showed {shown})", ex);
            return; // whatever the local pass already showed (or the primary message alone) stands
        }

        Log.Debug($"EnrichReadingPaneWithSiblingsAsync for {row.Id}: fast pass showed {shown}, fuller pass found {conversation.Count}");

        // Stale — the user has opened a different message since this search started. Also skip a
        // pointless identical re-render when the fuller pass found nothing beyond what's already
        // on screen (comparing against what was actually rendered above, not the fast pass's own
        // full local count — a long thread's fast pass deliberately renders fewer cards than it
        // found, and this second pass is exactly what's meant to reveal the rest of those).
        if (requestId != _openRequestSeq || conversation.Count <= 1 || conversation.Count == shown)
        {
            if (conversation.Count != shown)
                Log.Debug($"Fuller conversation pass discarded: requestId stale (wanted {requestId}, now {_openRequestSeq}), found {conversation.Count} vs shown {shown}");
            return;
        }

        RenderConversation(conversation, detail);
    }

    /// <summary>The network-free half of conversation gathering — MailStore's own ancestor lookup,
    /// plus whatever siblings already have a cached body (in-memory, MailStore, or sample data).
    /// A sibling found but not yet cached is simply skipped here rather than fetched — this pass
    /// exists specifically to render fast, and GatherConversationAsync's fuller pass (missing-
    /// ancestor live fallback, live body fetch for anything still uncached) picks up the rest.</summary>
    private List<(InboxRow Row, MessageDetail Detail)> GatherLocalConversation(InboxRow row, MessageDetail openedDetail)
    {
        var results = new List<(InboxRow Row, MessageDetail Detail)> { (row, openedDetail) };

        IEnumerable<InboxRow> siblings;
        if (UseMockData)
        {
            var key = row.ConversationKey;
            if (string.IsNullOrWhiteSpace(key))
                return results;
            siblings = _folderData.Values.SelectMany(rows => rows)
                .Where(r => r.Id != row.Id && r.ConversationKey == key)
                .GroupBy(r => r.Id).Select(g => g.First())
                .Take(8);
        }
        else
        {
            if (openedDetail.References is null || openedDetail.References.Count == 0)
                return results;
            try
            {
                siblings = _mailStore?.FindConversationSiblings(
                    SpecialMailbox(_currentFolder), row.Id, openedDetail.References) ?? [];
            }
            catch (Exception) { siblings = []; }
        }

        foreach (var sibling in siblings)
        {
            var siblingMailbox = sibling.Mailbox ?? SpecialMailbox(_currentFolder);
            var siblingDetail = _localBodies.GetValueOrDefault(sibling.Id)
                ?? _messageDetailCache.GetValueOrDefault((siblingMailbox, sibling.Id))
                ?? (UseMockData
                    ? MockData.MessageBodies.GetValueOrDefault(sibling.Id)
                    : _mailStore?.GetBody(siblingMailbox, sibling.Id));
            if (siblingDetail is not null)
                results.Add((sibling, siblingDetail));
        }

        return SortConversation(results);
    }

    private void RenderConversation(List<(InboxRow Row, MessageDetail Detail)> conversation, MessageDetail detail)
    {
        string cleanHtml;
        try
        {
            cleanHtml = FlagMismatchedLinks(BuildConversationHtml(conversation));
        }
        catch (Exception ex)
        {
            // Logged, not just swallowed — a live report of a conversation staying stuck at its
            // first-pass card count with no visible change turned out to have no diagnostic trail
            // at all: every catch on this whole path was silent. If this fires, the fuller pass
            // already found more cards than what's on screen but couldn't actually render them.
            Log.Warn($"Building conversation HTML failed ({conversation.Count} cards) — keeping whatever's already shown", ex);
            return; // same best-effort reasoning as the primary path's own catch
        }

        var (safeHtml, hadRemoteImages) = IsIitbSender(detail.From)
            ? (cleanHtml, false)
            : SanitizeRemoteImages(cleanHtml);
        _blockedImagesHtml = hadRemoteImages ? cleanHtml : null;
        if (hadRemoteImages)
            ImagesBlockedBar.SlideDownReveal();
        else
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
        NavigateReadingPane(WrapHtml(safeHtml));
    }

    /// <summary>
    /// Finds every other message in the same conversation (subject with reply/forward prefixes
    /// stripped) — across every folder for sample data, matching Apple Mail's default of including
    /// related messages from other mailboxes, or across the *whole current folder* for a live
    /// account (not just whatever page happens to be loaded — a reply from months ago still needs
    /// to thread in, the same as Apple Mail does regardless of pagination). Returns newest-first
    /// (Apple Mail's own conversation-view default), opened message included.
    /// </summary>
    private async Task<List<(InboxRow Row, MessageDetail Detail)>> GatherConversationAsync(
        InboxRow row, MessageDetail openedDetail, CancellationToken ct = default)
    {
        var results = new List<(InboxRow Row, MessageDetail Detail)> { (row, openedDetail) };

        IEnumerable<InboxRow> siblings;
        if (UseMockData)
        {
            // Sample data has no real Message-ID/References to match on, so this path still groups
            // by subject — it's synthetic demo content, not real mail, and not what the live fix
            // below is about.
            var key = row.ConversationKey;
            if (string.IsNullOrWhiteSpace(key))
                return results;
            siblings = _folderData.Values.SelectMany(rows => rows)
                .Where(r => r.Id != row.Id && r.ConversationKey == key)
                .GroupBy(r => r.Id).Select(g => g.First())
                .Take(8);
        }
        else
        {
            // Nothing to search for without a real References chain — siblings are ancestors only
            // now (see MailStore.FindConversationSiblings' own remarks), so the opened message's own
            // Message-ID alone (with no References) has nothing to match against.
            if (openedDetail.References is null || openedDetail.References.Count == 0)
                return results;

            // A local MailStore query first, not a live cross-folder IMAP SEARCH — this is what
            // actually removes the lock-contention bug that motivated a good chunk of this
            // session's earlier debugging: there's no network round trip here at all for the
            // common case, so it can't compete with (or be delayed by) anything else on the shared
            // connection.
            try
            {
                siblings = _mailStore?.FindConversationSiblings(
                    SpecialMailbox(_currentFolder), row.Id, openedDetail.References) ?? [];
            }
            catch (Exception ex)
            {
                Log.Warn($"Local conversation-sibling lookup failed for {row.Id}", ex);
                siblings = []; // best-effort — a lookup failure still shows the opened message alone
            }

            // The opened message's own References name specific ancestor ids — a live report
            // confirmed a real, older thread whose earlier messages had simply aged out of
            // MailSyncEngine's synced window (the most recent ~200 per folder) and so had no local
            // row to match against, even though the thread link itself was perfectly real. Rather
            // than re-adding a full live cross-folder search on every open (reintroducing the exact
            // contention removed above), this asks the server for *only* the specific ancestor ids
            // MailStore couldn't answer for — a small, precisely-bounded fallback, not a general one.
            if (!UseMockData && openedDetail.References is { Count: > 0 } refs)
            {
                var missingAncestors = refs
                    .Where(r => !string.IsNullOrWhiteSpace(r) && _mailStore?.HasMessageId(r) != true)
                    .ToList();
                if (missingAncestors.Count > 0)
                {
                    try
                    {
                        var live = await _mail!.FindConversationSiblingsAsync(
                            row.Id, openedMessageId: null, openedReferences: missingAncestors, ct: ct);
                        if (live.Count > 0)
                            siblings = siblings.Concat(live)
                                .GroupBy(s => (s.Mailbox, s.Id)).Select(g => g.First())
                                .ToList();
                    }
                    catch (OperationCanceledException) { /* the user moved on — the local results already gathered still stand */ }
                    catch (Exception ex) { Log.Warn($"Live missing-ancestor fallback failed for {missingAncestors.Count} id(s)", ex); }
                }
            }
        }

        foreach (var sibling in siblings)
        {
            // Same reasoning as FindConversationSiblingsAsync's own per-folder check: only between
            // siblings, never abandoning a fetch already in flight — the user having moved on stops
            // this from starting the *next* sibling's round trip, nothing more.
            if (ct.IsCancellationRequested)
                break;

            // A cross-folder sibling's UID is only unique within its own folder (see
            // InboxRow.Mailbox's own remarks) — Inbox UID 42 and Sent UID 42 are unrelated
            // messages, so the body's own (mailbox, uid) key must use the sibling's actual folder,
            // not whatever folder the opened message itself is in.
            var siblingMailbox = sibling.Mailbox ?? SpecialMailbox(_currentFolder);
            MessageDetail? siblingDetail = _localBodies.GetValueOrDefault(sibling.Id)
                ?? (UseMockData
                    ? MockData.MessageBodies.GetValueOrDefault(sibling.Id)
                    : _mailStore?.GetBody(siblingMailbox, sibling.Id));

            var bodyFetchFailed = false;
            if (siblingDetail is null && !UseMockData)
            {
                try
                {
                    siblingDetail = await _mail!.OpenMessageAsync(sibling.Id, sibling.Mailbox, ct);
                    if (siblingDetail is not null)
                        _mailStore?.SaveBody(siblingMailbox, sibling.Id, siblingDetail);
                }
                catch (Exception) { bodyFetchFailed = true; }
            }

            if (siblingDetail is not null)
            {
                results.Add((sibling, siblingDetail));
            }
            else if (bodyFetchFailed)
            {
                // A real Apple Mail-style thread still lists a message it knows exists even when
                // its body couldn't be fetched — silently dropping it instead (the old behaviour)
                // meant the conversation quietly lost a message with no indication anything was
                // ever there. The row itself (sender/subject/date) already came back from the
                // header search that found this sibling in the first place, so there's real
                // information to show even without the body.
                results.Add((sibling, new MessageDetail(
                    Subject: sibling.Subject, From: sibling.Sender, Date: sibling.Date,
                    BodyHtml: """<p style="color:#999;font-style:italic;">Couldn't load this message.</p>""")));
            }
        }

        return SortConversation(results);
    }

    /// <summary>The message actually opened always leads, regardless of where it falls
    /// chronologically in the thread — sorting purely newest-first (the old behaviour) meant
    /// opening an older message in a thread that already has a later reply (a search hit, or
    /// clicking an older row directly) buried the very message just opened — its attachments
    /// included — under that reply's card instead of showing it. The rest of the thread still
    /// reads newest-first below it, and this is a no-op for the common case of opening the newest
    /// message in a thread, since that's already what "leads" would mean anyway. Shared by
    /// GatherLocalConversation and GatherConversationAsync so both passes order results identically
    /// — the fast pass's render must not visibly reshuffle once the fuller pass replaces it.</summary>
    private static List<(InboxRow Row, MessageDetail Detail)> SortConversation(
        List<(InboxRow Row, MessageDetail Detail)> results)
    {
        var opened = results[0];
        var rest = results.Skip(1).OrderByDescending(r => r.Row.Timestamp ?? DateTime.MinValue);
        return [opened, .. rest];
    }

    // Unique across the whole reading pane, not just one To/Cc line — several long recipient lists
    // in the same conversation each producing their own toggle would otherwise collide on the same
    // checkbox id and cross-wire each other's expand/collapse state.
    private static int _quoteToggleSeq;

    /// <summary>
    /// A To/Cc line that actually lets you see who's hidden behind "and N more" — a plain collapsed
    /// summary with no way to expand it is a dead end once a list is long enough to need collapsing
    /// in the first place. Pure-CSS checkbox-toggle trick, since script is disabled in the reading
    /// pane's WebView2.
    /// </summary>
    private string BuildRecipientLine(string label, string recipients)
    {
        if (string.IsNullOrWhiteSpace(recipients))
            return "";

        var (shown, hidden) = MailText.SplitRecipients(recipients);
        var shownText = string.Join(", ", shown.Select(FormatAddressLink));
        if (hidden.Count == 0)
            return $"""<div class="qto">{label}: {shownText}</div>""";

        var toggleId = $"rtoggle{System.Threading.Interlocked.Increment(ref _quoteToggleSeq)}";
        var hiddenText = string.Join(", ", hidden.Select(FormatAddressLink));
        return $"""
            <div class="qto">{label}: {shownText} <input type="checkbox" id="{toggleId}" class="qtoggle-cb"><span class="rtoggle-content">, {hiddenText}</span> <label for="{toggleId}" class="rtoggle-label rtoggle-more">and {hidden.Count} more</label><label for="{toggleId}" class="rtoggle-label rtoggle-less">show less</label></div>
            """;
    }

    /// <summary>
    /// Turns "Name &lt;address&gt;" (or a bare address) into plain name text plus a real clickable
    /// mailto: link for just the address — the name isn't itself a link (there's nothing to select
    /// or copy about a display name), the address is, matching what someone reaches for when they
    /// click/select a recipient at all. Clicking it opens a new message addressed to them (see the
    /// mailto:// handling in ConfigureReadingPane).
    /// </summary>
    private static string FormatAddressLink(string rawAddress)
    {
        var address = MailText.AddressOnly(rawAddress);
        if (string.IsNullOrWhiteSpace(address))
            return Encode(rawAddress);

        var href = Encode("mailto:" + address);
        var addressLink = $"""<a class="qaddr" href="{href}">{Encode(address)}</a>""";

        var name = MailText.DisplayName(rawAddress);
        if (string.IsNullOrWhiteSpace(name) || name.Equals(address, StringComparison.OrdinalIgnoreCase))
            return addressLink;

        return $"{Encode(name)} &lt;{addressLink}&gt;";
    }

    /// <summary>Renders a whole conversation as separate Apple Mail-style cards — every message,
    /// including the one that was actually clicked, gets identical treatment: its own avatar,
    /// sender, to-line and date. The message actually opened leads (see GatherConversationAsync),
    /// then the rest of the thread newest-first below it. A message's own quoted/forwarded history
    /// (baked into its raw body, rather than a genuinely separate stored message) renders as-is,
    /// unfolded — see this method's own remarks below.</summary>
    /// <summary>A colored file-type badge for an attachment chip, Gmail/Apple-Mail style — every
    /// attachment used to show the same plain paperclip regardless of what it actually was, which
    /// read as "the app doesn't know/care what kind of file this is" (a photo and a spreadsheet
    /// looking identical). See AttachmentIconClassifier for the shared label/color mapping — the
    /// compose window's own attachment chips (WPF, not HTML) use the exact same one.</summary>
    private static string AttachmentIcon(string fileName)
    {
        var info = AttachmentIconClassifier.For(fileName);
        var tint = HexToRgba(info.ColorHex, 0.14);
        return $"""
            <span class="qaicon" style="background:{tint}">
              <svg viewBox="0 0 16 16" width="15" height="15"><path d="{info.PathData}" fill="none"
                stroke="{info.ColorHex}" stroke-width="1.15" stroke-linecap="round" stroke-linejoin="round"/></svg>
            </span>
            """;
    }

    /// <summary>"#RRGGBB" at the given opacity — the tinted-chip background every attachment icon
    /// sits on (~14% of the mark's own color), computed once here so ComposeWindow's WPF chips and
    /// these HTML ones stay derived from the exact same AttachmentIconClassifier colors instead of
    /// each hand-tuning their own tint.</summary>
    private static string HexToRgba(string hex, double alpha)
    {
        var h = hex.TrimStart('#');
        var r = Convert.ToInt32(h[..2], 16);
        var g = Convert.ToInt32(h[2..4], 16);
        var b = Convert.ToInt32(h[4..6], 16);
        return $"rgba({r},{g},{b},{alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
    }

    /// <summary>Matches a lone attribution line's own plain text — "On Mon, 7 Sept 2026 at 22:29,
    /// Jayesh Pillai &lt;jay@iitb.ac.in&gt; wrote:" — checked against one already-isolated DOM
    /// element's TextContent (StripDuplicatedQuote's own backward walk over boundary's preceding
    /// siblings), not scanned across raw HTML: TextContent already strips an embedded mailto
    /// &lt;a&gt; down to plain text, and the element boundary itself already bounds the match to one
    /// specific candidate rather than risking a stray "on" elsewhere in the message matching across
    /// a block boundary.</summary>
    private static readonly Regex AttributionLine = new(
        @"^On\b.{0,200}\bwrote:?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Removes a message's own trailing quoted/forwarded history — real DOM boundary
    /// detection (AngleSharp, the same engine HtmlSanitizer is built on) against the actual
    /// &lt;blockquote&gt;/&lt;hr&gt;/gmail_quote structure, not a regex over an English "On ... wrote:"
    /// phrase, since a lot of real mail doesn't use that exact wording (different locales, or none
    /// at all) — see BuildConversationHtml's own remarks for why this only ever runs when a sibling
    /// card is already showing the same content separately.</summary>
    private static string StripDuplicatedQuote(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;
        try
        {
            var parser = new AngleSharp.Html.Parser.HtmlParser();
            var document = parser.ParseDocument("<!doctype html><html><body></body></html>");
            var body = document.Body!;
            body.InnerHtml = html;

            // Outlook's own quote convention is completely different from blockquote — no wrapping
            // tag at all, just a plain <hr>, then a paragraph whose first line is a bold "From:"
            // label followed by Sent/To/Cc/Subject lines.
            static bool IsOutlookHeaderBlock(AngleSharp.Dom.IElement el)
            {
                var firstChild = el.Children.FirstOrDefault();
                if (firstChild is null || (firstChild.TagName != "B" && firstChild.TagName != "STRONG"))
                    return false;
                return firstChild.TextContent.Trim().TrimEnd(':').Equals("From", StringComparison.OrdinalIgnoreCase);
            }

            // The first real quote marker anywhere in the message — a <blockquote> (the
            // near-universal convention), Gmail's own gmail_quote wrapper class, or Outlook's own
            // <hr>/"From:" header block above — found via a genuine document-order scan, not text
            // matching. A match inside .qsig — a signature this app itself inserted — is skipped:
            // plenty of signature designs use a <blockquote> purely for a decorative left-indent,
            // nothing to do with quoting.
            var boundary = body.QuerySelectorAll("*")
                .Where(el => el.Closest(".qsig") is null)
                .FirstOrDefault(el => el.Matches("blockquote, .gmail_quote, .qcard, hr") || IsOutlookHeaderBlock(el));
            if (boundary is null)
                return html; // nothing to strip

            // The "On [date], [name] wrote:" attribution line is usually nested *inside* the same
            // wrapper as its blockquote — already part of boundary's own subtree, no special
            // handling needed. Some Gmail versions instead write it as an independent *preceding
            // sibling*, sharing no common wrapper at all; walking backward while a preceding
            // sibling's own text is just that attribution line absorbs it into what gets stripped
            // too, instead of leaving it stranded and visible on its own.
            while (boundary.PreviousElementSibling is { } prev
                   && prev.Closest(".qsig") is null
                   && AttributionLine.IsMatch(prev.TextContent.Trim()))
            {
                boundary = prev;
            }

            var node = (AngleSharp.Dom.INode)boundary;
            while (node is not null)
            {
                var next = node.NextSibling;
                node.Parent?.RemoveChild(node);
                node = next!;
            }

            return body.InnerHtml;
        }
        catch (Exception)
        {
            return html; // best-effort — worst case, this message's duplicate quote isn't stripped
        }
    }

    /// <summary>Strips genuinely empty trailing elements — a bare &lt;br&gt;, or a &lt;p&gt;/&lt;div&gt;/&lt;span&gt;
    /// containing nothing but whitespace/more &lt;br&gt;s and no &lt;img&gt; — from the very end of a
    /// message body. A signature built with a chain of blank spacer lines (common — visual line
    /// spacing instead of CSS margin) used to be swept up along with everything else by the
    /// quote-folding this app no longer does (see BuildConversationHtml's own remarks); without that,
    /// a card could end in a visible run of empty lines. Walks backward from the last node and stops
    /// at the first one that isn't provably empty, so nothing earlier in the message — including a
    /// deliberate blank line the sender left in the middle of their own text — is ever touched.</summary>
    private static string TrimTrailingBlankContent(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;
        try
        {
            var parser = new AngleSharp.Html.Parser.HtmlParser();
            var document = parser.ParseDocument("<!doctype html><html><body></body></html>");
            var body = document.Body!;
            body.InnerHtml = html;

            static bool IsBlank(AngleSharp.Dom.INode node) => node switch
            {
                AngleSharp.Dom.IText text => string.IsNullOrWhiteSpace(text.TextContent),
                AngleSharp.Dom.IElement { TagName: "BR" } => true,
                AngleSharp.Dom.IElement el => el.QuerySelector("img") is null && string.IsNullOrWhiteSpace(el.TextContent),
                _ => false,
            };

            var node = body.LastChild;
            while (node is not null && IsBlank(node))
            {
                var prev = node.PreviousSibling;
                node.Parent?.RemoveChild(node);
                node = prev;
            }

            return body.InnerHtml;
        }
        catch (Exception)
        {
            return html; // best-effort — worst case, the trailing blank lines just aren't trimmed
        }
    }

    /// <summary>
    /// Builds the whole conversation as a real DOM (AngleSharp), appending each message's card as
    /// a genuine sibling node — not by concatenating HTML strings together, which an earlier
    /// version of this method did. That distinction matters specifically because of what's
    /// untrusted here: a message's own body can be malformed (a genuinely unclosed tag some sender's
    /// mail client produced), and a final "re-parse the whole assembled document" pass — tried and
    /// found insufficient — only guarantees the *result* is well-formed, not that the unclosed
    /// tag's own auto-recovery didn't legally nest the *next* card inside it as a child, which is
    /// exactly what an unclosed element's own content model allows a spec-compliant parser to do.
    /// Setting one element's InnerHtml, by contrast, gives that string its own isolated parsing
    /// context scoped to that one element — nothing inside it can structurally reach outside its
    /// own subtree, no matter how malformed it is, so a card built this way physically cannot
    /// swallow a sibling the way a shared, concatenated parse could.
    /// </summary>
    private string BuildConversationHtml(List<(InboxRow Row, MessageDetail Detail)> conversation)
    {
        _conversationAttachments.Clear();
        _conversationImages.Clear();

        // Every sibling card in a conversation renders fully expanded, all at once — unlike real
        // Apple Mail, where a thread's other messages sit collapsed as summary bars until you click
        // one open, so at most a couple of message bodies are ever visible together. A live report
        // showed exactly what removing all deduplication (an earlier attempt at "matching Apple",
        // which really only matched the single-message case that prompted it) does under THIS app's
        // own always-expanded-cards design: each reply's own embedded copy of the earlier thread
        // duplicates across every card, and that duplication compounds with each additional reply —
        // "a lot of repeated mails... some are just as cards and some are lengthy threads within
        // card". Stripping a message's own trailing quote when a sibling card already shows that
        // same content separately is what actually prevents that, and only runs here — when there's
        // more than one card on screen at once. A standalone message (conversation.Count == 1) still
        // renders its own quoted history completely unfolded, which is the part of "do it like
        // Apple" that does hold regardless of how many cards are showing.
        var suppressDuplicateQuotes = conversation.Count > 1;

        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var document = parser.ParseDocument("<!doctype html><html><body></body></html>");
        var container = document.Body!;

        foreach (var (msgRow, msgDetail) in conversation)
        {
            var rawBody = SanitizeHtmlForDisplay(msgDetail.BodyHtml);
            if (suppressDuplicateQuotes)
                rawBody = StripDuplicatedQuote(rawBody);
            // A message whose entire content was quoted history — a bare forward with nothing of
            // its own added — legitimately renders as nothing once StripDuplicatedQuote removes
            // that duplicate quote. Left as-is, that showed as a card with a sender/date header and
            // a blank void underneath, reading as broken rather than as "there's genuinely nothing
            // more here than what's already shown elsewhere."
            if (suppressDuplicateQuotes
                && System.Text.RegularExpressions.Regex.Replace(rawBody, "<[^>]+>", "").Trim().Length == 0
                && !rawBody.Contains("<img", StringComparison.OrdinalIgnoreCase))
                rawBody = """<p style="color:#999;font-style:italic;">(No additional text — see the quoted message above)</p>""";
            var body = WrapClickableImages(TrimTrailingBlankContent(rawBody), msgRow.Id);
            var toLine = BuildRecipientLine("To", msgDetail.To);
            var ccLine = BuildRecipientLine("Cc", msgDetail.Cc);
            var senderLink = FormatAddressLink(msgDetail.From);
            var attachments = WithRealSizes(msgDetail.Attachments ?? []);
            var attachmentsHtml = "";
            if (attachments.Count > 0)
            {
                _conversationAttachments[msgRow.Id] = attachments;
                var chips = new System.Text.StringBuilder();
                for (var i = 0; i < attachments.Count; i++)
                {
                    var a = attachments[i];
                    chips.Append($"""
                        <a class="qattach" href="appattach://open?id={Uri.EscapeDataString(msgRow.Id)}&idx={i}">
                          {AttachmentIcon(a.Name)}{Encode(a.Name)}<span class="qasize">{Encode(a.Size)}</span>
                        </a>
                        """);
                }
                var downloadAll = attachments.Count > 1
                    ? $"""<a class="qattach-all" href="appattach://downloadall?id={Uri.EscapeDataString(msgRow.Id)}">Download all {attachments.Count}</a>"""
                    : "";
                attachmentsHtml = $"""<div class="qattachments">{chips}{downloadAll}</div>""";
            }
            // Apple Mail's own in-thread convention: a flagged message keeps its star visible on
            // its own card, and an unread one keeps its blue dot, rather than that state only ever
            // showing in the message list and disappearing the moment you're looking at the thread.
            var unreadDot = msgRow.Unread ? """<span class="qunread" title="Unread"></span>""" : "";
            var star = msgRow.Starred ? """<span class="qstar" title="Starred">&#9733;</span>""" : "";

            // The shell (avatar/name/to/cc/date/attachments) is all content this app built itself
            // — trusted, safe to parse as one InnerHtml assignment. The message body is the one
            // untrusted piece, so it's deliberately left out here and set on .qbody separately
            // below, in its own isolated parsing scope (see this method's own remarks).
            var qcard = document.CreateElement("div");
            qcard.ClassName = "qcard";
            qcard.InnerHtml = $"""
                <div class="qhead">
                  <div class="qavatar">{Encode(msgRow.Initial)}</div>
                  <div class="qwho">
                    <div class="qname">{unreadDot}{senderLink}</div>
                    {toLine}
                    {ccLine}
                  </div>
                  <div class="qdate">{star}{Encode(msgDetail.Date)}</div>
                </div>
                <div class="qbody"></div>
                {attachmentsHtml}
                """;
            container.AppendChild(qcard);
            qcard.QuerySelector(".qbody")!.InnerHtml = body;
        }
        return container.InnerHtml;
    }

    /// <summary>
    /// Where attachments are materialised before being shown. Kept out of the user's Downloads
    /// folder — opening a mail shouldn't quietly litter it with files.
    /// </summary>
    private static string AttachmentCacheDirectory => Path.Combine(AppDataPaths.Root, "attachments");

    /// <summary>
    /// Gets a real file on disk for an attachment: generated for sample data, downloaded via IMAP
    /// for live mail. Returns null when it couldn't be obtained.
    /// </summary>
    private async Task<string?> ResolveAttachmentAsync(MailAttachment attachment)
    {
        if (UseMockData || string.IsNullOrEmpty(attachment.Url))
            return SampleAttachments.EnsureFile(AttachmentCacheDirectory, attachment.Name);

        // Keyed by a per-message subfolder (attachment.Url is "imap:{uid}:{index}"), not just the
        // filename — otherwise two different emails with a same-named attachment (e.g. "invoice.pdf")
        // would silently overwrite each other's cached file.
        var messageDir = Path.Combine(AttachmentCacheDirectory, SampleAttachments.SafeName(attachment.Url.Replace(':', '_')));
        Directory.CreateDirectory(messageDir);
        var path = Path.Combine(messageDir, SampleAttachments.SafeName(attachment.Name));

        // The exact same attachment, opened a second time in the same session (or a prior one —
        // this cache is never cleared) — a live report traced a PDF stuck on "Downloading…" for
        // close to a minute partly to this never being checked at all, so even re-opening the
        // identical file paid a full re-download every single time.
        if (File.Exists(path))
            return path;

        StatusText.Text = $"Downloading {attachment.Name}…";
        // System.Progress<T> (not a raw callback) captures the caller's own SynchronizationContext
        // at construction — since this runs on the UI thread, its Report calls land back on the UI
        // thread automatically, no manual Dispatcher.Invoke needed to touch StatusText from them.
        var progress = new Progress<int>(percent => StatusText.Text = $"Downloading {attachment.Name}… {percent}%");
        return await _mail!.DownloadAttachmentAsync(attachment, path, progress);
    }

    /// <summary>
    /// Copies files picked in the compose window into the attachment cache, so a message that has
    /// been "sent" on sample data can still open its own attachments afterwards.
    /// </summary>
    private IReadOnlyList<MailAttachment> StageAttachments(IReadOnlyList<ComposeAttachment> files)
    {
        if (files.Count == 0)
            return [];

        var staged = new List<MailAttachment>(files.Count);
        try
        {
            Directory.CreateDirectory(AttachmentCacheDirectory);
        }
        catch (Exception)
        {
            // Fall through: the chips are still worth showing even if the copy can't be made.
        }

        foreach (var file in files)
        {
            try
            {
                File.Copy(file.Path,
                    Path.Combine(AttachmentCacheDirectory, SampleAttachments.SafeName(file.Name)),
                    overwrite: true);
            }
            catch (Exception)
            {
                // Keep the chip; opening it will fall back to a generated sample file.
            }
            staged.Add(new MailAttachment(file.Name, file.Size));
        }
        return staged;
    }

    /// <summary>
    /// Sample attachments are generated on demand, so their declared size is whatever the sample
    /// data happened to claim. Replacing it with the real file size keeps the chip honest.
    /// </summary>
    private IReadOnlyList<MailAttachment> WithRealSizes(IReadOnlyList<MailAttachment> attachments)
    {
        if (!UseMockData)
            return attachments;

        var sized = new List<MailAttachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            try
            {
                var path = SampleAttachments.EnsureFile(AttachmentCacheDirectory, attachment.Name);
                sized.Add(attachment with { Size = AttachmentViewerWindow.FormatSize(new FileInfo(path).Length) });
            }
            catch (Exception)
            {
                sized.Add(attachment);
            }
        }
        return sized;
    }

    /// <summary>
    /// Routes an appattach://open?id=..&idx=.. or appattach://downloadall?id=.. link click (see
    /// BuildConversationHtml) back to a real attachment on a real message.
    /// </summary>
    private async void HandleAttachmentLink(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return;

        var query = parsed.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        if (!query.TryGetValue("id", out var id) || !_conversationAttachments.TryGetValue(id, out var attachments))
            return;

        if (parsed.Host.Equals("downloadall", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadAllAttachmentsAsync(attachments);
            return;
        }

        if (parsed.Host.Equals("open", StringComparison.OrdinalIgnoreCase)
            && query.TryGetValue("idx", out var idxStr) && int.TryParse(idxStr, out var idx)
            && idx >= 0 && idx < attachments.Count)
        {
            await OpenAttachmentAsync(attachments[idx]);
        }
    }

    /// <summary>
    /// Opens the attachment inside the app. Saving is still available, but from the viewer —
    /// clicking a PDF should show you the PDF, not immediately ask where to put it.
    /// </summary>
    private async Task OpenAttachmentAsync(MailAttachment attachment)
    {
        try
        {
            var path = await ResolveAttachmentAsync(attachment);
            if (path is null)
            {
                // DownloadAttachmentAsync itself now logs the actual reason (a live report of some,
                // not all, attachments failing to open with no way to tell why) — this just records
                // which attachment/message hit it, to correlate with that.
                Log.Warn($"Couldn't resolve attachment '{attachment.Name}' (url={attachment.Url})");
                StatusText.Text = $"Couldn't download {attachment.Name}";
                return;
            }

            StatusText.Text = $"Opened {attachment.Name}";

            // Images get a small floating Quick Look-style panel instead of a full document
            // window — everything else (PDF, text) still goes to AttachmentViewerWindow, since a
            // plain Image control can't render those and Edge's own PDF viewer is already the
            // better experience for that one anyway.
            if (QuickLookWindow.CanPreview(attachment.Name))
                new QuickLookWindow(path, attachment.Name) { Owner = this }.Show();
            else
                new AttachmentViewerWindow(path, attachment.Name) { Owner = this }.Show();
        }
        catch (SessionExpiredException)
        {
            StatusText.Text = "Signed out — sign in again to continue";
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening attachment '{attachment.Name}' failed", ex);
            StatusText.Text = $"Couldn't open {attachment.Name}: {ex.Message}";
        }
    }

    /// <summary>Saves every attachment on a message into one folder the user picks — a thread with
    /// several files (project data + notes, say) otherwise means clicking Save on each one
    /// individually.</summary>
    private async Task DownloadAllAttachmentsAsync(IReadOnlyList<MailAttachment> attachments)
    {
        if (attachments.Count == 0)
            return;

        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a folder to save attachments to" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        var saved = 0;
        foreach (var attachment in attachments)
        {
            try
            {
                var sourcePath = await ResolveAttachmentAsync(attachment);
                if (sourcePath is null)
                    continue;
                File.Copy(sourcePath, Path.Combine(dialog.SelectedPath, SampleAttachments.SafeName(attachment.Name)), overwrite: true);
                saved++;
            }
            catch (Exception)
            {
                // Best-effort — one bad attachment shouldn't abort the rest of the batch.
            }
        }

        StatusText.Text = saved > 0 ? $"Saved {saved} attachment(s) to {dialog.SelectedPath}" : "Couldn't save attachments";
    }

    // A weekday name or "today"/"tomorrow" paired with a clock time ("Tuesday ... 5 PM") is enough
    // to build a real-looking "when" line without needing to actually parse a calendar invite —
    // used only as a fallback for a plain message that has no real text/calendar part to read.
    private static readonly System.Text.RegularExpressions.Regex MeetingDayPattern = new(
        @"\b(today|tomorrow|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex MeetingTimePattern = new(
        @"\b\d{1,2}(:\d{2})?\s?(AM|PM|am|pm)\b");
    // Zoom's standard invite text always has a "Topic: <name>" line; Teams/Meet link-only invites
    // (no .ics) rarely label a title this explicitly, so this is Zoom-specific rather than a
    // general heuristic — a mail subject is still the best guess for the others.
    private static readonly System.Text.RegularExpressions.Regex MeetingTopicPattern = new(
        @"Topic:\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Scans the raw (pre-sanitize) message HTML for a Zoom/Meet/Teams join link, so the reading
    /// pane can surface a Gmail-style meeting card instead of just saying "this message contains a
    /// link". Checked against the unsanitized HTML since the link only ever lives in an href/text,
    /// neither of which SanitizeRemoteImages touches — but running it first keeps this independent
    /// of that step's behavior. Only used when the message has no real calendar invite to read
    /// (see MessageDetail.Calendar) — that's always the more accurate source when present.
    /// </summary>
    private static (string Label, string Url, string? When, string? Title)? FindMeetingLink(string html)
    {
        if (Automation.MeetingLinkFinder.Find(html) is not (var label, var url))
            return null;

        var plainText = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        var day = MeetingDayPattern.Match(plainText);
        var time = MeetingTimePattern.Match(plainText);
        string? when = (day.Success, time.Success) switch
        {
            (true, true) => $"{Capitalize(day.Value)}, {time.Value.ToUpperInvariant()}",
            (false, true) => time.Value.ToUpperInvariant(),
            (true, false) => Capitalize(day.Value),
            _ => null,
        };

        var topicMatch = MeetingTopicPattern.Match(plainText);
        var title = topicMatch.Success
            ? System.Net.WebUtility.HtmlDecode(topicMatch.Groups[1].Value.Trim())
            : null;

        return (label, url, when, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private void JoinMeetingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_meetingLinkUrl is null)
            return;
        // Same http(s)-only guard as every other sender-controlled link — a meeting link is text
        // pulled straight out of the message body, so nothing stops a hostile sender putting an
        // arbitrary URI scheme there for the shell to hand off to whatever handler claims it.
        if (!OpenInDefaultBrowser(_meetingLinkUrl))
            StatusText.Text = "Couldn't open the meeting link";
    }

    /// <summary>
    /// The URL form needs nothing more than a browser tab — preferred over the mailto: form when a
    /// sender offers both, since that one still requires composing and sending a message. No
    /// signature on the mailto fallback (unlike a normal new message) — an unsubscribe request
    /// isn't correspondence, and a signature block only adds noise a mailing list's parser has to
    /// ignore.
    /// </summary>
    private void UnsubscribeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_unsubscribeUrl is not null)
        {
            // Same http(s)-only guard as the meeting link and every other sender-controlled URL —
            // see OpenInDefaultBrowser.
            if (!OpenInDefaultBrowser(_unsubscribeUrl))
                StatusText.Text = "Couldn't open the unsubscribe link";
        }
        else if (_unsubscribeMailto is not null)
        {
            var address = Uri.UnescapeDataString(_unsubscribeMailto["mailto:".Length..].Split('?')[0]);
            if (!string.IsNullOrWhiteSpace(address))
                OpenCompose(address, subject: "Unsubscribe");
        }
    }

    /// <summary>Reflects whether the open message's sender is currently VIP — filled/gold star and
    /// "Remove VIP" when they are, outline star and "Mark as VIP" when they aren't. Matches
    /// StarToggle's own filled-vs-outline convention in the message list.</summary>
    private void UpdateVipToggleUi(string senderAddress)
    {
        var isVip = !string.IsNullOrEmpty(senderAddress) && InboxRow.VipSenders.Contains(senderAddress);
        VipToggleGlyph.Text = char.ConvertFromUtf32(isVip ? 0xE735 : 0xE734); // filled/outline star (see comment above)
        VipToggleGlyph.Foreground = isVip
            ? (System.Windows.Media.Brush)FindResource("Warning")
            : (System.Windows.Media.Brush)FindResource("TextBody");
        VipToggleButton.ToolTip = isVip ? "Remove Important" : "Mark sender as Important";
    }

    /// <summary>Toggles VIP status for the open message's sender — everywhere that address shows up
    /// in the message list, not just this one message (see InboxRow.IsVip).</summary>
    private void VipToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRow is not { SenderAddress.Length: > 0 } row)
            return;

        // Case-insensitive removal, matching InboxRow.VipSenders' own comparer below — List.Remove
        // is case-sensitive by default, which could otherwise fail to find/remove an address that
        // was stored with different casing than the one this exact message happens to carry,
        // leaving the toggle stuck "on" with no visible way to turn it back off.
        var existingIndex = _settings.VipSenders.FindIndex(
            a => a.Equals(row.SenderAddress, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
            _settings.VipSenders.RemoveAt(existingIndex);
        else
            _settings.VipSenders.Add(row.SenderAddress);
        _settings.Save();
        SyncVipSenders();
        UpdateVipToggleUi(row.SenderAddress);
        _messagesView.Refresh();
    }

    /// <summary>
    /// Shown the instant a row is clicked, before the async IMAP fetch for its full body even
    /// starts — the header (subject/sender/date/avatar) is already known from the row itself, so it
    /// updates immediately instead of lagging behind the list selection. Only the body has to wait,
    /// and shows an explicit "Loading…" placeholder rather than the *previous* message's content,
    /// which otherwise stays on screen long enough to look like the wrong mail opened.
    /// </summary>
    private async Task MarkReadInBackgroundAsync(string id)
    {
        try
        {
            await _mail!.SetReadAsync(id, true);
            // Written straight through rather than waiting for MailSyncEngine's next sweep — this
            // is the single most frequent local write there is (every message open), and the local
            // database should reflect it immediately, not up to a minute later.
            _mailStore?.UpdateFlags(SpecialMailbox(_currentFolder), id, unread: false);
        }
        catch (Exception)
        {
            // Reading the message still worked; a failed read-flag isn't worth surfacing here.
        }
    }

    // Bumped on every call so a slow lookup for a message the user has already navigated away
    // from can't land its result on top of whatever is showing now.
    private int _readingAvatarToken;

    private void UpdateReadingAvatar(string senderAddress)
    {
        ReadingAvatarImage.Visibility = Visibility.Collapsed;
        ReadingAvatarBadge.Visibility = Visibility.Collapsed;
        var token = ++_readingAvatarToken;
        // See ShowDomainAvatars' own remarks (MainWindow.Folders.cs) — turned off per feedback.
        if (!ShowDomainAvatars || UseMockData || string.IsNullOrEmpty(senderAddress))
            return;

        _ = AvatarService.GetAvatarAsync(senderAddress).ContinueWith(t =>
        {
            if (token != _readingAvatarToken || t.Result is not { } result)
                return;
            ReadingAvatarImage.Fill = new ImageBrush(result.Image) { Stretch = Stretch.UniformToFill };
            ReadingAvatarImage.Visibility = Visibility.Visible;
            ReadingAvatarBadge.Visibility = result.Verified ? Visibility.Visible : Visibility.Collapsed;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowReadingPaneLoading(InboxRow row)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        if (ReadingCard.Visibility != Visibility.Visible)
            ReadingCard.FadeIn(180);
        else
            ReadingCard.Visibility = Visibility.Visible;

        ReadingSubject.Text = row.Subject;
        ReadingFrom.Text = row.Sender;
        ReadingDate.Text = row.Date;
        ReadingAvatarInitial.Text = row.Initial;
        UpdateReadingAvatar(row.SenderAddress);
        ReadingFromRow.Visibility = Visibility.Collapsed;
        ExternalSenderBar.Visibility = Visibility.Collapsed;
        MeetingBar.Visibility = Visibility.Collapsed;
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
        UnsubscribeBar.Visibility = Visibility.Collapsed;
        JoinMeetingButton.Visibility = Visibility.Visible;

        // A native overlay instead of a placeholder WebView2 navigation — navigating here just to
        // navigate again moments later once the real content arrives doubled WebView2's per-open
        // engine overhead for no visible benefit, which is what made opening a message feel slower
        // right after this loading state was added.
        ReadingPaneLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void ResetReadingPane()
    {
        _openRow = null;
        _openDetail = null;
        _blockedImagesHtml = null;
        _meetingLinkUrl = null;
        _unsubscribeUrl = null;
        _unsubscribeMailto = null;
        _conversationAttachments.Clear();
        _conversationImages.Clear();
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
        ExternalSenderBar.Visibility = Visibility.Collapsed;
        MeetingBar.Visibility = Visibility.Collapsed;
        UnsubscribeBar.Visibility = Visibility.Collapsed;
        JoinMeetingButton.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;
        ReadingCard.Visibility = Visibility.Collapsed;
        MessageList.SelectedItem = null;
    }

}
