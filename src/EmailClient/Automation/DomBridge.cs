using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailClient.Automation;

public sealed record InboxRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("unread")] bool Unread,
    [property: JsonPropertyName("starred")] bool Starred = false,
    [property: JsonPropertyName("hasAttachment")] bool HasAttachment = false)
{
    public string Initial => string.IsNullOrWhiteSpace(Sender) ? "?" : Sender.Trim()[..1].ToUpperInvariant();
}

public sealed record MailAttachment(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("url")] string Url = "");

public sealed record MessageDetail(
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("bodyHtml")] string BodyHtml,
    [property: JsonPropertyName("to")] string To = "",
    [property: JsonPropertyName("cc")] string Cc = "",
    [property: JsonPropertyName("attachments")] IReadOnlyList<MailAttachment>? Attachments = null);

/// <summary>
/// Drives the real webmail.iitb.ac.in DOM inside the hidden AutomationHost: scrapes what's on
/// screen and dispatches real click/input events on Roundcube's own controls.
///
/// webmail.iitb.ac.in runs Roundcube (confirmed), so the selectors below target Roundcube's
/// actual, long-stable markup (the "Elastic" skin's default since Roundcube 1.3+) rather than
/// a blind guess:
///   - message rows: &lt;tr id="rcmrowUID" class="message [unread]"&gt; inside #messagelist — the
///     "rcmrow" id prefix has been stable across Roundcube versions/skins for years.
///   - toolbar actions: #button-compose / #button-send — Roundcube has used these exact ids for
///     its compose/send toolbar buttons across skins for a long time.
///   - message preview: #messagecontframe — the well-known iframe id Roundcube (and its plugin
///     ecosystem) uses for the reading-pane preview frame.
/// These are still worth confirming with the Inspect (DevTools) button against the live,
/// authenticated site — skin customizations or a newer/older Roundcube version can shift exact
/// class names — but they start from real Roundcube conventions, not a generic guess.
/// </summary>
public sealed class DomBridge(AutomationHost host)
{
    // ---- SELECTOR CONSTANTS (tune these against the live site if needed) ----------------
    private const string MessageRowSelector = "tr[id^='rcmrow']";
    private const string SenderInRowSelector = "td.subject span.fromto, .fromto, td:nth-child(2)";
    private const string SubjectInRowSelector = "td.subject span.subject, span.subject, td:nth-child(3)";
    private const string DateInRowSelector = "td.subject span.date, .date, td:last-child";

    private const string ComposeButtonSelector = "#button-compose, a.button.compose";
    private const string SendButtonSelector = "#button-send, a.button.send";
    private const string ToFieldSelector = "#_to, textarea[name='_to'], input[name='_to']";
    private const string CcFieldSelector = "#_cc, textarea[name='_cc'], input[name='_cc']";
    private const string BccFieldSelector = "#_bcc, textarea[name='_bcc'], input[name='_bcc']";
    private const string CcToggleSelector = "#compose-cc, a[data-target='_cc'], a.button.cc, [aria-label='Cc' i]";
    private const string SubjectFieldSelector = "#_subject, input[name='_subject']";
    private const string BodyFieldSelector = "#composebody, textarea[name='_message']";
    private const string BodyIframeSelector = "iframe#composebody_ifr"; // present when TinyMCE HTML compose is on

    private const string PreviewFrameSelector = "iframe#messagecontframe, #preview-pane iframe";
    private const string PreviewSubjectSelector = "tr.subject td, .message-partheaders tr.subject td:last-child, .subject";
    private const string PreviewFromSelector = "tr.from td, .message-partheaders tr.from td:last-child, .from";
    private const string PreviewDateSelector = "tr.date td, .message-partheaders tr.date td:last-child, .date";
    private const string PreviewToSelector = "tr.to td, .message-partheaders tr.to td:last-child, .to";
    private const string PreviewCcSelector = "tr.cc td, .message-partheaders tr.cc td:last-child, .cc";
    private const string PreviewBodySelector = "#messagebody, .message-body, body";
    private const string AttachmentItemSelector = "#attachment-list li, .attachmentslist li";

    /// <summary>Roundcube's inbox has message rows; its login form does not.</summary>
    public const string IsLoggedInScript =
        $"(function(){{ return document.querySelector(\"{MessageRowSelector}\") != null || " +
        "document.querySelector(\"#messagelist\") != null; })()";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<InboxRow>> ListInboxAsync()
    {
        var script = $$"""
            (function() {
                const rows = Array.from(document.querySelectorAll("{{MessageRowSelector}}"));
                const text = (row, sel) => row.querySelector(sel)?.textContent?.trim() ?? "";
                return JSON.stringify(rows.slice(0, 200).map((row) => ({
                    id: row.id,
                    sender: text(row, "{{SenderInRowSelector}}"),
                    subject: text(row, "{{SubjectInRowSelector}}"),
                    snippet: "",
                    date: text(row, "{{DateInRowSelector}}"),
                    unread: row.classList.contains("unread")
                })));
            })();
            """;

        var json = await ExecuteJsonAsync(script);
        return JsonSerializer.Deserialize<List<InboxRow>>(json, JsonOptions) ?? [];
    }

