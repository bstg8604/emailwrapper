namespace EmailClient.Automation;

/// <summary>
/// Dummy mail data so the custom UI can be built/polished without needing a completed
/// IITB login or verified DomBridge selectors every time. DELETE this file and the
/// `UseMockData` flag in MainWindow.xaml.cs once real live data is working end-to-end.
/// </summary>
public static class MockData
{
    public static readonly IReadOnlyList<InboxRow> InboxRows =
    [
        new("m1", "Academic Office", "Mid-semester exam schedule released", "The mid-semester examination schedule for AY 2026-27 has been published on ASC...", "10:42 AM", true, Starred: true, HasAttachment: true),
        new("m2", "Prof. Ananya Rao", "Re: Extension for assignment 3", "Sure, I can give you until Friday. Please make sure to submit via Moodle...", "9:15 AM", true),
        new("m3", "Hostel Office", "Mess bill due reminder", "This is a reminder that your mess bill for the month is due on the 5th...", "Yesterday", false),
        new("m4", "IITB Placement Cell", "Pre-placement talk — Tuesday 6 PM", "You are invited to attend the pre-placement talk being held in LH-301...", "Yesterday", false, Starred: true),
        new("m5", "Rohan Mehta", "Project meeting notes", "Attaching the notes from today's sync. Key action items are highlighted...", "Mon", false, HasAttachment: true),
        new("m6", "IT Services", "Scheduled maintenance — webmail", "Webmail services will be briefly unavailable on Sunday 2 AM - 4 AM IST...", "Sun", false),
    ];

    public static readonly IReadOnlyList<InboxRow> SentRows =
    [
        new("s1", "You", "Re: Extension for assignment 3", "Thank you, professor. I'll submit by Friday midnight...", "9:20 AM", false),
        new("s2", "You", "Query about hostel transfer", "I wanted to ask about the process for a room change...", "Sat", false),
    ];

    public static readonly IReadOnlyList<InboxRow> DraftRows =
    [
        new("d1", "You", "Feedback on course registration", "Draft — I think the registration window should...", "Fri", false),
    ];

    public static readonly IReadOnlyList<InboxRow> TrashRows = [];

    public static readonly Dictionary<string, MessageDetail> MessageBodies = new()
    {
        ["m1"] = new MessageDetail(
            "Mid-semester exam schedule released",
            "Academic Office <academic@iitb.ac.in>",
            "Today, 10:42 AM",
            "<p>Dear Student,</p><p>The mid-semester examination schedule for AY 2026-27 has been published on ASC. Please check your slot and report to the allotted room 15 minutes early.</p><p>Regards,<br/>Academic Office</p>"),
        ["m2"] = new MessageDetail(
            "Re: Extension for assignment 3",
            "Ananya Rao <ananya.rao@cse.iitb.ac.in>",
            "Today, 9:15 AM",
            "<p>Sure, I can give you until Friday. Please make sure to submit via Moodle before midnight.</p><p>— Prof. Rao</p>"),
        ["m3"] = new MessageDetail(
            "Mess bill due reminder",
            "Hostel Office <hostel@iitb.ac.in>",
            "Yesterday, 6:03 PM",
            "<p>This is a reminder that your mess bill for the month is due on the 5th. Late payments incur a fine.</p>"),
        ["m4"] = new MessageDetail(
            "Pre-placement talk — Tuesday 6 PM",
            "Placement Cell <placement@iitb.ac.in>",
            "Yesterday, 3:20 PM",
            "<p>You are invited to attend the pre-placement talk being held in LH-301 this Tuesday at 6 PM. Attendance is mandatory for registered students.</p>"),
        ["m5"] = new MessageDetail(
            "Project meeting notes",
            "Rohan Mehta <rohan.mehta@iitb.ac.in>",
            "Mon, 11:47 AM",
            "<p>Attaching the notes from today's sync. Key action items are highlighted in bold — please review before Thursday.</p>"),
        ["m6"] = new MessageDetail(
            "Scheduled maintenance — webmail",
            "IT Services <itsc@iitb.ac.in>",
            "Sun, 8:00 AM",
            "<p>Webmail services will be briefly unavailable on Sunday 2 AM - 4 AM IST for scheduled maintenance.</p>"),
        ["s1"] = new MessageDetail(
            "Re: Extension for assignment 3",
            "You <you@iitb.ac.in>",
            "Today, 9:20 AM",
            "<p>Thank you, professor. I'll submit by Friday midnight.</p>"),
        ["s2"] = new MessageDetail(
            "Query about hostel transfer",
            "You <you@iitb.ac.in>",
            "Sat, 4:10 PM",
            "<p>I wanted to ask about the process for a room change next semester.</p>"),
        ["d1"] = new MessageDetail(
            "Feedback on course registration",
            "You <you@iitb.ac.in>",
            "Fri, 2:00 PM",
            "<p>Draft — I think the registration window should be extended by a few days...</p>"),
    };
}
