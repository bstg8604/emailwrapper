using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace EmailClient.Automation;

/// <summary>
/// Generates real files behind the sample attachments, so the in-app viewer has genuine bytes to
/// render rather than a placeholder. A sample PDF that isn't a real PDF would only prove the
/// viewer can show an error message.
///
/// DELETE alongside <see cref="MockData"/> once live attachments are working end-to-end.
/// </summary>
public static class SampleAttachments
{
    /// <summary>Writes the file for this attachment name if it isn't already on disk, and returns its path.</summary>
    public static string EnsureFile(string cacheDirectory, string fileName)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, SafeName(fileName));
        if (File.Exists(path))
            return path;

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png":
                File.WriteAllBytes(path, BuildPng(Path.GetFileNameWithoutExtension(fileName)));
                break;
            case ".pdf":
                File.WriteAllBytes(path, BuildPdf(TitleFor(fileName), BodyLinesFor(fileName)));
                break;
            case ".docx":
                File.WriteAllBytes(path, BuildDocx(TitleFor(fileName), BodyLinesFor(fileName)));
                break;
            case ".xlsx":
                File.WriteAllBytes(path, BuildXlsx(XlsxRowsFor(fileName)));
                break;
            default:
                File.WriteAllText(path, string.Join(Environment.NewLine, BodyLinesFor(fileName)), Encoding.UTF8);
                break;
        }
        return path;
    }

    public static string SafeName(string fileName)
    {
        var cleaned = new string([.. fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        return string.IsNullOrWhiteSpace(cleaned) ? "attachment" : cleaned;
    }

    private static string TitleFor(string fileName) => fileName switch
    {
        "midsem-schedule.pdf" => "Mid-semester Examination Schedule",
        "assignment3.pdf" => "CS 305 — Assignment 3",
        "grade-card-sem5.pdf" => "Grade Card — Semester 5",
        _ => Path.GetFileNameWithoutExtension(fileName),
    };

    private static IReadOnlyList<string> BodyLinesFor(string fileName) => fileName switch
    {
        "midsem-schedule.pdf" =>
        [
            "Academic Year 2026-27",
            "",
            "Date        Slot    Course     Venue",
            "Mon 08      09:00   CS 305     LA 201",
            "Tue 09      09:00   MA 214     LA 202",
            "Wed 10      14:00   CS 337     LH 301",
            "Fri 12      09:00   HS 301     LA 105",
            "",
            "Report 15 minutes before the start of each paper.",
            "Bring your institute ID card without fail.",
        ],
        "assignment3.pdf" =>
        [
            "Submitted by: you@iitb.ac.in",
            "",
            "Q1. Derivation of the update rule.",
            "Q2. Convergence argument for the stated learning rate.",
            "Q3. Empirical comparison against the baseline split.",
            "",
            "See the appendix for the full derivation of part (b).",
        ],
        "grade-card-sem5.pdf" =>
        [
            "Course     Credits   Grade",
            "CS 305     6         AA",
            "MA 214     8         AB",
            "CS 337     6         AA",
            "HS 301     6         BB",
            "",
            "SPI: 9.21     CPI: 8.94",
        ],
        "meeting-notes.docx" or "meeting-notes.txt" =>
        [
            "Project sync — notes",
            "=====================",
            "",
            "Discussed the evaluation split and the data loader rewrite.",
            "Agreed to freeze the feature set before Thursday's review.",
            "",
            "Open questions:",
            "  - Do we report macro or micro averages?",
            "  - Who owns the ablation table?",
        ],
        "action-items.txt" =>
        [
            "ACTION ITEMS",
            "",
            "[ ] Rohan  - finish the data loader",
            "[ ] Priya  - draft the evaluation section",
            "[ ] You    - rerun the baseline with the new split",
        ],
        _ =>
        [
            $"Sample attachment: {fileName}",
            "",
            "Generated so the in-app viewer has real content to display while the app is",
            "running on sample data. Replaced by the genuine file once live webmail is wired up.",
        ],
    };

    private static IReadOnlyList<IReadOnlyList<string>> XlsxRowsFor(string fileName) => fileName switch
    {
        "baseline-v2.csv" or "baseline-results.xlsx" =>
        [
            ["Split", "Precision", "Recall", "F1"],
            ["Train", "0.91", "0.88", "0.89"],
            ["Val", "0.84", "0.79", "0.81"],
            ["Test (fixed)", "0.80", "0.77", "0.78"],
        ],
        _ =>
        [
            ["Item", "Value"],
            ["Sample attachment", fileName],
            ["Generated by", "Purplemail sample data"],
        ],
    };

    // ---- DOCX ----------------------------------------------------------------------------------

    /// <summary>A genuinely valid, minimal .docx — Word (or this app's own preview) can both open
    /// it, unlike a plain-text file with a renamed extension.</summary>
    private static byte[] BuildDocx(string title, IReadOnlyList<string> lines)
    {
        var paragraphs = new StringBuilder();
        paragraphs.Append($"<w:p><w:r><w:rPr><w:b/><w:sz w:val=\"32\"/></w:rPr><w:t xml:space=\"preserve\">{XmlEscape(title)}</w:t></w:r></w:p>");
        foreach (var line in lines)
        {
            paragraphs.Append(string.IsNullOrWhiteSpace(line)
                ? "<w:p/>"
                : $"<w:p><w:r><w:t xml:space=\"preserve\">{XmlEscape(line)}</w:t></w:r></w:p>");
        }

        var documentXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            $"<w:body>{paragraphs}</w:body></w:document>";

        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>");
            WriteEntry(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");
            WriteEntry(zip, "word/document.xml", documentXml);
        }
        return stream.ToArray();
    }

    // ---- XLSX ----------------------------------------------------------------------------------

    /// <summary>A genuinely valid, minimal single-sheet .xlsx, all values via shared strings —
    /// the same convention a real Excel-produced workbook uses.</summary>
    private static byte[] BuildXlsx(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var shared = new List<string>();
        int SharedIndexOf(string value)
        {
            var existing = shared.IndexOf(value);
            if (existing >= 0)
                return existing;
            shared.Add(value);
            return shared.Count - 1;
        }

        var sheetRows = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            sheetRows.Append($"<row r=\"{r + 1}\">");
            var cols = rows[r];
            for (var c = 0; c < cols.Count; c++)
            {
                var cellRef = $"{ColumnLetter(c)}{r + 1}";
                sheetRows.Append($"<c r=\"{cellRef}\" t=\"s\"><v>{SharedIndexOf(cols[c])}</v></c>");
            }
            sheetRows.Append("</row>");
        }

        var sharedStringsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{shared.Count}\" uniqueCount=\"{shared.Count}\">" +
            string.Concat(shared.Select(s => $"<si><t xml:space=\"preserve\">{XmlEscape(s)}</t></si>")) +
            "</sst>";

        var sheetXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            $"<sheetData>{sheetRows}</sheetData></worksheet>";

        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
                "</Types>");
            WriteEntry(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            WriteEntry(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
                "</Relationships>");
            WriteEntry(zip, "xl/worksheets/sheet1.xml", sheetXml);
            WriteEntry(zip, "xl/sharedStrings.xml", sharedStringsXml);
        }
        return stream.ToArray();
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ColumnLetter(int index)
    {
        var letter = "";
        index++;
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            letter = (char)('A' + rem) + letter;
            index = (index - 1) / 26;
        }
        return letter;
    }

    private static string XmlEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---- PNG ---------------------------------------------------------------------------------

    private static byte[] BuildPng(string caption)
    {
        using var bitmap = new Bitmap(720, 405);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.FromArgb(0xF8, 0xF6, 0xFB));

        using var accent = new SolidBrush(Color.FromArgb(0x6D, 0x28, 0xD9));
        using var accentSoft = new SolidBrush(Color.FromArgb(0x8B, 0x5C, 0xF6));
        using var ink = new SolidBrush(Color.FromArgb(0x33, 0x33, 0x33));
        using var muted = new SolidBrush(Color.FromArgb(0x9A, 0x9A, 0x9A));

        // A simple bar chart, so the sample image is recognisably a picture of something.
        int[] values = [58, 92, 47, 118, 76, 134];
        var baseline = 330;
        for (var i = 0; i < values.Length; i++)
        {
            var x = 90 + (i * 96);
            graphics.FillRectangle(i % 2 == 0 ? accent : accentSoft,
                x, baseline - values[i], 56, values[i]);
        }
        graphics.DrawLine(new Pen(Color.FromArgb(0xDD, 0xDD, 0xDD), 2), 70, baseline, 660, baseline);

        using var titleFont = new Font("Segoe UI", 18, System.Drawing.FontStyle.Bold);
        using var captionFont = new Font("Segoe UI", 11);
        graphics.DrawString(caption, titleFont, ink, 70, 40);
        graphics.DrawString("Sample image attachment — rendered inside the app", captionFont, muted, 70, 356);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    // ---- PDF ---------------------------------------------------------------------------------

    /// <summary>
    /// Builds a genuine single-page PDF. Byte offsets for the cross-reference table are recorded
    /// as the file is written rather than hardcoded — a wrong offset is what makes a hand-built
    /// PDF fail to open.
    /// </summary>
    private static byte[] BuildPdf(string title, IReadOnlyList<string> lines)
    {
        var content = new StringBuilder();
        content.Append("BT\n/F1 18 Tf\n72 780 Td\n(").Append(EscapePdfText(title)).Append(") Tj\nET\n");

        var y = 744;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                content.Append("BT\n/F1 11 Tf\n72 ").Append(y).Append(" Td\n(")
                       .Append(EscapePdfText(line)).Append(") Tj\nET\n");
            }
            y -= 18;
            if (y < 60)
                break;
        }

        var contentBytes = Encoding.ASCII.GetBytes(content.ToString());

        string[] objects =
        [
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]" +
                "/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        ];

        using var stream = new MemoryStream();
        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));

        Write("%PDF-1.4\n");

        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(stream.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        offsets.Add(stream.Position);
        Write($"5 0 obj\n<</Length {contentBytes.Length}>>\nstream\n");
        stream.Write(contentBytes);
        Write("\nendstream\nendobj\n");

        var xrefPosition = stream.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");

        Write($"trailer\n<</Size {offsets.Count + 1}/Root 1 0 R>>\nstartxref\n{xrefPosition}\n%%EOF\n");
        return stream.ToArray();
    }

    /// <summary>Helvetica/WinAnsi can't carry arbitrary Unicode, and (, ) and \ end a PDF string.</summary>
    private static string EscapePdfText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '(': builder.Append("\\("); break;
                case ')': builder.Append("\\)"); break;
                case '—' or '–': builder.Append('-'); break;
                case '‘' or '’': builder.Append('\''); break;
                case '“' or '”': builder.Append('"'); break;
                default: builder.Append(c <= 0x7E ? c : '?'); break;
            }
        }
        return builder.ToString();
    }
}
