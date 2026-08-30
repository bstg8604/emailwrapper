namespace EmailClient.Automation;

/// <summary>
/// One sample message. Both the list row and the reading-pane body are derived from this, so a
/// row's snippet, date and attachment marker always match the message it actually opens.
/// </summary>
public sealed record MockMessage(
    string Id,
    string Folder,
    string From,
    string Subject,
    string BodyHtml,
    TimeSpan Ago,
    string To = "you@iitb.ac.in",
    string Cc = "",
    bool Unread = false,
    bool Starred = false,
    IReadOnlyList<MailAttachment>? Attachments = null)
{
    public DateTime When => DateTime.Now - Ago;

    /// <summary>
    /// Sent and Drafts list the person the mail is going *to* — showing "You" on every row of
    /// your own Sent folder tells you nothing, which is why Gmail and Outlook both show the
    /// recipient there instead.
    /// </summary>
    public string RowSender => Folder is "Sent" or "Drafts"
        ? (string.IsNullOrWhiteSpace(To) ? "(no recipient)" : MailText.DisplayName(To))
        : MailText.DisplayName(From);

    public InboxRow ToRow() => new(
        Id,
        RowSender,
        string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject,
        MailText.Snippet(BodyHtml),
        MailText.FormatListDate(When),
        Unread,
        Starred,
        Attachments is { Count: > 0 },
        When);

    public MessageDetail ToDetail() => new(
        string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject,
        From,
        MailText.FormatDetailDate(When),
        BodyHtml,
        To,
        Cc,
        Attachments);
}

/// <summary>
/// Dummy mail data so the custom UI can be built/polished without needing a completed
/// live IMAP login every time. DELETE this file and the
/// `UseMockData` flag in MainWindow.xaml.cs once real live data is working end-to-end.
///
/// Dates are relative to "now" rather than hardcoded strings, so the sample inbox keeps looking
/// plausible instead of claiming everything arrived on a fixed day months ago.
/// </summary>
public static class MockData
{
    /// <summary>The signed-in user, so reply-all can keep them off their own recipient list.</summary>
    public const string SelfAddress = "you@iitb.ac.in";
    public const string SelfIdentity = "You <you@iitb.ac.in>";

