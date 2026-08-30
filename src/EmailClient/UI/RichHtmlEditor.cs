using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EmailClient.UI;

/// <summary>
/// Thin reusable wrapper around a WebView2 contenteditable rich-text surface — the same
/// execCommand-driven JS bridge ComposeWindow's body editor uses, factored out so the signature
/// editor (which needs the same bold/italic/list/link formatting, just for a much shorter piece of
/// text) doesn't hand-roll its own copy of it.
/// </summary>
public sealed class RichHtmlEditor
{
    private readonly WebView2 _view;
    private string _html = "";

    public RichHtmlEditor(WebView2 view) => _view = view;

    public bool IsReady { get; private set; }

    private bool _subscribed;

    public async Task InitializeAsync(string initialHtml)
    {
        try
        {
            await _view.EnsureCoreWebView2Async();
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            if (_subscribed)
            {
                // The same CoreWebView2 instance survives across re-InitializeAsync calls (e.g.
                // switching signatures reuses this control), so without unsubscribing first every
                // switch stacked another WebMessageReceived handler on top of the last.
                _view.CoreWebView2.NewWindowRequested -= NewWindowRequested;
                _view.CoreWebView2.WebMessageReceived -= OnMessage;
            }
            _view.CoreWebView2.NewWindowRequested += NewWindowRequested;
            _view.CoreWebView2.WebMessageReceived += OnMessage;
            _subscribed = true;

            var loaded = new TaskCompletionSource<bool>();
            void OnNavigationCompleted(object? _, CoreWebView2NavigationCompletedEventArgs __) => loaded.TrySetResult(true);
            _view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _view.NavigateToString(EditorHtml);
            await loaded.Task;
            _view.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;

            _html = initialHtml;
            await _view.CoreWebView2.ExecuteScriptAsync($"setContent({JsonSerializer.Serialize(initialHtml)})");
            IsReady = true;
        }
        catch (Exception)
        {
            // No WebView2 runtime, or it failed to start.
            IsReady = false;
        }
    }

    private static void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Deserialize<string>(e.WebMessageAsJson) ?? "{}");
            if (document.RootElement.TryGetProperty("html", out var html))
                _html = html.GetString() ?? "";
        }
        catch (JsonException)
        {
            // A malformed post is not worth interrupting editing over.
        }
    }

    /// <summary>Pulls the very latest content so Save never misses the last keystroke.</summary>
    public async Task<string> GetHtmlAsync()
    {
        if (!IsReady)
            return _html;

        try
        {
            var raw = await _view.CoreWebView2.ExecuteScriptAsync("getContent()");
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json is null)
                return _html;

            using var document = JsonDocument.Parse(json);
            _html = document.RootElement.GetProperty("html").GetString() ?? "";
        }
        catch (Exception)
        {
            // Keep whatever the debounced push last gave us.
        }
        return _html;
    }

    public async Task ExecAsync(string command, string? value = null)
    {
        if (!IsReady)
            return;

        var argument = value is null ? "undefined" : JsonSerializer.Serialize(value);
        await _view.CoreWebView2.ExecuteScriptAsync(
            $"exec({JsonSerializer.Serialize(command)}, {argument})");
    }

    private const string EditorHtml = """
        <html><head><meta name="color-scheme" content="light"><style>
        :root { color-scheme: light; }
        html, body { height: 100%; margin: 0; background: #ffffff; color: #1f1f1f; }
        body { font-family: 'Segoe UI', system-ui, sans-serif; font-size: 13px; line-height: 1.5; }
        #editor { min-height: 100%; padding: 10px 12px; outline: none; box-sizing: border-box; }
        a { color: #6d28d9; }
        </style></head>
        <body>
        <div id="editor" contenteditable="true" spellcheck="true"></div>
        <script>
        (function() {
            const editor = document.getElementById("editor");
            let pending;
            function post() {
                window.chrome.webview.postMessage(JSON.stringify({
                    html: editor.innerHTML,
                    text: editor.innerText
                }));
            }
            editor.addEventListener("input", function() {
                clearTimeout(pending);
                pending = setTimeout(post, 150);
            });
            editor.addEventListener("blur", post);

            window.setContent = function(html) { editor.innerHTML = html; };
            window.getContent = function() {
                return JSON.stringify({ html: editor.innerHTML, text: editor.innerText });
            };
            function ensureSelection() {
                const selection = window.getSelection();
                if (selection.rangeCount > 0 && editor.contains(selection.getRangeAt(0).commonAncestorContainer))
                    return;
                const range = document.createRange();
                range.selectNodeContents(editor);
                range.collapse(false);
                selection.removeAllRanges();
                selection.addRange(range);
            }
            window.exec = function(command, value) {
                editor.focus();
                ensureSelection();
                document.execCommand(command, false, value === undefined ? null : value);
                post();
            };
        })();
        </script>
        </body></html>
        """;
}
