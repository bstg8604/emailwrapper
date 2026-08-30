using System.Net;
using System.Text.RegularExpressions;

namespace EmailClient.Automation;

/// <summary>
/// Turns message HTML into the plain text the rest of the app needs: list snippets, the quoted
/// original in a reply or forward, and the body of a draft being resumed.
///
/// Deliberately one implementation rather than three: a snippet produced by different code than
/// the quote is how a list row ends up previewing something the message doesn't actually say.
/// </summary>
public static class MailText
{
    private static readonly Regex ScriptOrStyle =
        new(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex LineBreak = new(@"<br\s*/?>", RegexOptions.IgnoreCase);
    // Table rows end a line; paragraphs and sections end a block. Treating them the same
    // double-spaces every list. `</li>` deliberately isn't here — the opening `<li>` already
    // starts its line, so closing it too would leave a blank line between every bullet.
    private static readonly Regex RowEnd = new(@"</tr\s*>", RegexOptions.IgnoreCase);
    private static readonly Regex BlockEnd =
        new(@"</(p|div|h[1-6]|ul|ol|blockquote|table)\s*>", RegexOptions.IgnoreCase);
    private static readonly Regex ListItem = new(@"<li\b[^>]*>", RegexOptions.IgnoreCase);
    private static readonly Regex AnyTag = new("<[^>]+>");
    private static readonly Regex RepeatedBlankLines = new(@"(\n\s*){3,}");
    private static readonly Regex RepeatedSpaces = new(@"[ \t]{2,}");

    /// <summary>Readable plain text from message HTML, with block structure kept as line breaks.</summary>
    public static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var text = ScriptOrStyle.Replace(html, "");
        text = LineBreak.Replace(text, "\n");
        // Flush-left rather than indented: a leading indent survives on some bullets and gets
        // eaten by the blank-line collapse on others, which looks like a ragged list.
        text = ListItem.Replace(text, "\n• ");
        text = RowEnd.Replace(text, "\n");
        text = BlockEnd.Replace(text, "\n\n");
        text = AnyTag.Replace(text, "");
        text = WebUtility.HtmlDecode(text);

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = RepeatedSpaces.Replace(text, " ");
        text = string.Join("\n", text.Split('\n').Select(line => line.TrimEnd()));
        text = RepeatedBlankLines.Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>The one-line preview shown under a subject in the message list.</summary>
    public static string Snippet(string html, int maxLength = 120)
    {
        var text = RepeatedSpaces.Replace(HtmlToPlainText(html).Replace('\n', ' '), " ").Trim();
        return text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength).TrimEnd(), "…");
    }

    /// <summary>Prefixes each line with "&gt; ", the plain-text quoting convention every mail client uses.</summary>
    public static string Quote(string plainText) =>
        string.Join("\n", plainText.Split('\n')
            .Select(line => line.Length == 0 ? ">" : "> " + line));

    /// <summary>"Academic Office &lt;academic@iitb.ac.in&gt;" → "Academic Office".</summary>
    public static string DisplayName(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return "";

        var open = address.IndexOf('<');
        if (open > 0)
            return address[..open].Trim().Trim('"').Trim();

        var at = address.IndexOf('@');
        return at > 0 ? address[..at].Trim() : address.Trim();
    }

    /// <summary>"Academic Office &lt;academic@iitb.ac.in&gt;" → "academic@iitb.ac.in".</summary>
    public static string AddressOnly(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return "";

        var open = address.IndexOf('<');
        var close = address.IndexOf('>', open + 1);
        return open >= 0 && close > open ? address[(open + 1)..close].Trim() : address.Trim();
    }

    /// <summary>
    /// Merges recipient lists for reply-all, dropping duplicates and anyone in
    /// <paramref name="exclude"/> — the user themselves, and the original sender who has already
    /// been promoted to the To line. Without this, reply-all copies you on your own message.
    /// </summary>
    public static string MergeRecipients(IEnumerable<string> exclude, params string?[] lists)
    {
        var excluded = new HashSet<string>(exclude.Select(AddressOnly), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var list in lists)
        {
            if (string.IsNullOrWhiteSpace(list))
                continue;

            foreach (var part in list.Split(',', ';'))
            {
                var recipient = part.Trim();
                if (recipient.Length == 0)
                    continue;

                var address = AddressOnly(recipient);
                if (excluded.Contains(address) || !seen.Add(address))
                    continue;

                merged.Add(recipient);
            }
        }

        return string.Join(", ", merged);
    }

    /// <summary>Compact date for a message-list row, the way Gmail/Outlook abbreviate it.</summary>
    public static string FormatListDate(DateTime when)
    {
        var today = DateTime.Today;
        if (when.Date == today)
            return when.ToString("h:mm tt");
        if (when.Date == today.AddDays(-1))
            return "Yesterday";
        if (when.Date > today.AddDays(-7))
            return when.ToString("ddd");
        return when.Year == today.Year ? when.ToString("d MMM") : when.ToString("d MMM yyyy");
    }

    /// <summary>Fuller date for the reading pane header and quote attribution lines.</summary>
    public static string FormatDetailDate(DateTime when)
    {
        var today = DateTime.Today;
        if (when.Date == today)
            return $"Today, {when:h:mm tt}";
        if (when.Date == today.AddDays(-1))
            return $"Yesterday, {when:h:mm tt}";
        return when.Year == today.Year
            ? when.ToString("ddd, d MMM, h:mm tt")
            : when.ToString("ddd, d MMM yyyy, h:mm tt");
    }
}
