using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EmailClient.Automation;

namespace EmailClient.UI;

/// <summary>A file staged for sending, as the chip row displays it.</summary>
public sealed record ComposeAttachment(string Path, string Name, string Size);

public sealed record ComposeResult(
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string Body,
    string BodyHtml = "",
    IReadOnlyList<ComposeAttachment>? Attachments = null)
{
    public IReadOnlyList<ComposeAttachment> Files => Attachments ?? [];

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(To)
        || !string.IsNullOrWhiteSpace(Subject)
        || !string.IsNullOrWhiteSpace(Body)
        || Files.Count > 0;
}

/// <summary>
/// The compose window. The body is a WebView2 <c>contenteditable</c> surface rather than a WPF
/// RichTextBox: the message has to leave here as HTML (it is what Roundcube's own compose form
/// accepts, and what the reading pane renders), and a contenteditable produces that directly
/// instead of needing a FlowDocument-to-HTML conversion layer in between. It also brings
/// spell-check, undo/redo and paste handling for free.
/// </summary>
public partial class ComposeWindow : Window
{
    /// <summary>Set when the user hits Send; null if the window was cancelled.</summary>
    public ComposeResult? Result { get; private set; }

    /// <summary>Whatever was typed when the window closed without sending, for draft-saving.</summary>
    public ComposeResult? Draft { get; private set; }

    private readonly List<ComposeAttachment> _attachments = [];
    private readonly string _initialHtml;

    // Kept current by the editor posting back on every (debounced) edit, so closing the window —
    // which can't await — still has the body to hand to a draft.
    private string _bodyHtml = "";
    private string _bodyText = "";

    private bool _plainTextMode;
    private bool _editorReady;
    private bool _discarding;

    /// <summary>
    /// Recipient autocomplete, wired in by MainWindow from the connected account's mail-history
    /// index. Null on sample data or before that index has anything to offer — the To/Cc/Bcc
    /// fields just behave as plain text boxes then.
    /// </summary>
    public Func<string, IReadOnlyList<string>>? SuggestContacts { get; set; }

    /// <summary>
    /// Backs the address-book picker (Apple Mail's "Address" button) — the *full* browsable list,
    /// as opposed to <see cref="SuggestContacts"/>'s type-ahead matches for a partial query.
    /// Null or empty on sample data / before the live index has finished building.
    /// </summary>
    public Func<IReadOnlyList<EmailClient.Mail.ContactEntry>>? ListAllContacts { get; set; }

    /// <summary>
    /// Wired in by MainWindow (real mailboxes only) — periodically hands the current draft off to
    /// be saved server-side, so a crash or a dropped connection mid-compose doesn't lose everything
    /// typed since the last manual save. Null on sample data, where there's nothing to protect.
    /// </summary>
    public Func<ComposeResult, Task>? AutoSaveDraft { get; set; }

    private readonly System.Windows.Threading.DispatcherTimer _autosaveTimer =
        new() { Interval = TimeSpan.FromSeconds(30) };
    private string _lastAutosavedSignature = "";

    public ComposeWindow(string to = "", string subject = "", string body = "", string cc = "", string bcc = "",
        string bodyHtml = "", IReadOnlyList<ComposeAttachment>? attachments = null)
    {
        InitializeComponent();
        MaximizeBoundsFix.Apply(this);

        ToBox.Text = to;
        SubjectBox.Text = subject;
        CcBox.Text = cc;
        BccBox.Text = bcc;

        // Restores attachments after an undo-send reopen, or when resuming a draft that had some.
        if (attachments is { Count: > 0 })
        {
            _attachments.AddRange(attachments);
            RefreshAttachments();
        }

        // A caller may supply either: HTML for a reply/forward that should keep the original's
        // formatting, or plain text for a fresh message.
        _initialHtml = !string.IsNullOrEmpty(bodyHtml)
            ? bodyHtml
            : PlainTextToHtml(body);
        _bodyHtml = _initialHtml;
        _bodyText = body;
        PlainEditor.Text = body;

        if (!string.IsNullOrWhiteSpace(cc))
            ShowCc();
        if (!string.IsNullOrWhiteSpace(bcc))
            ShowBcc();

        PreviewKeyDown += ComposeWindow_PreviewKeyDown;
        StateChanged += (_, _) =>
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        Loaded += (_, _) => WindowCorners.Apply(this);
        this.FadeInOnShow();
        Loaded += async (_, _) => await InitialiseEditorAsync();
        Loaded += (_, _) => WireRecipientAutocomplete();
        Loaded += (_, _) => _autosaveTimer.Start();
        _autosaveTimer.Tick += AutosaveTimer_Tick;

        Closed += (_, _) =>
        {
            if (Result is null && !_discarding)
                Draft = BuildResult();
        };
    }

    // ---- Editor ---------------------------------------------------------------------------

    /// <summary>
    /// The editor document. Content changes are posted back debounced so the window always holds
    /// a recent copy without a round trip on every keystroke.
    /// </summary>
    private const string EditorHtml = """
        <html><head><meta name="color-scheme" content="light"><style>
        :root { color-scheme: light; }
        html, body { height: 100%; margin: 0; background: #ffffff; color: #1f1f1f; }
        body { font-family: 'Segoe UI', system-ui, sans-serif; font-size: 14px; line-height: 1.55; }
        #editor { min-height: 100%; padding: 14px 16px; outline: none; box-sizing: border-box; }
        blockquote { margin: 0 0 0 10px; padding: 0 0 0 12px; border-left: 3px solid #ddd; color: #555; }
        img { max-width: 100%; height: auto; }
        a { color: #6d28d9; }
        /* Excel/Sheets/Docs paste tabular data as a real HTML &lt;table&gt; — without borders it
           renders as invisible cells, which looks like the paste silently lost the data. */
        table { border-collapse: collapse; }
        table td, table th { border: 1px solid #ddd; padding: 4px 8px; }
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

            // Ctrl+Shift+V ("paste and match style" in Gmail/Apple Mail terms): reduces whatever
            // is on the clipboard to plain text instead of carrying over Word/Excel/Docs styling.
            // Plain Ctrl+V is left untouched, so a pasted spreadsheet still becomes a real table.
            let shiftDown = false;
            document.addEventListener("keydown", function(e) { if (e.key === "Shift") shiftDown = true; });
            document.addEventListener("keyup", function(e) { if (e.key === "Shift") shiftDown = false; });
            editor.addEventListener("paste", function(e) {
                if (!shiftDown) return;
                e.preventDefault();
                const text = (e.clipboardData || window.clipboardData).getData("text/plain");
                document.execCommand("insertText", false, text);
                post();
            });

            window.setContent = function(html) { editor.innerHTML = html; post(); };
            window.getContent = function() {
                return JSON.stringify({ html: editor.innerHTML, text: editor.innerText });
            };
            window.focusTop = function() {
                editor.focus();
                const range = document.createRange();
                range.setStart(editor, 0);
                range.collapse(true);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
            };
            // A formatting command silently no-ops if the editor never had a real
            // selection/caret placed in it yet — e.g. picking a font as the very first click
            // after the window opens, with focus still in To/Subject. editor.focus() alone
            // doesn't guarantee execCommand has anything to act on, so a fallback caret is
            // placed at the end of the content first.
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
            window.insertPlainText = function(text) {
                editor.focus();
                document.execCommand("insertText", false, text);
                post();
            };
            window.insertHtml = function(html) {
                editor.focus();
                ensureSelection();
                document.execCommand("insertHTML", false, html);
                post();
            };
            window.insertImage = function(dataUri) {
                editor.focus();
                document.execCommand("insertImage", false, dataUri);
                post();
            };
            // execCommand("fontSize") only has the legacy 1-7 HTML scale, no way to ask for an
            // actual point size — so size 7 is applied as a disposable marker, and every <font
            // size="7"> it produced is immediately swapped for a real CSS font-size in points.
            window.setFontSizePt = function(pt) {
                editor.focus();
                ensureSelection();
                document.execCommand("fontSize", false, "7");
                editor.querySelectorAll('font[size="7"]').forEach(function(node) {
                    node.removeAttribute("size");
                    node.style.fontSize = pt + "pt";
                });
                post();
            };
        })();
        </script>
        </body></html>
        """;

