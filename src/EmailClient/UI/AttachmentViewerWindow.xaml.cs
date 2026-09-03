using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;

namespace EmailClient.UI;

/// <summary>
/// Opens an attachment inside the app instead of handing it to Windows: images and PDFs render in
/// a scoped WebView2, text-like files are shown as text, and anything else gets an honest
/// "can't preview this" with Save and Open-externally still available.
/// </summary>
public partial class AttachmentViewerWindow : Window
{
    private readonly string _filePath;
    private readonly string _displayName;

    /// <summary>Raster images, shown on the app's own backdrop rather than the browser's.</summary>
    private static readonly Dictionary<string, string> ImageTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp",
            [".webp"] = "image/webp",
            [".ico"] = "image/x-icon",
        };

    /// <summary>Beyond this, the image is handed to WebView2 as a file instead of inlined.</summary>
    private const long MaxInlineImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Shown as escaped text. HTML and SVG are deliberately handled here rather than rendered —
    /// an attachment is untrusted content, and executing its markup to preview it would hand a
    /// malicious mail exactly what it wants.
    /// </summary>
    private static readonly HashSet<string> TextLike =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".csv", ".tsv", ".md", ".json", ".xml", ".yml", ".yaml",
            ".ini", ".cfg", ".html", ".htm", ".svg", ".css", ".js", ".cs", ".py", ".java", ".sql",
        };

    private static bool IsViewable(string extension) =>
        ImageTypes.ContainsKey(extension) || TextLike.Contains(extension)
        || extension is ".pdf" or ".docx" or ".xlsx";

    public AttachmentViewerWindow(string filePath, string displayName)
    {
        InitializeComponent();
        MaximizeBoundsFix.Apply(this);
        Loaded += (_, _) => WindowCorners.Apply(this);

        _filePath = filePath;
        _displayName = displayName;

        Title = displayName;
        FileNameText.Text = displayName;
        FileGlyph.Text = GlyphFor(displayName);

        try
        {
            FileSizeText.Text = FormatSize(new FileInfo(filePath).Length);
        }
        catch (Exception)
        {
            FileSizeText.Text = "";
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

        Loaded += async (_, _) => await ShowAttachmentAsync();
    }

    private static string GlyphFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" or ".svg" => char.ConvertFromUtf32(0xEB9F), // Pictures
        ".pdf" => char.ConvertFromUtf32(0xEA90), // PDF
        ".zip" or ".rar" or ".7z" => char.ConvertFromUtf32(0xF012), // archive
        _ => char.ConvertFromUtf32(0xE8A5), // generic document
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    private async Task ShowAttachmentAsync()
    {
        var extension = Path.GetExtension(_filePath).ToLowerInvariant();

        if (!File.Exists(_filePath))
        {
            ShowFallback("That file isn't available", "It couldn't be downloaded or has been removed.");
            return;
        }

        if (!IsViewable(extension))
        {
            ShowFallback($"No preview for {extension.TrimStart('.').ToUpperInvariant()} files",
                "Save a copy or open it externally to view this one.");
            return;
        }

        try
        {
            await Viewer.EnsureCoreWebView2Async();

            // The viewer only ever shows one local file; nothing here should be able to navigate
            // itself somewhere else or open a window.
            Viewer.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Viewer.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Viewer.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Viewer.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;

            LoadingText.Visibility = Visibility.Collapsed;
            Viewer.Visibility = Visibility.Visible;

            if (ImageTypes.TryGetValue(extension, out var mimeType))
            {
                var bytes = await File.ReadAllBytesAsync(_filePath);
                if (bytes.LongLength <= MaxInlineImageBytes)
                {
                    // Inlined as a data URI because a NavigateToString page can't load file://
                    // resources — and going through a file:// page instead would mean writing a
                    // scratch HTML file next to every attachment.
                    Viewer.NavigateToString(RenderImage($"data:{mimeType};base64,{Convert.ToBase64String(bytes)}"));
                }
                else
                {
                    Viewer.CoreWebView2.Navigate(new Uri(_filePath).AbsoluteUri);
                }
                return;
            }

            if (extension == ".pdf")
            {
                // Edge's own PDF viewer brings paging, zoom, search and print for free.
                Viewer.CoreWebView2.Navigate(new Uri(_filePath).AbsoluteUri);
                return;
            }

            // .docx/.xlsx are just zipped XML — read the text/cell values straight out of the parts
            // with the BCL's own ZipFile/XDocument, no Office library needed. This is a plain-text
            // preview, not a document engine: no formatting, images, formulas, or (for a workbook)
            // any sheet but the first — good enough to see what's in the attachment before deciding
            // whether it's worth opening for real.
            if (extension == ".docx")
            {
                Viewer.NavigateToString(RenderAsText(ExtractDocxText(_filePath)));
                return;
            }

            if (extension == ".xlsx")
            {
                Viewer.NavigateToString(RenderSpreadsheet(ExtractXlsxRows(_filePath)));
                return;
            }

            Viewer.NavigateToString(RenderAsText(await File.ReadAllTextAsync(_filePath)));
        }
        catch (Exception ex)
        {
            ShowFallback("Couldn't open this attachment", ex.Message);
        }
    }

    /// <summary>
    /// Centres the image on the app's own soft backdrop. Navigating straight to the file would
    /// use WebView2's built-in image view, which follows the OS theme and drops a black page into
    /// the middle of an otherwise light app.
    /// </summary>
    private static string RenderImage(string dataUri) => $$"""
        <html><head><meta name="color-scheme" content="light"><style>
        :root { color-scheme: light; }
        html, body { height: 100%; margin: 0; background: #FBFAFC; }
        body { display: flex; align-items: center; justify-content: center; padding: 24px; box-sizing: border-box; }
        img {
            max-width: 100%; max-height: 100%; object-fit: contain;
            background: #ffffff; border-radius: 8px;
            box-shadow: 0 2px 18px rgba(0,0,0,0.10);
        }
        </style></head><body><img src="{{dataUri}}" alt=""></body></html>
        """;

    /// <summary>
    /// Escapes the file's contents into a plain, light-themed page. The explicit palette matters
    /// for the same reason it does in the reading pane: WebView2 otherwise follows the OS dark
    /// theme and paints a black page behind dark text.
    /// </summary>
    private static string RenderAsText(string content) => $$"""
        <html><head><meta name="color-scheme" content="light"><style>
        :root { color-scheme: light; }
        html, body { background: #ffffff; color: #1f1f1f; margin: 0; }
        pre {
            font-family: Consolas, 'Cascadia Mono', monospace;
            font-size: 13px; line-height: 1.55;
            padding: 20px 24px; margin: 0;
            white-space: pre-wrap; word-wrap: break-word;
        }
        </style></head><body><pre>{{WebUtility.HtmlEncode(content)}}</pre></body></html>
        """;

    /// <summary>Pulls the readable text out of a .docx's word/document.xml — every &lt;w:t&gt; run,
    /// joined back into paragraphs on &lt;w:p&gt; boundaries.</summary>
    private static string ExtractDocxText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml");
        if (entry is null)
            return "(Couldn't find readable text in this document.)";

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = doc.Descendants(w + "p")
            .Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value)))
            .Where(p => p.Length > 0);

        var text = string.Join("\n\n", paragraphs);
        return text.Length > 0 ? text : "(No readable text found in this document.)";
    }

    /// <summary>Pulls cell values out of an .xlsx's first worksheet, resolving shared-string
    /// references — capped at 200 rows since this is a quick look, not a spreadsheet engine.</summary>
    private static List<List<string>> ExtractXlsxRows(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var shared = new List<string>();
        var sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry is not null)
        {
            using var sharedStream = sharedEntry.Open();
            foreach (var si in XDocument.Load(sharedStream).Descendants(ns + "si"))
                shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
        }

        var sheetEntry = archive.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => e.FullName)
            .FirstOrDefault();
        var rows = new List<List<string>>();
        if (sheetEntry is null)
            return rows;

        using var sheetStream = sheetEntry.Open();
        foreach (var row in XDocument.Load(sheetStream).Descendants(ns + "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var raw = cell.Element(ns + "v")?.Value;
                var isSharedString = (string?)cell.Attribute("t") == "s";
                cells.Add(raw is null
                    ? ""
                    : isSharedString && int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Count
                        ? shared[idx]
                        : raw);
            }
            rows.Add(cells);
            if (rows.Count >= 200)
                break;
        }
        return rows;
    }

    private static string RenderSpreadsheet(List<List<string>> rows)
    {
        if (rows.Count == 0)
            return RenderAsText("(No readable data found — only the first sheet is previewed, with no formulas or formatting.)");

        var sb = new System.Text.StringBuilder("<table>");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row)
                sb.Append($"<td>{WebUtility.HtmlEncode(cell)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");

        return $$"""
            <html><head><meta name="color-scheme" content="light"><style>
            :root { color-scheme: light; }
            html, body { background: #ffffff; color: #1f1f1f; margin: 0; }
            table { border-collapse: collapse; font-family: Consolas, 'Cascadia Mono', monospace;
              font-size: 12.5px; margin: 16px; }
            td { border: 1px solid #e5e5e5; padding: 4px 9px; white-space: nowrap; }
            tr:first-child td { background: #f7f7f9; font-weight: 600; }
            </style></head><body>{{sb}}</body></html>
            """;
    }

    private void ShowFallback(string title, string detail)
    {
        LoadingText.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Collapsed;
        FallbackPanel.Visibility = Visibility.Visible;
        FallbackTitle.Text = title;
        FallbackDetail.Text = detail;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _displayName,
            Title = "Save attachment",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.Copy(_filePath, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't save the file: {ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenExternallyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute hands the file to whatever Windows has registered for it — only ever
            // on an explicit click, never as the default action.
            Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't open the file: {ex.Message}", "Open failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