    public static readonly IReadOnlyList<MockMessage> All =
    [
        // ---- Inbox ----------------------------------------------------------------------
        new("m1", "Inbox", "Academic Office <academic@iitb.ac.in>",
            "Mid-semester exam schedule released",
            "<p>Dear Student,</p>" +
            "<p>The mid-semester examination schedule for AY 2026-27 has been published on ASC. " +
            "Please check your slot carefully and report to the allotted room <b>15 minutes before</b> " +
            "the start of your paper.</p>" +
            "<ul><li>Bring your institute ID card without fail.</li>" +
            "<li>Calculators are permitted only for the courses listed in the attached schedule.</li>" +
            "<li>Requests for slot changes close on the 12th.</li></ul>" +
            "<p>Regards,<br/>Academic Office</p>",
            TimeSpan.FromHours(2),
            Unread: true, Starred: true,
            Attachments: [new MailAttachment("midsem-schedule.pdf", "12 KB")]),

        new("m2", "Inbox", "Ananya Rao <ananya.rao@cse.iitb.ac.in>",
            "Re: Extension for assignment 3",
            "<p>Sure, I can give you until Friday. Please make sure to submit via Moodle before " +
            "midnight — the portal closes automatically and I can't reopen it afterwards.</p>" +
            "<p>Do include the derivation for part (b); several people skipped it last time.</p>" +
            "<p>— Prof. Rao</p>",
            TimeSpan.FromHours(3),
            Cc: "ta-cs305@cse.iitb.ac.in",
            Unread: true),

        new("m3", "Inbox", "Hostel Office <hostel@iitb.ac.in>",
            "Mess bill due reminder",
            "<p>This is a reminder that your mess bill for the month is due on the 5th.</p>" +
            "<p>Late payments attract a fine of ₹50 per day. You can pay through the hostel portal " +
            "or at the office counter between 10 AM and 4 PM on working days.</p>",
            TimeSpan.FromHours(20)),

        new("m4", "Inbox", "Placement Cell <placement@iitb.ac.in>",
            "Pre-placement talk — Tuesday 6 PM",
            "<p>You are invited to attend the pre-placement talk being held in <b>LH-301</b> this " +
            "Tuesday at 6 PM. Attendance is mandatory for registered students.</p>" +
            "<p><img src=\"https://example.com/placement-banner.png\" alt=\"Placement banner\"/></p>" +
            "<p>Please carry your registration slip.</p>",
            TimeSpan.FromHours(27),
            Cc: "cse-batch2027@iitb.ac.in",
            Starred: true),

        new("m5", "Inbox", "Rohan Mehta <rohan.mehta@iitb.ac.in>",
            "Project meeting notes",
            "<p>Attaching the notes from today's sync. Key action items are in bold — please review " +
            "before Thursday.</p>" +
            "<ul><li><b>Rohan</b> — finish the data loader.</li>" +
            "<li><b>Priya</b> — draft the evaluation section.</li>" +
            "<li><b>You</b> — rerun the baseline with the new split.</li></ul>" +
            "<p>Shout if anything looks wrong.</p>",
            TimeSpan.FromDays(3),
            Cc: "priya.singh@iitb.ac.in, dev.patel@iitb.ac.in",
            Attachments:
            [
                new MailAttachment("meeting-notes.docx", "11 KB"),
                new MailAttachment("baseline-results.xlsx", "9 KB"),
                new MailAttachment("whiteboard.png", "48 KB"),
                new MailAttachment("action-items.txt", "2 KB"),
            ]),

        new("m6", "Inbox", "IT Services <itsc@iitb.ac.in>",
            "Scheduled maintenance — webmail",
            "<p>Webmail services will be briefly unavailable on Sunday between 2 AM and 4 AM IST " +
            "for scheduled maintenance.</p>" +
            "<p>Mail sent during this window will be queued and delivered once the service is back. " +
            "No action is required from you.</p>",
            TimeSpan.FromDays(5)),

        new("m7", "Inbox", "Central Library <library@iitb.ac.in>",
            "Book due for return on Friday",
            "<p>The following item is due for return on Friday:</p>" +
            "<p><i>Pattern Recognition and Machine Learning</i> — C. M. Bishop</p>" +
            "<p>Renew online if no one else has reserved it, otherwise a fine applies from Saturday.</p>",
            TimeSpan.FromDays(6)),

        // A genuine back-and-forth — three separate stored messages sharing one subject, the way
        // conversation view actually groups a real IMAP thread (see the Ananya Rao thread above for
        // the two-message case). Kept as distinct MockMessage entries rather than one message with
        // quoted history baked in, so both examples render identically through BuildConversationHtml.
        new("m9a", "Inbox", "Priya Singh <priya.singh@iitb.ac.in>",
            "Baseline results for the CS305 project",
            "<p>Something looks off with the baseline — F1 of 0.81 seems too high for this split. " +
            "Can you double check whether the test set is actually held out, or if there's leakage " +
            "from training?</p>",
            TimeSpan.FromHours(29),
            Cc: "dev.patel@iitb.ac.in"),

        new("s4", "Sent", SelfIdentity,
            "Re: Baseline results for the CS305 project",
            "<p>Good catch — I think the leakage is coming from the split happening before " +
            "de-duplication. Can you rerun with the fix and share the new numbers? I'll hold off " +
            "on the writeup until then.</p>",
            TimeSpan.FromHours(24),
            To: "Priya Singh <priya.singh@iitb.ac.in>",
            Cc: "dev.patel@iitb.ac.in"),

        new("m9", "Inbox", "Priya Singh <priya.singh@iitb.ac.in>",
            "Re: Re: Baseline results for the CS305 project",
            "<p>Attaching the updated numbers — the new split fixed the leakage issue you flagged. " +
            "F1 is down slightly (0.81 → 0.78) but that's the honest number now.</p>" +
            "<p>Let's sync before we write this up for the report.</p>" +
            "<p>— Priya</p>",
            TimeSpan.FromHours(5),
            Cc: "dev.patel@iitb.ac.in",
            Unread: true,
            Attachments: [new MailAttachment("baseline-v2.csv", "6 KB")]),

        new("m8", "Inbox", "Dean of Student Affairs <dosa@iitb.ac.in>",
            "Institute holiday and revised class schedule",
            "<p>Dear Students,</p>" +
            "<p>The institute will remain closed on the coming Monday. Classes scheduled for that day " +
            "will be compensated as follows:</p>" +
            "<ul><li>Monday slot 1 classes move to Saturday, 9 AM.</li>" +
            "<li>Monday slot 2 classes move to Saturday, 11 AM.</li>" +
            "<li>Laboratory sessions will be rescheduled by the respective departments.</li></ul>" +
            "<p>Hostel mess timings remain unchanged. The library will operate on a holiday schedule " +
            "(10 AM to 6 PM). Students travelling home are advised to inform their hostel warden in " +
            "advance so that room checks can be planned accordingly.</p>" +
            "<p>Regards,<br/>Dean of Student Affairs</p>",
            TimeSpan.FromDays(9)),

        // ---- Sent ------------------------------------------------------------------------
        new("s1", "Sent", SelfIdentity,
            "Re: Extension for assignment 3",
            "<p>Thank you, professor. I'll submit by Friday midnight and make sure part (b) includes " +
            "the full derivation.</p>",
            TimeSpan.FromHours(2.5),
            To: "Ananya Rao <ananya.rao@cse.iitb.ac.in>"),

        new("s2", "Sent", SelfIdentity,
            "Submitting assignment 3",
            "<p>Professor, attaching my submission for assignment 3 as discussed.</p>",
            TimeSpan.FromDays(1),
            To: "Ananya Rao <ananya.rao@cse.iitb.ac.in>",
            Attachments: [new MailAttachment("assignment3.pdf", "18 KB")]),

        new("s3", "Sent", SelfIdentity,
            "Query about hostel transfer",
            "<p>I wanted to ask about the process for a room change next semester, and whether " +
            "applications open before or after the end-semester exams.</p>",
            TimeSpan.FromDays(4),
            To: "Hostel Office <hostel@iitb.ac.in>"),

        // ---- Drafts ----------------------------------------------------------------------
        new("d1", "Drafts", SelfIdentity,
            "Feedback on course registration",
            "<p>I think the registration window should be extended by a few days — the ASC portal " +
            "was unreachable for most of the first morning, and</p>",
            TimeSpan.FromDays(2),
            To: "Academic Office <academic@iitb.ac.in>"),

        new("d2", "Drafts", SelfIdentity,
            "",
            "<p>Reminder to self: ask about the lab slot clash before Friday.</p>",
            TimeSpan.FromHours(6),
            To: ""),

        // ---- Archive ----------------------------------------------------------------------
        new("a1", "Archive", "Examination Section <exam@iitb.ac.in>",
            "Semester 5 grade card available",
            "<p>Your grade card for Semester 5 is now available on ASC.</p>" +
            "<p>Any discrepancy must be reported to the examination section within 15 days.</p>",
            TimeSpan.FromDays(34),
            Attachments: [new MailAttachment("grade-card-sem5.pdf", "9 KB")]),

        new("a2", "Archive", "Hostel Office <hostel@iitb.ac.in>",
            "Hostel allotment result",
            "<p>Your hostel allotment for the current academic year has been confirmed. " +
            "Please complete check-in formalities at the hostel office.</p>",
            TimeSpan.FromDays(48)),

        // ---- Trash ------------------------------------------------------------------------
        new("t1", "Trash", "Rewards Team <no-reply@totally-legit-prizes.example>",
            "Congratulations!!! You have WON a free phone",
            "<p>CLICK NOW to claim your prize before it expires!!!</p>" +
            "<p><img src=\"https://example.com/tracking-pixel.gif\" alt=\"\"/></p>",
            TimeSpan.FromDays(1)),

        new("t2", "Trash", "Campus Newsletter <newsletter@iitb.ac.in>",
            "This week on campus",
            "<p>A round-up of talks, screenings and club events happening this week.</p>",
            TimeSpan.FromDays(11)),
    ];

    private static IReadOnlyList<InboxRow> RowsIn(string folder) =>
        [.. All.Where(m => m.Folder == folder)
               .OrderByDescending(m => m.When)
               .Select(m => m.ToRow())];

    public static readonly IReadOnlyList<InboxRow> InboxRows = RowsIn("Inbox");
    public static readonly IReadOnlyList<InboxRow> SentRows = RowsIn("Sent");
    public static readonly IReadOnlyList<InboxRow> DraftRows = RowsIn("Drafts");
    public static readonly IReadOnlyList<InboxRow> ArchiveRows = RowsIn("Archive");
    public static readonly IReadOnlyList<InboxRow> TrashRows = RowsIn("Trash");

    public static readonly Dictionary<string, MessageDetail> MessageBodies =
        All.ToDictionary(m => m.Id, m => m.ToDetail());
}
