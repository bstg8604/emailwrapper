using System.Net;
using System.Text.RegularExpressions;
using System.Linq;
using MimeKit;

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
    // A naive "<[^>]+>" stops at the first literal '>' it finds, including one sitting inside a
    // quoted attribute value (e.g. <img alt="1 > 2" src="x.png">) — that used to leave the tag's
    // own tail (' 2" src="x.png">') as visible garbage in the plain-text output. This version
    // treats a quoted span as opaque so a '>' inside quotes doesn't end the tag early.
    private static readonly Regex AnyTag = new(@"<[^>""']*(?:(?:""[^""]*""|'[^']*')[^>""']*)*>");
    private static readonly Regex RepeatedBlankLines = new(@"(\n\s*){3,}");
    // Also collapses runs of U+00A0 (non-breaking space) alongside plain space/tab —
    // Outlook's own HTML commonly indents with runs of &nbsp; instead of real spacing,
    // which otherwise survived this collapse intact and showed up as odd gaps in snippets/
    // quotes. Written as an escape, never the literal character, so it can't be silently
    // dropped by a tool that mishandles non-ASCII bytes.
    private static readonly Regex RepeatedSpaces = new("[ \t\u00A0]{2,}");

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
        if (text.Length <= maxLength)
            return text;

        // A raw char-count cut can land inside a surrogate pair (an emoji, or anything outside the
        // BMP) — common in emoji-laden newsletter/marketing HTML — leaving an orphaned high
        // surrogate that renders as a broken glyph. Back off one position when that's about to
        // happen so the pair stays whole.
        var cut = maxLength;
        if (char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]))
            cut--;

        return string.Concat(text.AsSpan(0, cut).TrimEnd(), "…");
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
    /// Splits a recipient/address-list string into its real individual entries via MimeKit's own
    /// parser — never a naive comma-split. A quoted display name can legitimately contain its own
    /// comma ("Doe, John" &lt;john@x.com&gt; is valid RFC 5322), and splitting on every literal
    /// comma cuts that one recipient in two: a dangling "Doe fragment (with a stray leading quote
    /// character) counted as its own separate recipient, and " John" &lt;john@x.com&gt; as another
    /// — inflating the real count and corrupting the display text, which is exactly what a live
    /// report of a mangled, prematurely-collapsed "and N more" To line turned out to be. Same fix
    /// RecipientBox's own ParseAddresses already applies to compose's recipient chips; this brings
    /// the reading pane's display formatting in line with it.
    /// </summary>
    private static List<string> SplitAddressList(string recipients)
    {
        if (string.IsNullOrWhiteSpace(recipients))
            return [];

        if (InternetAddressList.TryParse(recipients, out var list) && list.Count > 0)
            return list.Select(a => a.ToString()).ToList();

        // Didn't parse as a real address list at all — falls back to the old behavior rather than
        // losing the text outright; shouldn't normally happen for text this app itself already
        // round-tripped through IMAP/compose.
        return recipients.Split(',').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
    }

    /// <summary>
    /// Normalizes a comma-separated recipient list for display: an address that already carries a
    /// display name is left as "Name &lt;email&gt;"; a bare address is shown as-is (just the email),
    /// matching Gmail/Outlook's own convention rather than inventing punctuation around it.
    /// </summary>
    public static string FormatRecipientList(string recipients) =>
        string.Join(", ", SplitAddressList(recipients));

    /// <summary>
    /// Splits a comma-separated recipient list into what should be shown outright versus collapsed
    /// behind an "and N more" — for a caller (the reading pane) that wants to build a clickable
    /// toggle that actually expands to reveal the rest, rather than a dead-end summary with no way
    /// to see who else was on the list.
    /// </summary>
    public static (List<string> Shown, List<string> Hidden) SplitRecipients(string recipients, int maxShown = 4)
    {
        var all = SplitAddressList(recipients);
        return all.Count <= maxShown
            ? (all, [])
            : (all.Take(maxShown).ToList(), all.Skip(maxShown).ToList());
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

    /// <summary>
    /// Always an absolute date, never "Today"/"Yesterday" — for anywhere that date gets baked into
    /// permanent text (a reply/forward's quote attribution, a forwarded message's "Date:" line).
    /// Relative wording there would freeze at whatever it said when composed and read wrong forever
    /// after — Gmail, Outlook and Apple Mail all use an absolute date in a quote attribution for
    /// exactly this reason, even though their own reading-pane headers use relative wording.
    /// </summary>
    public static string FormatAbsoluteDate(DateTime when) => when.Year == DateTime.Today.Year
        ? when.ToString("ddd, d MMM, h:mm tt")
        : when.ToString("ddd, d MMM yyyy, h:mm tt");

    /// <summary>Fuller date for the reading pane header — relative wording is fine here since it's
    /// only ever shown live, never baked into text that outlives "today"/"yesterday" meaning
    /// anything (see <see cref="FormatAbsoluteDate"/> for that case).</summary>
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