    private async Task InitialiseEditorAsync()
    {
        try
        {
            await RichEditor.EnsureCoreWebView2Async();
            RichEditor.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            RichEditor.CoreWebView2.Settings.AreDevToolsEnabled = false;
            RichEditor.CoreWebView2.Settings.IsStatusBarEnabled = false;
            RichEditor.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            RichEditor.CoreWebView2.WebMessageReceived += OnEditorMessage;

            var loaded = new TaskCompletionSource<bool>();
            void OnNavigationCompleted(object? _, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs __)
                => loaded.TrySetResult(true);

            RichEditor.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            RichEditor.NavigateToString(EditorHtml);
            await loaded.Task;
            RichEditor.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;

            await RichEditor.CoreWebView2.ExecuteScriptAsync(
                $"setContent({JsonSerializer.Serialize(_initialHtml)})");
            _editorReady = true;

            PlaceInitialFocus();
        }
        catch (Exception)
        {
            // No WebView2 runtime, or it failed to start — fall back to the plain editor rather
            // than leaving the user with a compose window they can't type in. Re-derive from
            // _initialHtml (not the constructor's plain `body`) so a signature or quoted original
            // supplied only as HTML isn't silently dropped by the fallback.
            PlainEditor.Text = MailText.HtmlToPlainText(_initialHtml);
            SetPlainTextMode(true);
            _editorReady = false;
            PlaceInitialFocus();
        }
    }

