using System.Text.RegularExpressions;

namespace EmailClient.Automation;

/// <summary>
/// Finds a Zoom/Google Meet/Teams join link in arbitrary text. Shared between the reading pane
/// (scanning a plain message's body HTML, since it has no calendar part to fall back on) and
/// <see cref="EmailClient.Mail.ImapMailBackend"/> (scanning a real calendar invite's own
/// description/location, since that's where the join link usually lives instead).
/// </summary>
public static class MeetingLinkFinder
{
    public static readonly (string Label, Regex Pattern)[] Patterns =
    [
        ("Zoom", new Regex(@"https?://[^\s""'<>]*zoom\.us/j/[^\s""'<>]*", RegexOptions.IgnoreCase)),
        ("Google Meet", new Regex(@"https?://meet\.google\.com/[^\s""'<>]*", RegexOptions.IgnoreCase)),
        ("Teams", new Regex(@"https?://teams\.microsoft\.com/l/meetup-join/[^\s""'<>]*", RegexOptions.IgnoreCase)),
    ];

    /// <summary>Just the URL, decoded — for callers (like the calendar-invite parser) that already
    /// have their own idea of the meeting's title/time and only need the link itself.</summary>
    public static string? FindUrl(string text)
    {
        foreach (var (_, pattern) in Patterns)
        {
            var match = pattern.Match(text ?? "");
            if (match.Success)
                return System.Net.WebUtility.HtmlDecode(match.Value);
        }
        return null;
    }

    /// <summary>The label alongside the URL — for callers that need to say which platform it is.</summary>
    public static (string Label, string Url)? Find(string text)
    {
        foreach (var (label, pattern) in Patterns)
        {
            var match = pattern.Match(text ?? "");
            if (match.Success)
                return (label, System.Net.WebUtility.HtmlDecode(match.Value));
        }
        return null;
    }
}
