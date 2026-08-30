using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EmailClient.Automation;

public sealed record InboxRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("unread")] bool Unread,
    [property: JsonPropertyName("starred")] bool Starred = false,
    [property: JsonPropertyName("hasAttachment")] bool HasAttachment = false,
    // Sample rows carry the real instant so they can be sorted; IMAP rows carry it too now that
    // there's no scraped-label limitation forcing it to be null.
    [property: JsonPropertyName("timestamp")] DateTime? Timestamp = null)
{
    public string Initial
    {
        get
        {
            var trimmed = Sender?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return "?";
            var firstLetter = trimmed.FirstOrDefault(char.IsLetterOrDigit);
            return firstLetter == default ? "?" : char.ToUpperInvariant(firstLetter).ToString();
        }
    }

    // IITB course codes are consistently DEPT+3-digit-number ("CS305", "ME224") — spotting them in
    // the subject line lets the list surface a course chip without any per-message metadata from
    // the server, which real IMAP obviously doesn't carry.
    private static readonly Regex CourseCodePattern = new(@"\b([A-Z]{2,4}\d{3})\b");

    public string? CourseCode
    {
        get
        {
            var match = CourseCodePattern.Match(Subject ?? "");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    public bool HasCourseCode => CourseCode is not null;

    // Apple Mail's conversation view groups by subject once reply/forward prefixes are stripped —
    // "Trip to South America", "Re: Trip to South America" and "Fwd: Re: Trip to South America"
    // are all the same conversation. Repeated (not just leading-once) so "Re: Re: X" collapses too.
    private static readonly Regex ReplyPrefixPattern = new(@"^\s*(re|fw|fwd)\s*:\s*", RegexOptions.IgnoreCase);

    public string ConversationKey
    {
        get
        {
            var subject = Subject ?? "";
            string previous;
            do
            {
                previous = subject;
                subject = ReplyPrefixPattern.Replace(subject, "");
            } while (subject != previous);
            return subject.Trim().ToLowerInvariant();
        }
    }
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
    [property: JsonPropertyName("attachments")] IReadOnlyList<MailAttachment>? Attachments = null,
    // Never scraped/fetched from a received message — carried so a draft reopened from Drafts
    // comes back with its Bcc line intact instead of silently losing those recipients.
    [property: JsonPropertyName("bcc")] string Bcc = "");

/// <summary>One real IMAP folder/mailbox as the sidebar renders it.</summary>
public sealed record MailFolder(
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unread")] int Unread,
    [property: JsonPropertyName("depth")] int Depth = 0);

/// <summary>One page of a folder's message list.</summary>
public sealed record MessagePage(
    [property: JsonPropertyName("rows")] IReadOnlyList<InboxRow> Rows,
    [property: JsonPropertyName("page")] int Page = 1,
    [property: JsonPropertyName("pageCount")] int PageCount = 1,
    [property: JsonPropertyName("total")] int Total = 0,
    [property: JsonPropertyName("mailbox")] string Mailbox = "")
{
    public static readonly MessagePage Empty = new([]);
}
