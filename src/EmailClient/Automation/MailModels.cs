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
    [property: JsonPropertyName("senderAddress")] string SenderAddress = "",
    // Null means "whatever folder is currently open" (every existing caller before cross-folder
    // conversation siblings existed) — only ever set explicitly for a row that was found in a
    // *different* folder than the one currently showing (see
    // ImapMailBackend.FindConversationSiblingsAsync), so every operation that later needs to act
    // on this specific row (opening its body, downloading an attachment) knows which folder to
    // select first. IMAP UIDs are only unique within one folder, not across the whole mailbox, so
    // treating a cross-folder row's id as if it belonged to the active folder risks operating on
    // an entirely unrelated message that happens to share the same UID number.
    [property: JsonPropertyName("mailbox")] string? Mailbox = null)
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

    /// <summary>
    /// What a screen reader should call this row. The visible row is a stack of separate
    /// TextBlocks, which UI Automation would otherwise read out as four unrelated fragments with
    /// no indication of which message they belong to or whether it's been read.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var state = Unread ? "Unread" : "Read";
            var extras = (Starred ? ", starred" : "") + (HasAttachment ? ", has attachment" : "");
            return $"{state} message from {Sender}. {Subject}. {Date}{extras}";
        }
    }

    /// <summary>Per-row star button label — "Star" alone gives no clue which row it acts on.</summary>
    public string StarAccessibleName =>
        (Starred ? "Unstar message from " : "Star message from ") + Sender;

    /// <summary>Per-row bulk-select checkbox label, same reasoning as <see cref="StarAccessibleName"/>.</summary>
    public string SelectAccessibleName => $"Select message from {Sender}, {Subject}";

    // Set once by MainWindow when an account is signed into (or mock data selected) — the row
    // itself has no idea what "your own domain" is, so it can't compute this on its own.
    public static string SelfDomain { get; set; } = "";

    /// <summary>Kept in sync with AppSettings.VipSenders by MainWindow (same pattern as
    /// <see cref="SelfDomain"/>) — VIP status is per-sender-address app settings, not IMAP data, so
    /// a row can't know it on its own either.</summary>
    public static HashSet<string> VipSenders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apple Mail's VIP concept — a sender flagged for visual priority in the message
    /// list, independent of Starred (which flags one message, not everything from someone).</summary>
    public bool IsVip => !string.IsNullOrEmpty(SenderAddress) && VipSenders.Contains(SenderAddress);

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

    /// <summary>Kept in sync by MainWindow.MessageList.cs's UpdateThreadCounts, same pattern as
    /// <see cref="SelfDomain"/> — how many currently-listed rows share this row's ConversationKey.
    /// A row can't compute this on its own since it only knows its own subject, not its
    /// neighbors'.</summary>
    public static Dictionary<string, int> ThreadCounts { get; set; } = new();

    /// <summary>Apple Mail's small thread-count numeral next to the subject — see the badge's own
    /// XAML comment for why this is informational only (never hides a row).</summary>
    public int ThreadCount => ThreadCounts.TryGetValue(ConversationKey, out var c) ? c : 1;

    public bool HasThread => ThreadCount > 1;

    // Apple Mail's conversation view groups by subject once reply/forward prefixes are stripped —
    // "Trip to South America", "Re: Trip to South America" and "Fwd: Re: Trip to South America"
    // are all the same conversation. Repeated (not just leading-once) so "Re: Re: X" collapses too.
    // Beyond plain English re/fw/fwd: Outlook's own numbered form "Re[2]:"/"Re(2):"; and the other
    // languages/clients an IIT Bombay inbox plausibly sees mail through — AW (German), SV/VS
    // (Swedish/Finnish), TR (French transfert), WG (German weitergeleitet, "forwarded"), RIF
    // (Italian), ODP (Polish). Missing one of these used to leave a real reply's ConversationKey
    // permanently different from its own thread's — silently dropping it out of the conversation
    // view rather than threading it, which read as "sometimes mail just doesn't thread right."
    private static readonly Regex ReplyPrefixPattern =
        new(@"^\s*(re|fw|fwd|aw|sv|vs|tr|wg|rif|odp)(\s*\[\d+\]|\s*\(\d+\))?\s*:\s*", RegexOptions.IgnoreCase);

    // A leading bracketed-tag strip ("[EXTERNAL] Re: X" -> "X") was tried here and reverted — a
    // live report of a conversation view showing an unrelated sender's whole mail history pointed
    // at it (and at the then-existing subject-fallback in ImapMailBackend.FindConversationSiblingsAsync,
    // since removed entirely — that method now matches only real Message-ID/References links, the
    // same as Apple Mail/Thunderbird) as prime suspects: stripping an arbitrary "[...]" wrapper
    // risks collapsing many of one sender's genuinely-unrelated subjects down to the same remaining
    // text whenever they share a common tag (a course code, a mailing list name). Given the actual
    // conversation view no longer trusts subject text at all for live mail, this bracket-stripping
    // idea is safe to revisit if it's ever wanted — it isn't restored here only because nothing
    // has asked for it since.

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
    [property: JsonPropertyName("timestamp")] DateTime? Timestamp = null,
    // RFC 5322 Message-ID/References headers — the authoritative way real mail clients thread a
    // conversation, unlike guessing from the subject line (see InboxRow.ConversationKey, which
    // misses a reply whose subject was hand-edited or that uses a non-English "Re:" equivalent,
    // and can also wrongly merge two unrelated messages that happen to share a generic subject).
    // Used as a second, more reliable signal alongside the subject match in
    // MainWindow.GatherConversationAsync / ImapMailBackend.FindConversationSiblingsAsync.
    [property: JsonPropertyName("messageId")] string? MessageId = null,
    [property: JsonPropertyName("references")] IReadOnlyList<string>? References = null,
    // RFC 2369 List-Unsubscribe — set only when the sender actually declared one (newsletters,
    // mailing lists), never guessed from body text. UnsubscribeUrl is preferred when a sender
    // offers both, since it needs no compose step; UnsubscribeMailto is the fallback for senders
    // who only support the older mailto: form.
    [property: JsonPropertyName("unsubscribeUrl")] string? UnsubscribeUrl = null,
    [property: JsonPropertyName("unsubscribeMailto")] string? UnsubscribeMailto = null);

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
