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
    [property: JsonPropertyName("timestamp")] DateTime? Timestamp = null,
    // The raw sender address (never a display name) — kept alongside Sender so the list can tell
    // whether a message is from outside the signed-in account's domain without needing to reopen it.
    [property: JsonPropertyName("senderAddress")] string SenderAddress = "")
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

    // Set once by MainWindow when an account is signed into (or mock data selected) — the row
    // itself has no idea what "your own domain" is, so it can't compute this on its own.
    public static string SelfDomain { get; set; } = "";

    /// <summary>True when this row's sender domain doesn't match <see cref="SelfDomain"/> — mirrors
    /// the reading pane's own external-sender check, just against the address kept on the row
    /// instead of a freshly-opened MessageDetail.</summary>
    public bool IsExternalSender
    {
        get
        {
            var senderDomain = SenderAddress.Split('@').ElementAtOrDefault(1);
            if (string.IsNullOrEmpty(senderDomain) || string.IsNullOrEmpty(SelfDomain))
                return false;
            return !senderDomain.Equals(SelfDomain, StringComparison.OrdinalIgnoreCase)
                && !senderDomain.EndsWith("." + SelfDomain, StringComparison.OrdinalIgnoreCase);
        }
    }

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
    [property: JsonPropertyName("bcc")] string Bcc = "",
    // Set only when the message actually carries a text/calendar part (a real Outlook/Google
    // Calendar invite), parsed with Ical.Net — not a guess scraped from the body text the way a
    // bare Zoom/Meet/Teams link in an ordinary message has to be.
    [property: JsonPropertyName("calendar")] CalendarInvite? Calendar = null,
    // Date is already formatted for display (relative "Today"/"Yesterday" wording, matching the
    // reading pane header) — that's wrong once baked into a reply/forward's quote attribution,
    // which is permanent text: "Yesterday" sent today reads as "Yesterday" forever after. This is
    // the raw instant so quote-building can format an absolute date instead.
    [property: JsonPropertyName("timestamp")] DateTime? Timestamp = null);

/// <summary>A real calendar invite (RFC 5545 VEVENT) found as a message's text/calendar part.</summary>
public sealed record CalendarInvite(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("start")] DateTimeOffset? Start,
    [property: JsonPropertyName("location")] string? Location,
    // A Zoom/Meet/Teams link pulled from the invite's own description/location, not the message
    // body — an invite can (and often does) carry the join link nowhere else.
    [property: JsonPropertyName("joinUrl")] string? JoinUrl);

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
