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
        /* Apple Mail's own "•••" fold: a message's own trailing quoted history starts collapsed,
           since that same content is almost always already visible as its own separate card
           earlier in the same conversation — showing it again, expanded, by default is what caused
           it to visibly duplicate. A pure-CSS checkbox toggle, since script is disabled. */
        .qtoggle-cb { display: none; }
        .qtoggle-content { display: none; margin-top: 8px; }
        .qtoggle-cb:checked + .qtoggle-label + .qtoggle-content { display: block; }
        .qtoggle-label { display: inline-block; cursor: pointer; color: #6d28d9; background: #f4f4f6;
          border-radius: 12px; padding: 3px 12px; font-size: 13px; letter-spacing: 1px; margin-top: 6px; }
        .qtoggle-label:hover { background: #ebebee; }
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

    /// <summary>
    /// Matches one "leaf" quoted message: an attribution line ("On ... wrote:") immediately
    /// followed by a &lt;blockquote&gt; whose content contains no further blockquote tags. Applied
    /// repeatedly, this always resolves the innermost (oldest) quote first, so a chain nested N
    /// levels deep — exactly what a real IMAP reply-to-a-reply produces — unwraps one round at a
    /// time regardless of depth.
    /// </summary>
    // The wrapping structure around the attribution line varies by mail client — Gmail alone wraps
    // it in two or three nested <div>s (a "gmail_quote_container", then a "gmail_attr" div, plus a
    // trailing <br> before the next tag), Roundcube uses a single bare line or one <div>/<p> — so
    // rather than expecting exactly one wrapper (or matching by exact tag-pair), this tolerates any
    // run of up to a few div/p/br tags (open or close) on either side of the attribution text.
    //
    // The attribution text itself is matched lazily up to the literal word "wrote:" rather than
    // excluding '<' characters — Gmail's own format embeds the sender's address as a clickable
    // mailto: link right inside the line ("Head IDC &lt;<a href="mailto:...">...</a>&gt; wrote:"),
    // and excluding '<' entirely meant that embedded tag broke the match before it ever reached
    // "wrote:", so the quote was never recognized as one at all — not even collapsed, just missed.
    //
    // The second guard — refusing to cross into a new <div>/<p>/<br> — is just as essential: without
    // it, the literal word "on" appearing as a substring inside completely unrelated body text (e.g.
    // "...participating in the Convocati[on] and Departmental...") would itself satisfy "On", and the
    // lazy match would then happily stretch all the way to some genuine "wrote:" much later in the
    // same message, swallowing the sender's own new text as if it were quoted history. A real
    // attribution is always one short inline run of text, never spanning a block boundary — inline
    // tags like the mailto <a> above are fine to cross, a paragraph/div break is not.
    private static readonly Regex AttributedQuote = new(
        "(?:</?(?:p|div|br)\\b[^>]*>\\s*){0,6}(On (?:(?!wrote:)(?!</?(?:div|p|br)\\b).)*?wrote:)(?:\\s*</?(?:p|div|br)\\b[^>]*>){0,6}\\s*<blockquote\\b[^>]*>((?:(?!</?blockquote\\b).)*)</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // The other convention: the attribution line as the blockquote's own first paragraph
    // (<blockquote><p>On ... wrote:</p>...</blockquote>) rather than preceding it — what a raw
    // IMAP reply chain typically looks like, as opposed to this app's own BuildReplyBodyHtml.
    private static readonly Regex AttributedQuoteInside = new(
        "<blockquote\\b[^>]*>\\s*(?:</?(?:p|div|br)\\b[^>]*>\\s*){0,6}(On (?:(?!wrote:)(?!</?(?:div|p|br)\\b).)*?wrote:)(?:\\s*</?(?:p|div|br)\\b[^>]*>){0,6}((?:(?!</?blockquote\\b).)*)</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Turns a flat "quoted history" body — the nested blockquotes an IMAP reply chain (or this
    /// app's own reply/forward) accumulates — into Apple Mail-style separated cards: each earlier
    /// message becomes its own bordered, shadowed card with the "On ... wrote:" line as a header
    /// strip, fully visible rather than collapsed — the same always-expanded, clearly-bounded look
    /// Apple Mail's conversation view uses, just without needing per-message metadata this app
    /// doesn't have (each "message" here is really one HTML blob with quoted history baked in, not
    /// separate stored messages).
    /// </summary>
    // The other common forward convention (Roundcube/Outlook-style): a dashed marker line
    // ("-------- Original Message --------" / "---------- Forwarded message ----------") followed
    // by labeled Subject/Date/From/To/Cc header lines and then the forwarded body, with no
    // blockquote at all — so AttributedQuote/AttributedQuoteInside never match it. Whatever follows
    // the marker is everything there is (a forward is always the last thing in the message), so
    // this matches greedily to the end rather than needing its own closing delimiter.
    private static readonly Regex ForwardMarker = new(
        "<p>\\s*-{2,}\\s*(?:Original Message|Forwarded message)\\s*-{2,}\\s*</p>(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Unique across the whole reading pane, not just one message — several cards in the same
    // conversation each producing their own toggle would otherwise collide on the same checkbox
    // id and cross-wire each other's expand/collapse state.
    private static int _quoteToggleSeq;

    private static string SeparateQuotedThread(string html, bool suppressNestedQuotes = false)
    {
        string previous;
        do
        {
            previous = html;
            html = AttributedQuote.Replace(html, m =>
                $"<div class=\"qcard\"><div class=\"qhead\">{m.Groups[1].Value}</div>"
                + $"<div class=\"qbody\">{m.Groups[2].Value}</div></div>");
            html = AttributedQuoteInside.Replace(html, m =>
                $"<div class=\"qcard\"><div class=\"qhead\">{m.Groups[1].Value}</div>"
                + $"<div class=\"qbody\">{m.Groups[2].Value}</div></div>");
        } while (html != previous);

        var forward = ForwardMarker.Match(html);
        // Only hoist a forward marker that's still at the top level — one nested inside a quote
        // that AttributedQuote already turned into its own qcard above is a different case: "match
        // to end of string" would swallow that qcard's own closing tags into the new one, corrupting
        // both. Left alone, it just renders as ordinary nested content inside the existing qcard —
        // still folded under the same collapse toggle, so nothing is lost, just not double-carded.
        if (forward.Success && !html[..forward.Index].Contains("<div class=\"qcard\">", StringComparison.Ordinal))
        {
            html = html[..forward.Index]
                + $"<div class=\"qcard\"><div class=\"qhead\">Forwarded message</div>"
                + $"<div class=\"qbody\">{forward.Groups[1].Value}</div></div>";
        }

        // Everything from the first quote card to the end of the message IS the quoted history —
        // nothing meaningful ever follows it in a normal reply/forward — so folding from there
        // onward is enough, without needing to track each nesting level separately.
        var firstQuote = html.IndexOf("<div class=\"qcard\">", StringComparison.Ordinal);
        if (firstQuote >= 0)
        {
            if (suppressNestedQuotes)
            {
                // A sibling message elsewhere in this same conversation already shows this exact
                // content as its own real card — including it again here, even collapsed behind a
                // toggle, is a guaranteed duplicate rather than a possible one, so it's dropped
                // entirely instead of folded.
                html = html[..firstQuote];
            }
            else
            {
                var toggleId = $"qtoggle{System.Threading.Interlocked.Increment(ref _quoteToggleSeq)}";
                html = html[..firstQuote]
                    + $"""<input type="checkbox" id="{toggleId}" class="qtoggle-cb">"""
                    + $"""<label for="{toggleId}" class="qtoggle-label">&#8226;&#8226;&#8226;</label>"""
                    + $"""<div class="qtoggle-content">{html[firstQuote..]}</div>""";
            }
        }

        return html;
    }

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
    /// Locks down the reading pane's WebView2 once, at startup: no script execution at all (mail
    /// HTML has zero legitimate need for it), no DevTools, and every link click or attempted popup
    /// is redirected to the OS default browser instead of navigating in place.
    /// </summary>
    private void ConfigureReadingPane()
    {
        var core = ReadingPane.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenInDefaultBrowser(e.Uri);
        };
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

    /// <returns>False when the link was rejected (not a well-formed http/https URL) or the OS
    /// couldn't hand it to a browser — callers that show status text use this to say so, rather
    /// than the click silently doing nothing.</returns>
    private static bool OpenInDefaultBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            // Nothing sensible to do if the OS can't hand the link to a browser.
            return false;
        }
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
            return $"{m.Groups[1].Value}{TransparentPixel}{m.Groups[3].Value}";
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
    private void NavigateReadingPane(string html)
    {
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

    // Once a real message body has actually been fetched over IMAP, keep it — reopening the same
    // mail later in the session is then a free in-memory lookup instead of another network round
    // trip. Never evicted: a folder page tops out at 50-ish rows, so worst case this is a few
    // hundred cached bodies over a long session, not an unbounded leak.
    private readonly Dictionary<string, MessageDetail> _messageDetailCache = new();

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
        ShowReadingPaneLoading(row);

        MessageDetail? detail;
        var wasCached = _localBodies.ContainsKey(row.Id) || _messageDetailCache.ContainsKey(row.Id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            detail = _localBodies.GetValueOrDefault(row.Id)
                ?? _messageDetailCache.GetValueOrDefault(row.Id)
                ?? (UseMockData
                    ? MockData.MessageBodies.GetValueOrDefault(row.Id)
                    : await _mail!.OpenMessageAsync(row.Id));
            // A message read once stays readable with no connection. Deliberately not gated on
            // UseMockData: a failed sign-in leaves that true, and that is precisely the case this
            // fallback exists for. _cache is null until an account is known, so genuine sample-data
            // mode can't reach it.
            detail ??= _cache?.LoadDetail(row.Id);

            if (!UseMockData && detail is not null)
            {
                _messageDetailCache[row.Id] = detail;
                _cache?.SaveDetail(row.Id, detail);
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
        // Temporary diagnostic — remove once we've confirmed where the time actually goes. Not
        // gated behind UseMockData: this is exactly the number we need from a real account.
        var fetchMs = sw.ElapsedMilliseconds;

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
        if (!UseMockData && wasUnread)
        {
            try
            {
                await _mail!.SetReadAsync(row.Id, true);
            }
            catch (Exception)
            {
                // Reading the message still worked; a failed read-flag isn't worth blocking on.
            }
        }

        _openRow = UpdateRow(row.Id, r => r with { Unread = false }) ?? row;
        _openDetail = detail;
        // Opening an unread message is common enough that even the brief round trip
        // UpdateInboxBadge's own refresh takes would read as the count lagging behind what the
        // reading pane already shows — so it also drops by one immediately here. The refresh that
        // follows still reconciles against the server's actual count regardless.
        if (!UseMockData && wasUnread
            && int.TryParse(InboxUnreadCount.Text, out var shownUnread) && shownUnread > 0)
            SetInboxBadge(shownUnread - 1);
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
            var conversation = await GatherConversationAsync(_openRow, detail);
            if (requestId != _openRequestSeq)
                return;
            cleanHtml = FlagMismatchedLinks(BuildConversationHtml(conversation));

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

        if (!UseMockData)
            StatusText.Text = $"{(wasCached ? "Cache hit" : "Live fetch")} — {fetchMs}ms";
    }

    /// <summary>
    /// Finds every other message in the same conversation (subject with reply/forward prefixes
    /// stripped) — across every folder for sample data, matching Apple Mail's default of including
    /// related messages from other mailboxes, or across the *whole current folder* for a live
    /// account (not just whatever page happens to be loaded — a reply from months ago still needs
    /// to thread in, the same as Apple Mail does regardless of pagination). Returns newest-first
    /// (Apple Mail's own conversation-view default), opened message included.
    /// </summary>
    private async Task<List<(InboxRow Row, MessageDetail Detail)>> GatherConversationAsync(InboxRow row, MessageDetail openedDetail)
    {
        var key = row.ConversationKey;
        var results = new List<(InboxRow Row, MessageDetail Detail)> { (row, openedDetail) };
        if (string.IsNullOrWhiteSpace(key))
            return results;

        IEnumerable<InboxRow> siblings;
        if (UseMockData)
        {
            siblings = _folderData.Values.SelectMany(rows => rows)
                .Where(r => r.Id != row.Id && r.ConversationKey == key)
                .GroupBy(r => r.Id).Select(g => g.First())
                .Take(8);
        }
        else
        {
            try { siblings = await _mail!.FindConversationSiblingsAsync(key, row.Id, openedDetail.MessageId, openedDetail.References); }
            catch (Exception) { siblings = []; } // best-effort — a lookup failure still shows the opened message alone
        }

        foreach (var sibling in siblings)
        {
            MessageDetail? siblingDetail = _localBodies.GetValueOrDefault(sibling.Id)
                ?? (UseMockData ? MockData.MessageBodies.GetValueOrDefault(sibling.Id) : null);

            if (siblingDetail is null && !UseMockData)
            {
                try { siblingDetail = await _mail!.OpenMessageAsync(sibling.Id); }
                catch (Exception) { /* best-effort — a conversation missing one sibling still shows the rest */ }
            }

            if (siblingDetail is not null)
                results.Add((sibling, siblingDetail));
        }

        // The message actually opened always leads, regardless of where it falls chronologically
        // in the thread — sorting purely newest-first (the old behaviour) meant opening an older
        // message in a thread that already has a later reply (a search hit, or clicking an older
        // row directly) buried the very message just opened — its attachments included — under
        // that reply's card instead of showing it. The rest of the thread still reads newest-first
        // below it, and this is a no-op for the common case of opening the newest message in a
        // thread, since that's already what "leads" would mean anyway.
        var opened = results[0];
        var rest = results.Skip(1).OrderByDescending(r => r.Row.Timestamp ?? DateTime.MinValue);
        return [opened, .. rest];
    }

    /// <summary>
    /// A To/Cc line that actually lets you see who's hidden behind "and N more" — a plain collapsed
    /// summary with no way to expand it is a dead end once a list is long enough to need collapsing
    /// in the first place. Same checkbox-toggle trick as the quote fold, styled inline.
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
    /// then the rest of the thread newest-first below it. See SeparateQuotedThread for the
    /// single-message equivalent (a quoted-history blob baked into one message, rather than
    /// genuinely separate stored messages).</summary>
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

    private string BuildConversationHtml(List<(InboxRow Row, MessageDetail Detail)> conversation)
    {
        _conversationAttachments.Clear();
        _conversationImages.Clear();

        // A real earlier message in this same conversation already gets its own full card below —
        // that same content is almost always also baked into a later reply's own raw body as
        // quoted history, so showing it there too (even collapsed) is a guaranteed duplicate, not
        // a maybe. Only when a message has no siblings here (conversation.Count == 1) is its own
        // quoted history the only place that content exists, so it's kept (collapsed) in that case.
        var suppressNestedQuotes = conversation.Count > 1;

        var sb = new System.Text.StringBuilder();
        foreach (var (msgRow, msgDetail) in conversation)
        {
            var body = WrapClickableImages(
                SeparateQuotedThread(SanitizeHtmlForDisplay(msgDetail.BodyHtml), suppressNestedQuotes), msgRow.Id);
            // A message whose entire content was quoted history — a bare forward with nothing of
            // its own added — legitimately renders as nothing once suppressNestedQuotes strips that
            // duplicate quote (see the comment above). Left as-is, that showed as a card with a
            // sender/date header and a blank void underneath, reading as broken rather than as
            // "there's genuinely nothing more here than what's already shown elsewhere."
            if (System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", "").Trim().Length == 0
                && !body.Contains("<img", StringComparison.OrdinalIgnoreCase))
                body = """<p style="color:#999;font-style:italic;">(No additional text — see the quoted message above)</p>""";
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
            sb.Append($"""
                <div class="qcard">
                  <div class="qhead">
                    <div class="qavatar">{Encode(msgRow.Initial)}</div>
                    <div class="qwho">
                      <div class="qname">{senderLink}</div>
                      {toLine}
                      {ccLine}
                    </div>
                    <div class="qdate">{Encode(msgDetail.Date)}</div>
                  </div>
                  <div class="qbody">{body}</div>
                  {attachmentsHtml}
                </div>
                """);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Where attachments are materialised before being shown. Kept out of the user's Downloads
    /// folder — opening a mail shouldn't quietly litter it with files.
    /// </summary>
    private static string AttachmentCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IITBWebmailWrapper", "attachments");

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

        StatusText.Text = $"Downloading {attachment.Name}…";
        return await _mail!.DownloadAttachmentAsync(attachment, path);
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
            : (System.Windows.Media.Brush)FindResource("BorderStrong");
        VipToggleButton.ToolTip = isVip ? "Remove VIP" : "Mark sender as VIP";
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
