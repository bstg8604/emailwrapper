namespace EmailClient.Automation;

/// <summary>
/// Splits a Gmail-style search query ("from:jayesh has:attachment \"budget report\" older_than:7d")
/// into structured operators plus whatever's left as plain free text — the same shape every
/// operator-aware search box (Gmail, Outlook) uses. Quoted values ("multi word", including right
/// after an operator's colon) are kept intact as one token; everything else splits on whitespace.
/// </summary>
public static class SearchQueryParser
{
    public sealed record ParsedQuery(
        string? From, string? To, string? Subject,
        bool? Unread, bool? Starred, bool? HasAttachment,
        DateTime? OlderThan, DateTime? NewerThan,
        long? LargerThanBytes, long? SmallerThanBytes,
        string FreeText)
    {
        /// <summary>True once any operator was recognized — lets a caller skip operator-filtering
        /// logic entirely for the (overwhelmingly common) plain-text-only search.</summary>
        public bool HasOperators =>
            From is not null || To is not null || Subject is not null
            || Unread is not null || Starred is not null || HasAttachment is not null
            || OlderThan is not null || NewerThan is not null
            || LargerThanBytes is not null || SmallerThanBytes is not null;
    }

    public static ParsedQuery Parse(string query)
    {
        string? from = null, to = null, subject = null;
        bool? unread = null, starred = null, hasAttachment = null;
        DateTime? olderThan = null, newerThan = null;
        long? largerThan = null, smallerThan = null;
        var freeTextParts = new List<string>();

        foreach (var token in Tokenize(query ?? ""))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1)
            {
                freeTextParts.Add(Unquote(token));
                continue;
            }

            var key = token[..colon].ToLowerInvariant();
            var value = Unquote(token[(colon + 1)..]);

            switch (key)
            {
                case "from":
                    from = value;
                    break;
                case "to":
                    to = value;
                    break;
                case "subject":
                    subject = value;
                    break;
                case "has" when value.Equals("attachment", StringComparison.OrdinalIgnoreCase):
                    hasAttachment = true;
                    break;
                case "is" when value.Equals("unread", StringComparison.OrdinalIgnoreCase):
                    unread = true;
                    break;
                case "is" when value.Equals("read", StringComparison.OrdinalIgnoreCase):
                    unread = false;
                    break;
                case "is" when value.Equals("starred", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("flagged", StringComparison.OrdinalIgnoreCase):
                    starred = true;
                    break;
                case "older_than" when TryParseRelativeAge(value, out var age):
                    olderThan = DateTime.UtcNow - age;
                    break;
                case "newer_than" when TryParseRelativeAge(value, out var age2):
                    newerThan = DateTime.UtcNow - age2;
                    break;
                case "larger" when TryParseSize(value, out var largerBytes):
                    largerThan = largerBytes;
                    break;
                case "smaller" when TryParseSize(value, out var smallerBytes):
                    smallerThan = smallerBytes;
                    break;
                // An unrecognized "key:value" shape (a stray colon in ordinary text, e.g. a time
                // "10:30" someone typed) is kept as free text rather than silently discarded — the
                // user typed it expecting it to be searched for, operator syntax or not.
                default:
                    freeTextParts.Add(token);
                    break;
            }
        }

        return new ParsedQuery(from, to, subject, unread, starred, hasAttachment,
            olderThan, newerThan, largerThan, smallerThan, string.Join(' ', freeTextParts));
    }

    /// <summary>Splits on whitespace, except inside a double-quoted run — "trip report" (bare, or
    /// right after an operator's colon as in subject:"trip report") stays one token instead of
    /// being split into "trip and report" and silently misparsed as two free-text words.</summary>
    private static IEnumerable<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in query)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>Gmail's own relative-age syntax: a number followed by d(ays)/m(onths)/y(ears).</summary>
    private static bool TryParseRelativeAge(string value, out TimeSpan age)
    {
        age = default;
        if (value.Length < 2)
            return false;
        var unit = char.ToLowerInvariant(value[^1]);
        if (!int.TryParse(value[..^1], out var amount) || amount <= 0)
            return false;

        age = unit switch
        {
            'd' => TimeSpan.FromDays(amount),
            'm' => TimeSpan.FromDays(amount * 30),
            'y' => TimeSpan.FromDays(amount * 365),
            _ => TimeSpan.Zero,
        };
        return age > TimeSpan.Zero;
    }

    /// <summary>Gmail's own size syntax: a number optionally followed by K/M (kilobytes/megabytes);
    /// a bare number is bytes.</summary>
    private static bool TryParseSize(string value, out long bytes)
    {
        bytes = 0;
        if (value.Length == 0)
            return false;

        var unit = char.ToLowerInvariant(value[^1]);
        var (numberPart, multiplier) = unit switch
        {
            'k' => (value[..^1], 1024L),
            'm' => (value[..^1], 1024L * 1024),
            'g' => (value[..^1], 1024L * 1024 * 1024),
            _ => (value, 1L),
        };

        if (!double.TryParse(numberPart, out var amount) || amount <= 0)
            return false;

        bytes = (long)(amount * multiplier);
        return true;
    }
}