    public async Task<MessageDetail?> OpenMessageAsync(string id)
    {
        var clickScript = $$"""
            (function() {
                const row = document.getElementById({{JsonSerializer.Serialize(id)}});
                if (!row) return "false";
                row.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));
                row.dispatchEvent(new MouseEvent("click", { bubbles: true }));
                return "true";
            })();
            """;
        var clicked = await ExecuteJsonAsync(clickScript);
        if (clicked != "true")
            return null;

        // Give Roundcube a moment to load the preview iframe.
        await Task.Delay(500);

        var readScript = $$"""
            (function() {
                const frame = document.querySelector("{{PreviewFrameSelector}}");
                const doc = frame ? (frame.contentDocument || frame.contentWindow?.document) : document;
                if (!doc) return "null";
                const text = (sel) => doc.querySelector(sel)?.textContent?.trim() ?? "";
                const body = doc.querySelector("{{PreviewBodySelector}}");
                if (!body) return "null";
                // Roundcube renders attachments as a list outside the preview iframe.
                const attachments = Array.from(document.querySelectorAll("{{AttachmentItemSelector}}")).map(li => {
                    const link = li.querySelector("a[href]");
                    return {
                        name: (li.querySelector(".attachment-name, .filename")?.textContent ?? li.textContent ?? "").trim(),
                        size: (li.querySelector(".attachment-size, .filesize")?.textContent ?? "").trim(),
                        url: link ? link.href : ""
                    };
                });

                return JSON.stringify({
                    subject: text("{{PreviewSubjectSelector}}"),
                    from: text("{{PreviewFromSelector}}"),
                    date: text("{{PreviewDateSelector}}"),
                    to: text("{{PreviewToSelector}}"),
                    cc: text("{{PreviewCcSelector}}"),
                    attachments: attachments,
                    bodyHtml: body.innerHTML
                });
            })();
            """;
        var json = await ExecuteJsonAsync(readScript);
        return json == "null" ? null : JsonSerializer.Deserialize<MessageDetail>(json, JsonOptions);
    }

    public async Task StartComposeAsync()
    {
        var script = $$"""
            (function() {
                const btn = document.querySelector("{{ComposeButtonSelector}}");
                btn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
            })();
            """;
        await ExecuteAsync(script);
        await Task.Delay(500);
    }

    public async Task FillComposeAsync(string to, string subject, string body, string cc = "", string bcc = "")
    {
        var script = $$"""
            (function() {
                function setNativeValue(el, value) {
                    const proto = el instanceof HTMLTextAreaElement ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
                    const setter = Object.getOwnPropertyDescriptor(proto, "value")?.set;
                    setter ? setter.call(el, value) : (el.value = value);
                    el.dispatchEvent(new Event("input", { bubbles: true }));
                    el.dispatchEvent(new Event("change", { bubbles: true }));
                }
                const to = document.querySelector("{{ToFieldSelector}}");
                if (to) setNativeValue(to, {{JsonSerializer.Serialize(to)}});

                const subject = document.querySelector("{{SubjectFieldSelector}}");
                if (subject) setNativeValue(subject, {{JsonSerializer.Serialize(subject)}});

                const cc = {{JsonSerializer.Serialize(cc)}};
                const bcc = {{JsonSerializer.Serialize(bcc)}};
                if (cc || bcc) {
                    // Cc/Bcc fields are hidden until Roundcube's own toggle reveals them.
                    document.querySelector("{{CcToggleSelector}}")?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
                    const ccField = document.querySelector("{{CcFieldSelector}}");
                    if (cc && ccField) setNativeValue(ccField, cc);
                    const bccField = document.querySelector("{{BccFieldSelector}}");
                    if (bcc && bccField) setNativeValue(bccField, bcc);
                }

                // Plain-text compose: a regular textarea. HTML compose: TinyMCE swaps in an
                // iframe (id ends in "_ifr") whose contentDocument.body is the actual editor.
                const bodyIframe = document.querySelector("{{BodyIframeSelector}}");
                const bodyDoc = bodyIframe?.contentDocument;
                if (bodyDoc?.body) {
                    bodyDoc.body.innerHTML = {{JsonSerializer.Serialize(body)}};
                    bodyDoc.body.dispatchEvent(new Event("input", { bubbles: true }));
                } else {
                    const bodyField = document.querySelector("{{BodyFieldSelector}}");
                    if (bodyField) setNativeValue(bodyField, {{JsonSerializer.Serialize(body)}});
                }
            })();
            """;
        await ExecuteAsync(script);
    }

    public async Task ClickSendAsync()
    {
        var script = $$"""
            (function() {
                const btn = document.querySelector("{{SendButtonSelector}}");
                btn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
            })();
            """;
        await ExecuteAsync(script);
    }

    /// <summary>Installs a MutationObserver that posts a message back whenever the message list changes.</summary>
    public async Task InstallChangeObserverAsync()
    {
        var script = $$"""
            (function() {
                if (window.__wrapperObserverInstalled) return;
                window.__wrapperObserverInstalled = true;
                const list = document.querySelector("#messagelist") || document.body;
                const observer = new MutationObserver(() => {
                    window.chrome.webview.postMessage(JSON.stringify({ type: "domChanged" }));
                });
                observer.observe(list, { childList: true, subtree: true });
            })();
            """;
        await ExecuteAsync(script);
    }

    private Task<string> ExecuteAsync(string script) => host.ExecuteScriptAsync(script);

    /// <summary>
    /// WebView2's ExecuteScriptAsync JSON-encodes whatever the script returns. Every script here
    /// returns a JS string (a JSON.stringify'd payload, or a literal "true"/"false"/"null"), so the
    /// raw result is always a JSON string-of-a-string that needs one extra unwrap.
    /// </summary>
    private async Task<string> ExecuteJsonAsync(string script)
    {
        var raw = await ExecuteAsync(script);
        return JsonSerializer.Deserialize<string>(raw) ?? "null";
    }
}
