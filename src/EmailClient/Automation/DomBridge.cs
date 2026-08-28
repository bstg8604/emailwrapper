using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailClient.Automation;

public sealed record InboxRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("unread")] bool Unread);

public sealed record MessageDetail(
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("bodyHtml")] string BodyHtml);

/// <summary>
/// Drives the real webmail.iitb.ac.in DOM inside the hidden AutomationHost: scrapes what's on
/// screen and dispatches real click/input events on Zimbra's own controls.
///
/// IMPORTANT — open risk called out in PLAN.md: the exact selectors below are best-effort
/// placeholders based on common Zimbra Modern UI markup (role="row" virtualized lists,
/// aria-label-driven controls). They have NOT been verified against the live, authenticated
/// site and will very likely need adjusting after inspecting webmail.iitb.ac.in with DevTools
/// open. Update the SELECTOR CONSTANTS section first before touching call sites.
/// </summary>
public sealed class DomBridge(AutomationHost host)
{
    // ---- SELECTOR CONSTANTS (tune these against the live site) --------------------------
    private const string MessageListSelector = "[role='row'], .zli, tr.ZmMailListItem";
    private const string ComposeButtonSelector = "[aria-label='Compose' i], [title='Compose' i], button[data-testid='compose']";
    private const string SendButtonSelector = "[aria-label='Send' i], button[title='Send' i]";
    private const string ToFieldSelector = "[aria-label='To' i] input, input[name='to']";
    private const string SubjectFieldSelector = "[aria-label='Subject' i] input, input[name='subject']";
    private const string BodyFieldSelector = "[aria-label='Message body' i] [contenteditable='true'], .cke_editable, [contenteditable='true']";
    private const string ReadingPaneSelector = "[role='article'], .MsgBody, .ZmMailMsgView";

    /// <summary>Best-effort "are we looking at an inbox, not a login form" check.</summary>
    public const string IsLoggedInScript =
        $"(function(){{ return document.querySelector(\"{MessageListSelector}\") != null && " +
        "document.querySelector(\"input[type=password]\") == null; })()";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<InboxRow>> ListInboxAsync()
    {
        var script = $$"""
            (function() {
                const rows = Array.from(document.querySelectorAll("{{MessageListSelector}}"));
                return JSON.stringify(rows.slice(0, 200).map((row, i) => {
                    const text = (sel) => row.querySelector(sel)?.textContent?.trim() ?? "";
                    return {
                        id: row.id || row.getAttribute("data-id") || String(i),
                        sender: text("[class*=sender i], [class*=from i]") || text("td:nth-child(2)"),
                        subject: text("[class*=subject i]") || text("td:nth-child(3)"),
                        snippet: text("[class*=snippet i], [class*=fragment i]"),
                        date: text("[class*=date i]") || text("td:last-child"),
                        unread: row.classList.contains("unread") || row.getAttribute("aria-label")?.toLowerCase().includes("unread") === true
                    };
                }));
            })();
            """;

        var json = await ExecuteJsonAsync(script);
        return JsonSerializer.Deserialize<List<InboxRow>>(json, JsonOptions) ?? [];
    }

    public async Task<MessageDetail?> OpenMessageAsync(string id)
    {
        var clickScript = $$"""
            (function() {
                const row = document.getElementById({{JsonSerializer.Serialize(id)}}) ||
                    document.querySelector(`[data-id="${{{JsonSerializer.Serialize(id)}}}"]`);
                if (!row) return "false";
                row.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));
                row.dispatchEvent(new MouseEvent("click", { bubbles: true }));
                return "true";
            })();
            """;
        var clicked = await ExecuteJsonAsync(clickScript);
        if (clicked != "true")
            return null;

        // Give Zimbra's own UI a moment to render the reading pane.
        await Task.Delay(400);

        var readScript = $$"""
            (function() {
                const pane = document.querySelector("{{ReadingPaneSelector}}");
                if (!pane) return "null";
                const text = (sel) => document.querySelector(sel)?.textContent?.trim() ?? "";
                return JSON.stringify({
                    subject: text("[class*=subject i]"),
                    from: text("[class*=from i], [class*=sender i]"),
                    date: text("[class*=date i]"),
                    bodyHtml: pane.innerHTML
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
        await Task.Delay(400);
    }

    public async Task FillComposeAsync(string to, string subject, string body)
    {
        var script = $$"""
            (function() {
                function setValue(sel, value) {
                    const el = document.querySelector(sel);
                    if (!el) return false;
                    if ("value" in el) {
                        const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")?.set;
                        setter ? setter.call(el, value) : (el.value = value);
                        el.dispatchEvent(new Event("input", { bubbles: true }));
                        el.dispatchEvent(new Event("change", { bubbles: true }));
                    } else {
                        el.textContent = value;
                        el.dispatchEvent(new Event("input", { bubbles: true }));
                    }
                    return true;
                }
                setValue("{{ToFieldSelector}}", {{JsonSerializer.Serialize(to)}});
                setValue("{{SubjectFieldSelector}}", {{JsonSerializer.Serialize(subject)}});
                setValue("{{BodyFieldSelector}}", {{JsonSerializer.Serialize(body)}});
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
                if (window.__iitbWrapperObserverInstalled) return;
                window.__iitbWrapperObserverInstalled = true;
                const target = document.body;
                const observer = new MutationObserver(() => {
                    window.chrome.webview.postMessage(JSON.stringify({ type: "domChanged" }));
                });
                observer.observe(target, { childList: true, subtree: true });
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