    private void OnEditorMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Deserialize<string>(e.WebMessageAsJson) ?? "{}");
            if (document.RootElement.TryGetProperty("html", out var html))
                _bodyHtml = html.GetString() ?? "";
            if (document.RootElement.TryGetProperty("text", out var text))
                _bodyText = text.GetString() ?? "";
        }
        catch (JsonException)
        {
            // A malformed post is not worth interrupting composition over.
        }
    }

    /// <summary>Pulls the very latest content, so Send never misses the last keystroke.</summary>
    private async Task FlushEditorAsync()
    {
        if (_plainTextMode || !_editorReady)
        {
            _bodyText = PlainEditor.Text;
            _bodyHtml = PlainTextToHtml(PlainEditor.Text);
            return;
        }

        try
        {
            var raw = await RichEditor.CoreWebView2.ExecuteScriptAsync("getContent()");
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json is null)
                return;

            using var document = JsonDocument.Parse(json);
            _bodyHtml = document.RootElement.GetProperty("html").GetString() ?? "";
            _bodyText = document.RootElement.GetProperty("text").GetString() ?? "";
        }
        catch (Exception)
        {
            // Keep whatever the debounced push last gave us.
        }
    }

    private async Task ExecAsync(string command, string? value = null)
    {
        if (_plainTextMode || !_editorReady)
            return;

        var argument = value is null ? "undefined" : JsonSerializer.Serialize(value);
        await RichEditor.CoreWebView2.ExecuteScriptAsync(
            $"exec({JsonSerializer.Serialize(command)}, {argument})");
    }

    private static string PlainTextToHtml(string text) =>
        string.IsNullOrEmpty(text)
            ? ""
            : string.Join("", text.Replace("\r\n", "\n").Split('\n')
                .Select(line => line.Length == 0 ? "<p><br></p>" : $"<p>{WebUtility.HtmlEncode(line)}</p>"));

    // ---- Formatting commands ----------------------------------------------------------------

    private async void Bold_Click(object sender, RoutedEventArgs e) => await ExecAsync("bold");
    private async void Italic_Click(object sender, RoutedEventArgs e) => await ExecAsync("italic");
    private async void Underline_Click(object sender, RoutedEventArgs e) => await ExecAsync("underline");
    private async void Strikethrough_Click(object sender, RoutedEventArgs e) => await ExecAsync("strikeThrough");
    private async void BulletList_Click(object sender, RoutedEventArgs e) => await ExecAsync("insertUnorderedList");
    private async void NumberedList_Click(object sender, RoutedEventArgs e) => await ExecAsync("insertOrderedList");
    private async void Indent_Click(object sender, RoutedEventArgs e) => await ExecAsync("indent");
    private async void Outdent_Click(object sender, RoutedEventArgs e) => await ExecAsync("outdent");
    private async void Quote_Click(object sender, RoutedEventArgs e) => await ExecAsync("formatBlock", "blockquote");
    private async void ClearFormatting_Click(object sender, RoutedEventArgs e) => await ExecAsync("removeFormat");

    private async void AlignLeft_Click(object sender, RoutedEventArgs e) => await ExecAsync("justifyLeft");
    private async void AlignCenter_Click(object sender, RoutedEventArgs e) => await ExecAsync("justifyCenter");
    private async void AlignRight_Click(object sender, RoutedEventArgs e) => await ExecAsync("justifyRight");

    /// <summary>
    /// Expands/collapses OverflowWrap — the less-frequently-used formatting actions live in a
    /// second toolbar row rather than a dropdown menu, the same "more options" row Roundcube's
    /// compose toolbar uses. Rich-only rows in that panel are kept in sync with the current
    /// plain/rich mode by SetPlainTextMode, exactly like FormatWrap's own controls.
    /// </summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var expanding = OverflowWrap.Visibility != Visibility.Visible;
        if (expanding)
            OverflowWrap.SlideDownReveal(140, fromOffset: -6);
        else
            OverflowWrap.SlideUpHide(110, toOffset: -6);
        MoreButton.Background = expanding
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xED, 0xE8, 0xF8))
            : System.Windows.Media.Brushes.White;
    }

    /// <summary>
    /// execCommand("fontSize") only understands the legacy HTML 1–7 scale, not an actual point
    /// size — Word/Docs-style numeric sizing needs a real CSS font-size instead. The standard
    /// workaround: apply the legacy size 7 (guaranteed unused elsewhere) as a throwaway marker,
    /// then swap the &lt;font size="7"&gt; wrapper it produces for a span with the real size.
    /// </summary>
    private async void SizeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_editorReady || SizeCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem { Content: string points })
            return;
        await RichEditor.CoreWebView2.ExecuteScriptAsync($"setFontSizePt({points})");
    }

    // The Google Sheets/Docs fill-colour picker's layout: a greyscale row (black to white), then a
    // grid of hue columns each shaded from light to dark — generated from HSL rather than hand-
    // picked hex values, the same way Sheets' own palette is clearly a formula, not a curated list.
    private static readonly double[] HueColumns = [0, 25, 40, 80, 140, 180, 210, 255, 290, 330];
    private static readonly double[] ShadeLightness = [0.82, 0.68, 0.54, 0.40, 0.28];

    private static string HslToHex(double h, double s, double l)
    {
        double C = (1 - Math.Abs(2 * l - 1)) * s;
        double X = C * (1 - Math.Abs((h / 60 % 2) - 1));
        double m = l - C / 2;
        var (r, g, b) = h switch
        {
            < 60 => (C, X, 0.0),
            < 120 => (X, C, 0.0),
            < 180 => (0.0, C, X),
            < 240 => (0.0, X, C),
            < 300 => (X, 0.0, C),
            _ => (C, 0.0, X),
        };
        int R = (int)Math.Round((r + m) * 255), G = (int)Math.Round((g + m) * 255), B = (int)Math.Round((b + m) * 255);
        return $"#{R:x2}{G:x2}{B:x2}";
    }

    private static string HsvToHex(double h, double s, double v)
    {
        double C = v * s;
        double X = C * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - C;
        var (r, g, b) = h switch
        {
            < 60 => (C, X, 0.0),
            < 120 => (X, C, 0.0),
            < 180 => (0.0, C, X),
            < 240 => (0.0, X, C),
            < 300 => (X, 0.0, C),
            _ => (C, 0.0, X),
        };
        int R = (int)Math.Round((r + m) * 255), G = (int)Math.Round((g + m) * 255), B = (int)Math.Round((b + m) * 255);
        return $"#{R:x2}{G:x2}{B:x2}";
    }

    private static IReadOnlyList<string> GreyscaleRow() =>
        [.. Enumerable.Range(0, 10).Select(i => HslToHex(0, 0, 1 - i / 9.0))];

    /// <summary>10 hue columns x 5 shades, read column-major so each hue's shades sit together —
    /// same reading order as Sheets' grid.</summary>
    private static IReadOnlyList<string> HueGrid() =>
        [.. HueColumns.SelectMany(h => ShadeLightness.Select(l => HslToHex(h, 0.68, l)))];

    private Window? _colourPopup;
    private Window? _highlightPopup;

    // Most-recently-used custom colours, shared across every Compose window opened this session
    // (not just this one) — the same way a recently-used list behaves in Word/Docs. Capped short;
    // this is a quick-recall strip, not a saved palette.
    private static readonly List<string> RecentTextColours = [];
    private static readonly List<string> RecentHighlightColours = [];
    private const int MaxRecentColours = 6;

    private static void RememberRecent(List<string> recents, string hex)
    {
        if (hex == "transparent")
            return;
        recents.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
        recents.Insert(0, hex);
        if (recents.Count > MaxRecentColours)
            recents.RemoveRange(MaxRecentColours, recents.Count - MaxRecentColours);
    }

    /// <summary>
    /// Accepts "#rgb", "#rrggbb", "rgb(r, g, b)" or the bare hex without a leading "#", the formats
    /// someone would actually type or paste in from elsewhere. Returns a normalised "#rrggbb".
    /// </summary>
    private static bool TryParseColourInput(string input, out string hex)
    {
        hex = "";
        input = input.Trim();
        if (input.Length == 0)
            return false;

        var rgbMatch = System.Text.RegularExpressions.Regex.Match(
            input, @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (rgbMatch.Success)
        {
            var parts = new int[3];
            for (var i = 0; i < 3; i++)
            {
                if (!int.TryParse(rgbMatch.Groups[i + 1].Value, out parts[i]) || parts[i] is < 0 or > 255)
                    return false;
            }
            hex = $"#{parts[0]:x2}{parts[1]:x2}{parts[2]:x2}";
            return true;
        }

        var candidate = input.StartsWith('#') ? input[1..] : input;
        if (candidate.Length == 3 && candidate.All(Uri.IsHexDigit))
        {
            hex = $"#{candidate[0]}{candidate[0]}{candidate[1]}{candidate[1]}{candidate[2]}{candidate[2]}".ToLowerInvariant();
            return true;
        }
        if (candidate.Length == 6 && candidate.All(Uri.IsHexDigit))
        {
            hex = $"#{candidate}".ToLowerInvariant();
            return true;
        }
        return false;
    }

    private static System.Windows.Media.Brush BrushFor(string hex) =>
        (new BrushConverter().ConvertFromString(hex) as System.Windows.Media.Brush)!;

    /// <summary>Rounded-square swatch with a soft resting shadow and a hover "lift" (scale up +
    /// a brighter ring) — the small tactile motion modern colour pickers (Notion, Linear, Figma)
    /// use in place of Sheets' flat hover-only ring.</summary>
    private static System.Windows.Controls.Button MakeSwatchButton(string hex, string name, bool isNone, double size = 18)
    {
        var swatch = new System.Windows.Controls.Button
        {
            Width = size,
            Height = size,
            Margin = new Thickness(2),
            Background = isNone ? System.Windows.Media.Brushes.White : BrushFor(hex),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0xE1, 0xE6)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = hex,
            ToolTip = name,
            Content = isNone ? "✕" : null,
            FontSize = 8,
            Foreground = System.Windows.Media.Brushes.Gray,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        swatch.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, name);
        var template = new ControlTemplate(typeof(System.Windows.Controls.Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
            { Color = System.Windows.Media.Colors.Black, Opacity = 0.12, BlurRadius = 3, ShadowDepth = 1 });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        // Built before the template is assigned to the button — once assigned and applied, the
        // TriggerCollection seals and can't be added to any more.
        var hoverTrigger = new Trigger { Property = System.Windows.Controls.Button.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6D, 0x28, 0xD9))) { TargetName = "Bd" });
        hoverTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2)) { TargetName = "Bd" });
        template.Triggers.Add(hoverTrigger);
        swatch.Template = template;

        var scaleEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        swatch.MouseEnter += (_, _) =>
        {
            var scale = (ScaleTransform)swatch.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.22, TimeSpan.FromMilliseconds(110)) { EasingFunction = scaleEase });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.22, TimeSpan.FromMilliseconds(110)) { EasingFunction = scaleEase });
        };
        swatch.MouseLeave += (_, _) =>
        {
            var scale = (ScaleTransform)swatch.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)) { EasingFunction = scaleEase });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)) { EasingFunction = scaleEase });
        };

        return swatch;
    }

    /// <summary>
    /// Builds and opens a Google Sheets/Docs-style colour-picker popup anchored under
    /// <paramref name="anchor"/>: a greyscale row, a hue x shade grid generated from HSL, then a
    /// "Custom" section with recently-used colours and a "+" that expands hex/RGB entry — shared by
    /// the text-colour and highlight-colour pickers, which differ only in whether "None" is offered
    /// and what picking a colour does. Rebuilt fresh every open (not cached) so the recent-colours
    /// row always reflects the latest picks.
    /// </summary>
    private Window BuildColourPopup(
        System.Windows.Controls.Button anchor, string title, bool includeNone, List<string> recents, Action<string> onPick)
    {
        var outer = new StackPanel { Margin = new Thickness(14) };

        outer.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.Black,
            Margin = new Thickness(0, 0, 0, 10),
        });

        if (includeNone)
        {
            var noneRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var none = MakeSwatchButton("transparent", "None", isNone: true);
            none.Click += (_, _) => onPick("transparent");
            noneRow.Children.Add(none);
            noneRow.Children.Add(new TextBlock
            {
                Text = "None", FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.DimGray,
            });
            outer.Children.Add(noneRow);
        }

        var greyRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        foreach (var hex in GreyscaleRow())
        {
            var swatch = MakeSwatchButton(hex, hex, isNone: false);
            swatch.Click += (_, _) => onPick(hex);
            greyRow.Children.Add(swatch);
        }
        outer.Children.Add(greyRow);

        // Column-major: each hue's shades stack in one column, light at the top, same as Sheets.
        var hueGrid = new Grid();
        for (var c = 0; c < HueColumns.Length; c++)
            hueGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < ShadeLightness.Length; r++)
            hueGrid.RowDefinitions.Add(new RowDefinition());

        for (var c = 0; c < HueColumns.Length; c++)
        {
            for (var r = 0; r < ShadeLightness.Length; r++)
            {
                var hex = HslToHex(HueColumns[c], 0.68, ShadeLightness[r]);
                var swatch = MakeSwatchButton(hex, hex, isNone: false);
                swatch.Click += (_, _) => onPick(hex);
                System.Windows.Controls.Grid.SetColumn(swatch, c);
                System.Windows.Controls.Grid.SetRow(swatch, r);
                hueGrid.Children.Add(swatch);
            }
        }
        outer.Children.Add(hueGrid);

        outer.Children.Add(new Border { Height = 1, Background = System.Windows.Media.Brushes.WhiteSmoke, Margin = new Thickness(0, 8, 0, 6) });

        outer.Children.Add(new TextBlock
        {
            Text = "CUSTOM", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(1, 0, 0, 4),
        });

        var customRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        foreach (var hex in recents)
        {
            var swatch = MakeSwatchButton(hex, hex, isNone: false);
            swatch.Click += (_, _) => onPick(hex);
            customRow.Children.Add(swatch);
        }

        // "+" opens the custom picker below instead of a second popup/dialog — one click away, no
        // extra window to manage. A draggable hue strip for a quick pick, plus the hex/RGB box for
        // exact values — the pairing every modern picker (Figma, Notion, macOS) uses instead of
        // Sheets' text-only "custom" entry.
        var customPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        // A label above the field, not ghost text inside it — matches how every other field in
        // this app already shows its label (see AccountWindow's "Email"/"Password" captions), and
        // sidesteps the whole class of "does the placeholder actually line up with the caret" bug
        // two different inline-overlay attempts both hit here.
        var hexLabel = new TextBlock
        {
            Text = "HEX CODE", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 3),
        };
        var hexBox = new System.Windows.Controls.TextBox
        {
            Width = 130, Padding = new Thickness(6, 5, 6, 5), FontSize = 12,
            Text = "", BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD5, 0xD5, 0xD5)),
        };

        // Saturation/value square (Figma/Photoshop convention) — the hue strip alone (the previous
        // "custom" picker) could only ever land on one fixed lightness/saturation per hue; this
        // adds the missing second dimension: darker/lighter and more/less saturated variants of
        // whatever hue the strip below picks.
        double customHue = 0, customSat = 1, customVal = 1;

        // Fixed Width, and the thumb positioned via Canvas.Left/Top rather than a growing Margin —
        // a Margin-positioned child's offset counts toward its own DesiredSize, which bubbles up
        // and made the whole popup stretch wider as the thumb was dragged right. A Canvas's
        // children never affect its own measured size regardless of where they're placed on it.
        const double svWidth = 220, svHeight = 110;
        var svSquare = new Grid { Width = svWidth, Height = svHeight, Margin = new Thickness(0, 0, 0, 10), ClipToBounds = true };
        var svBase = new Border { CornerRadius = new CornerRadius(6), Background = BrushFor(HsvToHex(0, 1, 1)) };
        var svSaturationOverlay = new Border
        {
            Background = new LinearGradientBrush(Colors.White, System.Windows.Media.Color.FromArgb(0, 255, 255, 255),
                new System.Windows.Point(0, 0), new System.Windows.Point(1, 0)),
        };
        var svValueOverlay = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), Colors.Black,
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1)),
        };
        var svThumb = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(2), BorderBrush = System.Windows.Media.Brushes.White,
            IsHitTestVisible = false,
        };
        var svThumbLayer = new Canvas { IsHitTestVisible = false };
        svThumbLayer.Children.Add(svThumb);
        Canvas.SetLeft(svThumb, -7);
        Canvas.SetTop(svThumb, -7);
        svSquare.Children.Add(svBase);
        svSquare.Children.Add(svSaturationOverlay);
        svSquare.Children.Add(svValueOverlay);
        svSquare.Children.Add(svThumbLayer);

        var hueStops = Enumerable.Range(0, 7)
            .Select(i => new GradientStop(((SolidColorBrush)BrushFor(HslToHex(i * 60, 1, 0.5))).Color, i / 6.0));
        var hueTrack = new Border
        {
            Height = 14, CornerRadius = new CornerRadius(7), Cursor = System.Windows.Input.Cursors.Hand,
            Background = new LinearGradientBrush(new GradientStopCollection(hueStops), new System.Windows.Point(0, 0), new System.Windows.Point(1, 0)),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0xE1, 0xE6)),
        };
        // Same Canvas-positioning fix as the sv-square thumb below — a Margin-positioned thumb's
        // offset counts toward the Grid's own measured width, which grew the whole popup wider as
        // the thumb was dragged right.
        var hueThumb = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6D, 0x28, 0xD9)),
            IsHitTestVisible = false,
        };
        var hueThumbLayer = new Canvas { IsHitTestVisible = false };
        hueThumbLayer.Children.Add(hueThumb);
        Canvas.SetTop(hueThumb, -1);
        var hueGridPanel = new Grid { Width = svWidth, Height = 14, Margin = new Thickness(0, 0, 0, 10) };
        hueGridPanel.Children.Add(hueTrack);
        hueGridPanel.Children.Add(hueThumbLayer);

        void UpdateHexFromHsv() => hexBox.Text = HsvToHex(customHue, customSat, customVal);

        var draggingHue = false;
        void MoveHueThumb(double fraction)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            Canvas.SetLeft(hueThumb, fraction * (svWidth - hueThumb.Width));
            customHue = fraction * 359.999;
            svBase.Background = BrushFor(HsvToHex(customHue, 1, 1));
            UpdateHexFromHsv();
        }
        hueGridPanel.MouseLeftButtonDown += (_, e) =>
        {
            draggingHue = true;
            hueGridPanel.CaptureMouse();
            MoveHueThumb(e.GetPosition(hueGridPanel).X / svWidth);
        };
        hueGridPanel.MouseMove += (_, e) =>
        {
            if (draggingHue)
                MoveHueThumb(e.GetPosition(hueGridPanel).X / svWidth);
        };
        hueGridPanel.MouseLeftButtonUp += (_, _) =>
        {
            draggingHue = false;
            hueGridPanel.ReleaseMouseCapture();
        };

        var draggingSv = false;
        void MoveSvThumb(double fx, double fy)
        {
            fx = Math.Clamp(fx, 0, 1);
            fy = Math.Clamp(fy, 0, 1);
            Canvas.SetLeft(svThumb, fx * svWidth - 7);
            Canvas.SetTop(svThumb, fy * svHeight - 7);
            customSat = fx;
            customVal = 1 - fy;
            UpdateHexFromHsv();
        }
        svSquare.MouseLeftButtonDown += (_, e) =>
        {
            draggingSv = true;
            svSquare.CaptureMouse();
            var p = e.GetPosition(svSquare);
            MoveSvThumb(p.X / svWidth, p.Y / svHeight);
        };
        svSquare.MouseMove += (_, e) =>
        {
            if (draggingSv)
            {
                var p = e.GetPosition(svSquare);
                MoveSvThumb(p.X / svWidth, p.Y / svHeight);
            }
        };
        svSquare.MouseLeftButtonUp += (_, _) =>
        {
            draggingSv = false;
            svSquare.ReleaseMouseCapture();
        };

        var hexRow = new DockPanel { LastChildFill = true };
        var preview = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD5, 0xD5, 0xD5)),
            Background = System.Windows.Media.Brushes.White,
        };
        DockPanel.SetDock(preview, Dock.Left);
        hexRow.Children.Add(preview);
        hexRow.Children.Add(hexBox);

        var addButton = new System.Windows.Controls.Button
        {
            Content = "Use colour", Padding = new Thickness(10, 5, 10, 5), FontSize = 11.5,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6D, 0x28, 0xD9)),
            Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0),
            IsEnabled = false,
        };
        var addTemplate = new ControlTemplate(typeof(System.Windows.Controls.Button));
        var addBorder = new FrameworkElementFactory(typeof(Border));
        addBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        addBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        addBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        var addPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        addPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        addBorder.AppendChild(addPresenter);
        addTemplate.VisualTree = addBorder;
        addButton.Template = addTemplate;

        hexBox.TextChanged += (_, _) =>
        {
            if (TryParseColourInput(hexBox.Text, out var parsed))
            {
                preview.Background = BrushFor(parsed);
                addButton.IsEnabled = true;
            }
            else
            {
                preview.Background = System.Windows.Media.Brushes.White;
                addButton.IsEnabled = false;
            }
        };
        addButton.Click += (_, _) =>
        {
            if (TryParseColourInput(hexBox.Text, out var parsed))
                onPick(parsed);
        };
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && TryParseColourInput(hexBox.Text, out var parsed))
                onPick(parsed);
        };

        customPanel.Children.Add(svSquare);
        customPanel.Children.Add(hueGridPanel);
        customPanel.Children.Add(hexLabel);
        customPanel.Children.Add(hexRow);
        customPanel.Children.Add(addButton);

        var plusButton = new System.Windows.Controls.Button
        {
            Width = 20, Height = 20, Margin = new Thickness(2.5), Content = "+", FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.White,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6D, 0x28, 0xD9)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD5, 0xD5, 0xD5)),
            ToolTip = "Custom colour",
        };
        plusButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Custom colour");
        var plusTemplate = new ControlTemplate(typeof(System.Windows.Controls.Button));
        var plusBorder = new FrameworkElementFactory(typeof(Border));
        plusBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        plusBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        plusBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
        plusBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        var plusPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        plusPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        plusPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        plusBorder.AppendChild(plusPresenter);
        plusTemplate.VisualTree = plusBorder;
        plusButton.Template = plusTemplate;
        plusButton.Click += (_, _) =>
        {
            // One-way: once open there's no reason to re-hide it (the whole popup closes on any
            // pick or on clicking away), and leaving "+" visible next to an already-open picker
            // reads as a stray, purposeless control rather than an integrated section.
            customPanel.SlideDownReveal(140, fromOffset: -6);
            plusButton.Visibility = Visibility.Collapsed;
            hexBox.Focus();
        };
        customRow.Children.Add(plusButton);

        outer.Children.Add(customRow);
        outer.Children.Add(customPanel);

        return ShowFloatingWindow(anchor, outer, cornerRadius: 14);
    }

    /// <summary>
    /// A small borderless owned Window, used instead of a WPF Popup for floating panels that need
    /// real rounded corners. A Popup anchored in this window can't get genuine per-pixel
    /// transparency — this window hosts WebView2 (the rich body editor) elsewhere in its tree,
    /// which stops WPF from compositing any Popup here with transparency at all, so a rounded
    /// Border's unpainted corner pixels render solid black instead of see-through (the same reason
    /// AccountWindow had to become a real Window rather than a Popup). A separate top-level Window
    /// — even one owned by this one — isn't affected by that and gets proper DWM corner rounding.
    /// Closes itself on losing focus, the same "click away to dismiss" a Popup with
    /// StaysOpen=false has.
    /// </summary>
    private Window ShowFloatingWindow(FrameworkElement anchor, UIElement content, double cornerRadius)
    {
        var win = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = System.Windows.Media.Brushes.White,
            Content = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xEA, 0xF2)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(cornerRadius),
                Child = content,
            },
        };
        win.Loaded += (_, _) => EmailClient.UI.WindowCorners.Apply(win);
        win.Deactivated += (_, _) =>
        {
            // Closing the window itself (e.g. picking a swatch) also fires Deactivated on the way
            // out — without this, that raced with the close already in progress and threw "Cannot
            // ... Close ... while a Window is closing".
            try { win.Close(); }
            catch (InvalidOperationException) { /* already closing */ }
        };

        var topLeft = anchor.PointToScreen(new System.Windows.Point(0, anchor.ActualHeight + 6));
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        win.Left = topLeft.X / scale;
        win.Top = topLeft.Y / scale;

        win.Show();
        return win;
    }

    private void ColourButton_Click(object sender, RoutedEventArgs e)
    {
        if (_colourPopup is { IsVisible: true } open)
        {
            open.Close();
            return;
        }

        _colourPopup = BuildColourPopup(ColourButton, "Text colour", includeNone: false, RecentTextColours, async hex =>
        {
            _colourPopup!.Close();
            RememberRecent(RecentTextColours, hex);
            ColourSwatch.Background = BrushFor(hex);
            await ExecAsync("foreColor", hex);
        });
    }

    private void HighlightButton_Click(object sender, RoutedEventArgs e)
    {
        if (_highlightPopup is { IsVisible: true } open)
        {
            open.Close();
            return;
        }

        _highlightPopup = BuildColourPopup(HighlightButton, "Highlight colour", includeNone: true, RecentHighlightColours, async hex =>
        {
            _highlightPopup!.Close();
            RememberRecent(RecentHighlightColours, hex);
            HighlightSwatch.Background = hex == "transparent" ? System.Windows.Media.Brushes.White : BrushFor(hex);
            await ExecAsync("hiliteColor", hex);
        });
    }

    // ---- Contacts / address book -------------------------------------------------------------

    private System.Windows.Controls.Primitives.Popup? _contactsPopup;
    private System.Windows.Controls.ListBox? _contactsList;
    private System.Windows.Controls.TextBox? _contactsSearchBox;
    private IReadOnlyList<EmailClient.Mail.ContactEntry> _allContacts = [];

    private void ContactsButton_Click(object sender, RoutedEventArgs e)
    {
        _allContacts = ListAllContacts?.Invoke() ?? [];
        if (_allContacts.Count == 0)
        {
            System.Windows.MessageBox.Show(this,
                "No contacts yet — this list is built from the people you've actually emailed, " +
                "and fills in as you send and receive mail.",
                "No contacts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_contactsPopup is null)
            BuildContactsPopup();

        _contactsSearchBox!.Text = "";
        _contactsList!.ItemsSource = _allContacts;
        _contactsPopup!.IsOpen = true;
        _contactsSearchBox.Focus();
    }

    private void BuildContactsPopup()
    {
        _contactsSearchBox = new System.Windows.Controls.TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12.5,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xED, 0xED, 0xF2)),
            Tag = "Search contacts",
        };
        _contactsSearchBox.TextChanged += (_, _) =>
        {
            var query = _contactsSearchBox.Text;
            _contactsList!.ItemsSource = string.IsNullOrWhiteSpace(query)
                ? _allContacts
                : _allContacts.Where(c => c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || c.Address.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        _contactsList = new System.Windows.Controls.ListBox
        {
            BorderThickness = new Thickness(0),
            MaxHeight = 260,
            DisplayMemberPath = nameof(EmailClient.Mail.ContactEntry.Formatted),
        };
        _contactsList.PreviewMouseLeftButtonUp += (_, _) => CommitContactSelection();
        _contactsList.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                CommitContactSelection();
        };

        var panel = new StackPanel { Width = 280 };
        panel.Children.Add(_contactsSearchBox);
        panel.Children.Add(_contactsList);

        _contactsPopup = new System.Windows.Controls.Primitives.Popup
        {
            // See BuildColourPopup's comment — Effect+ClipToBounds on the same element hard-clips
            // the blurred shadow into a dark ring; splitting them onto two nested Borders is the fix.
            Child = new Border
            {
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { Opacity = 0.15, BlurRadius = 16, ShadowDepth = 3 },
                Child = new Border
                {
                    Background = System.Windows.Media.Brushes.White,
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE2, 0xDD, 0xF0)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    ClipToBounds = true,
                    Child = panel,
                },
            },
            PlacementTarget = ContactsButton,
            StaysOpen = false,
        };
        _contactsPopup.KeepOnScreen();
        _contactsPopup.AnimateOnOpen();
    }

    /// <summary>Appends the picked address into whichever of To/Cc/Bcc was focused last — the
    /// same "click to add to the recipients you're building" behaviour Apple Mail's own address
    /// panel has, rather than replacing whatever was already typed there.</summary>
    private void CommitContactSelection()
    {
        if (_contactsList?.SelectedItem is not EmailClient.Mail.ContactEntry contact)
            return;

        var box = _lastFocusedRecipientBox;
        box.Text = string.IsNullOrWhiteSpace(box.Text)
            ? contact.Formatted
            : $"{box.Text.TrimEnd().TrimEnd(',')}, {contact.Formatted}";
        box.CaretIndex = box.Text.Length;

        _contactsPopup!.IsOpen = false;
        box.Focus();
    }

    // ---- Emoji ------------------------------------------------------------------------------

    // A small fixed set rather than a full picker (Gmail's own compose toolbar uses the same
    // approach) — covers what actually shows up in everyday mail without needing an emoji library.
    private static readonly string[] EmojiChoices =
    [
        "😀", "😂", "🙂", "😉", "😊", "😍", "😢", "😮",
        "👍", "👏", "🙏", "🚀", "🎉", "🔥", "✅", "❌",
        "⚠️", "📌", "📎", "💡", "📅", "⏰", "❤️", "✨",
    ];

    private System.Windows.Controls.Primitives.Popup? _emojiPopup;

    private void EmojiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_emojiPopup is null)
        {
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 8, Margin = new Thickness(6) };
            foreach (var emoji in EmojiChoices)
            {
                var button = new System.Windows.Controls.Button
                {
                    Content = emoji,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"),
                    FontSize = 18,
                    Width = 32,
                    Height = 32,
                    Margin = new Thickness(2),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                var template = new ControlTemplate(typeof(System.Windows.Controls.Button));
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
                presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
                border.AppendChild(presenter);
                template.VisualTree = border;
                var hoverTrigger = new Trigger { Property = System.Windows.Controls.Button.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                    new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xEE, 0xFC))));
                template.Triggers.Add(hoverTrigger);
                button.Template = template;
                button.Click += EmojiChoice_Click;
                grid.Children.Add(button);
            }

            _emojiPopup = new System.Windows.Controls.Primitives.Popup
            {
                // See BuildColourPopup's comment for why the shadow and the clipped/rounded
                // content live on two separate, nested Borders rather than one.
                Child = new Border
                {
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                        { Opacity = 0.15, BlurRadius = 16, ShadowDepth = 3 },
                    Child = new Border
                    {
                        Background = System.Windows.Media.Brushes.White,
                        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE2, 0xDD, 0xF0)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        ClipToBounds = true,
                        Child = grid,
                    },
                },
                StaysOpen = false,
            };
            _emojiPopup.KeepOnScreen();
            _emojiPopup.AnimateOnOpen();
        }

        // Anchored to whatever invoked it (the More-menu item), rather than a fixed toolbar
        // button — emoji now lives in the overflow menu, not the main bar.
        _emojiPopup.PlacementTarget = sender as System.Windows.UIElement;
        _emojiPopup.IsOpen = !_emojiPopup.IsOpen;
    }

    private async void EmojiChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Content: string emoji })
            return;

        _emojiPopup!.IsOpen = false;
        if (_plainTextMode || !_editorReady)
        {
            PlainEditor.SelectedText = emoji;
            return;
        }
        await RichEditor.CoreWebView2.ExecuteScriptAsync($"insertPlainText({JsonSerializer.Serialize(emoji)})");
    }

    // ---- Signature ----------------------------------------------------------------------------

    /// <summary>
    /// Wired in by MainWindow — the app's saved signatures (name + HTML body, so it keeps whatever
    /// line breaks were entered) live in AppSettings on the MainWindow side, not something
    /// ComposeWindow owns itself. A real class rather than a tuple — WPF's DisplayMemberPath binds
    /// via reflection, which can't see compile-time-only tuple element names.
    /// </summary>
    public sealed class SignatureOption
    {
        public string Name { get; init; } = "";
        public string Html { get; init; } = "";
    }

    public Func<List<SignatureOption>>? GetSignatures { get; set; }

    /// <summary>Refills the combo just before it drops down, so a signature added/renamed/removed
    /// elsewhere while this Compose window is open still shows up correctly.</summary>
    private void SignatureCombo_DropDownOpened(object sender, EventArgs e)
    {
        var signatures = GetSignatures?.Invoke() ?? [];

        SignatureCombo.Items.Clear();
        SignatureCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Signature", IsEnabled = false });
        if (signatures.Count == 0)
        {
            SignatureCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = "No signatures set up", IsEnabled = false, FontStyle = FontStyles.Italic,
            });
        }
        else
        {
            foreach (var option in signatures)
                SignatureCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = option.Name, Tag = option.Html });
        }
        SignatureCombo.SelectedIndex = 0;
    }

    private void SignatureCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SignatureCombo.SelectedIndex <= 0)
            return;
        if (SignatureCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string html })
            InsertSignature(html);
        // Back to the placeholder — this is a one-shot "insert" action, not a persistent choice.
        SignatureCombo.SelectedIndex = 0;
    }

    private async void InsertSignature(string html)
    {
        if (_plainTextMode || !_editorReady)
        {
            PlainEditor.SelectedText = MailText.HtmlToPlainText(html);
            return;
        }
        await RichEditor.CoreWebView2.ExecuteScriptAsync($"insertHtml({JsonSerializer.Serialize(html)})");
    }

    private async void FontCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_editorReady || FontCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string family })
            return;
        await ExecAsync("fontName", family);
    }

    /// <summary>
    /// Reads the system clipboard directly (Win32, not the browser clipboard API) and inserts it
    /// as plain text — the toolbar counterpart to the in-editor Ctrl+Shift+V handler, for anyone
    /// who wouldn't otherwise discover the shortcut.
    /// </summary>
    private async void PastePlainButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_editorReady || !System.Windows.Clipboard.ContainsText())
            return;
        var text = System.Windows.Clipboard.GetText();
        await RichEditor.CoreWebView2.ExecuteScriptAsync($"insertPlainText({JsonSerializer.Serialize(text)})");
    }

    /// <summary>Max size of an image embedded inline — beyond this it belongs as a real attachment instead.</summary>
    private const long MaxInlineImageBytes = 5 * 1024 * 1024;

    private async void InsertImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_editorReady)
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > MaxInlineImageBytes)
            {
                System.Windows.MessageBox.Show(this,
                    "That image is larger than 5 MB. Attach it as a file instead of embedding it inline.",
                    "Image too large", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            var mimeType = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => "image/png",
            };
            var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
            await RichEditor.CoreWebView2.ExecuteScriptAsync($"insertImage({JsonSerializer.Serialize(dataUri)})");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't insert the image: {ex.Message}", "Insert failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        LinkBar.Visibility = Visibility.Visible;
        LinkBox.Text = "https://";
        LinkBox.Focus();
        LinkBox.CaretIndex = LinkBox.Text.Length;
    }

    private async void InsertLink_Click(object sender, RoutedEventArgs e)
    {
        var url = LinkBox.Text.Trim();
        LinkBar.Visibility = Visibility.Collapsed;

        // Only ever produce links the viewer can safely follow — a javascript: or data: href
        // typed in here would otherwise be handed straight to the recipient.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps
                && parsed.Scheme != Uri.UriSchemeMailto))
        {
            System.Windows.MessageBox.Show(this, "Enter a http, https or mailto address.", "That link isn't valid",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ExecAsync("createLink", parsed.AbsoluteUri);
    }

    private void CancelLink_Click(object sender, RoutedEventArgs e) => LinkBar.Visibility = Visibility.Collapsed;

    // ---- Plain text mode ----------------------------------------------------------------------

    private async void TogglePlainText_Click(object sender, RoutedEventArgs e)
    {
        if (!_plainTextMode)
            await FlushEditorAsync();

        SetPlainTextMode(!_plainTextMode);

        if (_plainTextMode)
        {
            // Dropping to plain text keeps the words and loses the formatting, which is exactly
            // what the switch means.
            PlainEditor.Text = MailText.HtmlToPlainText(_bodyHtml);
            PlainEditor.Focus();
        }
        else if (_editorReady)
        {
            await RichEditor.CoreWebView2.ExecuteScriptAsync(
                $"setContent({JsonSerializer.Serialize(PlainTextToHtml(PlainEditor.Text))})");
        }
    }

    private void SetPlainTextMode(bool plain)
    {
        _plainTextMode = plain;
        PlainEditor.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        RichEditor.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        // Each rich-only control (marked Tag="RichOnly" in XAML) hides individually rather than
        // through one wrapping container — see the comment on FormatWrap in the XAML for why a
        // nested WrapPanel breaks the toolbar's layout. The toggle itself has no such Tag, so it
        // keeps flowing normally and stays reachable in both modes.
        foreach (var element in FormatWrap.Children.OfType<FrameworkElement>()
                     .Concat(OverflowWrap.Children.OfType<FrameworkElement>()))
        {
            if (element.Tag as string == "RichOnly")
                element.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        }
        // Content stays a static "Aa" glyph — the toggled state itself is shown by the highlight,
        // same convention as the highlight-colour swatch button.
        PlainRichToggleButton.ToolTip = plain ? "Switch to rich text" : "Switch to plain text";
        PlainRichToggleButton.Background = plain
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xED, 0xE8, 0xF8))
            : System.Windows.Media.Brushes.White;
    }

    // ---- Attachments ---------------------------------------------------------------------------

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach files",
            Multiselect = true,
            // Explicit rather than relying on the default: any file type can be attached here,
            // same as Gmail/Outlook/Apple Mail/Thunderbird — this is not restricted to documents
            // or images the way the toolbar's "Insert image" button deliberately is.
            Filter = "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        AddAttachments(dialog.FileNames);
    }

    private void ComposeWindow_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void ComposeWindow_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            AddAttachments(paths);
        e.Handled = true;
    }

    private void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                // Dropping a folder should not silently attach nothing-shaped entries.
                if (Directory.Exists(path))
                    continue;

                var info = new FileInfo(path);
                if (!info.Exists)
                    continue;

                _attachments.Add(new ComposeAttachment(path, info.Name,
                    AttachmentViewerWindow.FormatSize(info.Length)));
            }
            catch (Exception)
            {
                // A file that vanished between the picker and here just isn't attached.
            }
        }

        RefreshAttachments();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string path })
            return;
        _attachments.RemoveAll(a => a.Path == path);
        RefreshAttachments();
    }

    private void RefreshAttachments()
    {
        AttachmentList.ItemsSource = null;
        AttachmentList.ItemsSource = _attachments;
        AttachmentList.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Window plumbing -------------------------------------------------------------------------

    /// <summary>
    /// A new message starts in the To field; a reply or forward already has its recipient, so it
    /// starts at the very top of the body — above the quoted original, which is where the reply
    /// actually gets written. Landing after the quote would mean scrolling up before typing.
    /// </summary>
    private async void PlaceInitialFocus()
    {
        if (string.IsNullOrWhiteSpace(ToBox.Text))
        {
            ToBox.Focus();
            ToBox.CaretIndex = ToBox.Text.Length;
            return;
        }

        if (_plainTextMode || !_editorReady)
        {
            PlainEditor.Focus();
            PlainEditor.CaretIndex = 0;
            PlainEditor.ScrollToHome();
            return;
        }

        RichEditor.Focus();
        await RichEditor.CoreWebView2.ExecuteScriptAsync("focusTop()");
    }

    // ---- Recipient autocomplete -------------------------------------------------------------

    /// <summary>
    /// A small dropdown under whichever of To/Cc/Bcc has focus, listing people from the account's
    /// mail history that match the token currently being typed (the segment after the last comma).
    /// Picking one replaces just that token, leaving anything typed before it alone.
    /// </summary>
    private System.Windows.Controls.Primitives.Popup? _suggestPopup;
    private System.Windows.Controls.ListBox? _suggestList;
    private System.Windows.Controls.TextBox? _suggestTarget;

    // Which recipient field the address-book picker adds a clicked contact to — whichever of
    // To/Cc/Bcc was focused most recently, defaulting to To before any of them ever were.
    private System.Windows.Controls.TextBox _lastFocusedRecipientBox = null!;

    private void WireRecipientAutocomplete()
    {
        _lastFocusedRecipientBox = ToBox;
        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.TextChanged += RecipientBox_TextChanged;
            box.PreviewKeyDown += RecipientBox_PreviewKeyDown;
            box.LostFocus += (_, _) => HideSuggestions();
            box.GotFocus += (_, _) => _lastFocusedRecipientBox = box;
        }
    }

    private void RecipientBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SuggestContacts is null || sender is not System.Windows.Controls.TextBox box)
            return;

        var token = CurrentToken(box);
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
        {
            HideSuggestions();
            return;
        }

        var matches = SuggestContacts(token);
        if (matches.Count == 0)
        {
            HideSuggestions();
            return;
        }

        ShowSuggestions(box, matches);
    }

    private static string CurrentToken(System.Windows.Controls.TextBox box)
    {
        var upToCaret = box.Text[..Math.Min(box.CaretIndex, box.Text.Length)];
        var lastComma = upToCaret.LastIndexOf(',');
        return upToCaret[(lastComma + 1)..].Trim();
    }

    private void ShowSuggestions(System.Windows.Controls.TextBox box, IReadOnlyList<string> matches)
    {
        _suggestTarget = box;

        if (_suggestPopup is null)
        {
            _suggestList = new System.Windows.Controls.ListBox { BorderThickness = new Thickness(1) };
            _suggestList.PreviewMouseLeftButtonUp += (_, _) => CommitSuggestion();
            _suggestPopup = new System.Windows.Controls.Primitives.Popup
            {
                // See BuildColourPopup's comment for why the shadow and the clipped/rounded
                // content live on two separate, nested Borders rather than one.
                Child = new Border
                {
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                        { Opacity = 0.12, BlurRadius = 14, ShadowDepth = 2 },
                    Child = new Border
                    {
                        Background = System.Windows.Media.Brushes.White,
                        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE2, 0xDD, 0xF0)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        ClipToBounds = true,
                        Child = _suggestList,
                    },
                },
                PlacementTarget = box,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
            };
        }

        _suggestList!.ItemsSource = matches;
        _suggestList.SelectedIndex = 0;
        _suggestPopup.PlacementTarget = box;
        _suggestPopup.IsOpen = true;
    }

    private void HideSuggestions()
    {
        if (_suggestPopup is not null)
            _suggestPopup.IsOpen = false;
    }

    private void CommitSuggestion()
    {
        if (_suggestTarget is not { } box || _suggestList?.SelectedItem is not string chosen)
            return;

        var upToCaret = box.Text[..Math.Min(box.CaretIndex, box.Text.Length)];
        var lastComma = upToCaret.LastIndexOf(',');
        var prefix = lastComma < 0 ? "" : box.Text[..(lastComma + 1)] + " ";
        var suffix = box.Text[Math.Min(box.CaretIndex, box.Text.Length)..];

        box.Text = $"{prefix}{chosen}, {suffix.TrimStart()}".TrimEnd();
        box.CaretIndex = (prefix + chosen + ", ").Length;
        HideSuggestions();
        box.Focus();
    }

    private void RecipientBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_suggestPopup is not { IsOpen: true } || _suggestList is null)
            return;

        switch (e.Key)
        {
            case Key.Down:
                _suggestList.SelectedIndex = Math.Min(_suggestList.SelectedIndex + 1, _suggestList.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.Up:
                _suggestList.SelectedIndex = Math.Max(_suggestList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab:
                CommitSuggestion();
                e.Handled = true;
                break;
            case Key.Escape:
                HideSuggestions();
                e.Handled = true;
                break;
        }
    }

    private void CcToggle_Click(object sender, MouseButtonEventArgs e) => ShowCc();
    private void BccToggle_Click(object sender, MouseButtonEventArgs e) => ShowBcc();

    private void ShowCc()
    {
        if (CcRow.Visibility == Visibility.Visible)
            return;
        CcRow.SlideDownReveal(140, fromOffset: -6);
        CcRowDivider.Visibility = Visibility.Visible;
        CcToggle.Visibility = Visibility.Collapsed;
        UpdateToggleSeparator();
    }

    private void ShowBcc()
    {
        if (BccRow.Visibility == Visibility.Visible)
            return;
        BccRow.SlideDownReveal(140, fromOffset: -6);
        BccRowDivider.Visibility = Visibility.Visible;
        BccToggle.Visibility = Visibility.Collapsed;
        UpdateToggleSeparator();
    }

    private void HideCc_Click(object sender, MouseButtonEventArgs e)
    {
        CcBox.Clear();
        CcRow.Visibility = Visibility.Collapsed;
        CcRowDivider.Visibility = Visibility.Collapsed;
        CcToggle.Visibility = Visibility.Visible;
        UpdateToggleSeparator();
    }

    private void HideBcc_Click(object sender, MouseButtonEventArgs e)
    {
        BccBox.Clear();
        BccRow.Visibility = Visibility.Collapsed;
        BccRowDivider.Visibility = Visibility.Collapsed;
        BccToggle.Visibility = Visibility.Visible;
        UpdateToggleSeparator();
    }

    private void UpdateToggleSeparator() =>
        ToggleSeparator.Visibility = CcToggle.Visibility == Visibility.Visible && BccToggle.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    // Ctrl+Enter to send and Esc to cancel are standard across Gmail, Outlook, and Apple Mail.
    private void ComposeWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SendButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (LinkBar.Visibility == Visibility.Visible)
            {
                LinkBar.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            Close();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Skips the round trip entirely when nothing's changed since the last autosave (e.g. the user
    /// stepped away without typing) — a cheap length+content signature, not a full diff, is enough
    /// to catch the common "unchanged" case.
    /// </summary>
    private async void AutosaveTimer_Tick(object? sender, EventArgs e)
    {
        if (AutoSaveDraft is null)
            return;

        await FlushEditorAsync();
        var draft = BuildResult();
        if (!draft.HasContent)
            return;

        var signature = $"{draft.To}{draft.Cc}{draft.Bcc}{draft.Subject}{draft.Body}{draft.Files.Count}";
        if (signature == _lastAutosavedSignature)
            return;

        _lastAutosavedSignature = signature;
        try
        {
            await AutoSaveDraft(draft);
        }
        catch
        {
            // Autosave failures (e.g. a transient network drop) must not escape this async void
            // handler — that would hit the UI-thread unhandled-exception path and crash the app.
            // Reset the signature so the next tick retries instead of silently giving up forever.
            _lastAutosavedSignature = "";
        }
    }

    private ComposeResult BuildResult() => new(
        ToBox.Text, CcBox.Text, BccBox.Text, SubjectBox.Text,
        _plainTextMode ? PlainEditor.Text : _bodyText,
        _plainTextMode ? "" : _bodyHtml,
        [.. _attachments]);

    // Catches the everyday "I said I attached something and then didn't" mistake — every major
    // client nags about this one way or another. Fires once per send attempt; if the user chooses
    // to send anyway, it doesn't ask again for the rest of this compose session.
    private static readonly System.Text.RegularExpressions.Regex MentionsAttachment = new(
        @"\battach(ed|ing|ment|ments)?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private bool _attachmentReminderDismissed;

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await FlushEditorAsync();

        if (string.IsNullOrWhiteSpace(ToBox.Text))
        {
            System.Windows.MessageBox.Show(this, "Add at least one recipient.", "Missing recipient",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var bodyText = _plainTextMode ? PlainEditor.Text : _bodyText;
        if (!_attachmentReminderDismissed && _attachments.Count == 0 && MentionsAttachment.IsMatch(bodyText))
        {
            if (System.Windows.MessageBox.Show(this,
                    "This message mentions an attachment, but nothing is attached. Send anyway?",
                    "No attachment found", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            _attachmentReminderDismissed = true;
        }

        // The actual send is driven by MainWindow, after a short undo-send window — this window's
        // job is just to collect the message and hand it back.
        Result = BuildResult();
        Close();
    }

    private async void SaveDraftButton_Click(object sender, RoutedEventArgs e)
    {
        await FlushEditorAsync();
        Draft = BuildResult();
        _discarding = true; // Draft is already set; don't let Closed overwrite it with a stale copy.
        Close();
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        if (BuildResult().HasContent
            && System.Windows.MessageBox.Show(this, "Discard this message? It won't be saved to Drafts.",
                "Discard message", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // Discard means discard — the Closed handler must not quietly turn it into a draft.
        _discarding = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _autosaveTimer.Stop();
        _autosaveTimer.Tick -= AutosaveTimer_Tick;
        if (_editorReady)
        {
            // _editorReady means the WebView2/CoreWebView2 was actually initialized — whether the
            // rich editor is currently *shown* (_plainTextMode) is irrelevant to whether it needs
            // disposing. Gating on both used to skip disposal entirely for any window that was
            // ever toggled to plain text, leaking that WebView2 for the rest of the process.
            //
            // Closing can't await, so this leans on the debounced push from the editor. A blur
            // fires one immediately, which covers the ordinary click-the-X case.
            RichEditor.Dispose();
        }
    }
}
