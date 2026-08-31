using EmailClient.Automation;

namespace EmailClient.Mail;

/// <summary>
/// Decides which rows in a freshly-fetched page deserve a desktop notification.
///
/// The old test — "the top row's id changed and it's unread" — announced at most one message and
/// missed the rest of a burst, so three mails arriving between two polls produced one toast. It
/// also fired on the very first page load (everything is "new" when you have no history) and on a
/// delete-then-refresh, which promotes an older unread message to the top without anything having
/// arrived. Tracking ids explicitly gets all three cases right.
/// </summary>
public sealed class NewMailNotifier
{
    /// <summary>
    /// Ids stay remembered well past the page they were seen on, so a message that scrolls off
    /// page one and later comes back (a delete above it, a sort change) isn't re-announced as new.
    /// Bounded so a long-lived session can't grow this without limit — the queue mirrors the set
    /// purely to know which id is the oldest when trimming.
    /// </summary>
    private const int MaxRemembered = 2000;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private bool _primed;

    /// <summary>
    /// Whether the baseline has been established yet. Callers that might invoke <see cref="Collect"/>
    /// from a non-mail-check refresh (a folder switch, say) should skip that call once this is
    /// true — otherwise a message that arrived but hasn't been announced yet gets marked "seen" by
    /// the unrelated navigation and is never announced at all. The very first call still has to go
    /// through regardless, or the next genuine check would flood-announce the whole mailbox.
    /// </summary>
    public bool IsPrimed => _primed;

    /// <summary>
    /// Forgets everything, so the next <see cref="Collect"/> re-primes silently. Called when the
    /// account changes — another mailbox's ids say nothing about this one, and the first page of a
    /// freshly signed-in account is history, not news.
    /// </summary>
    public void Reset()
    {
        _seen.Clear();
        _order.Clear();
        _primed = false;
    }

    /// <summary>
    /// Records <paramref name="rows"/> as seen and returns the ones worth announcing: unread, and
    /// never observed before. The first call after construction or <see cref="Reset"/> returns
    /// nothing — it establishes the baseline, since at that point every message looks new.
    /// </summary>
    public IReadOnlyList<InboxRow> Collect(IEnumerable<InboxRow> rows)
    {
        var fresh = new List<InboxRow>();

        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Id))
                continue;
            if (!Remember(row.Id))
                continue;
            if (_primed && row.Unread)
                fresh.Add(row);
        }

        _primed = true;
        return fresh;
    }

    /// <summary>Adds an id, returning true only the first time it's seen.</summary>
    private bool Remember(string id)
    {
        if (!_seen.Add(id))
            return false;

        _order.Enqueue(id);
        while (_order.Count > MaxRemembered)
            _seen.Remove(_order.Dequeue());

        return true;
    }

    /// <summary>
    /// Renders a batch as a notification. One message reads like the message itself; a burst leads
    /// with the count and lists the newest few, because a toast that only names one of five is
    /// worse than one that admits there are five.
    /// </summary>
    public static (string Title, string Text) Describe(IReadOnlyList<InboxRow> fresh)
    {
        if (fresh.Count == 1)
            return ("New mail", Line(fresh[0]));

        const int listed = 3;
        var lines = fresh.Take(listed).Select(Line).ToList();
        if (fresh.Count > listed)
            lines.Add($"and {fresh.Count - listed} more");

        return ($"{fresh.Count} new messages", string.Join(Environment.NewLine, lines));
    }

    private static string Line(InboxRow row)
    {
        var sender = string.IsNullOrWhiteSpace(row.Sender) ? "(unknown sender)" : row.Sender.Trim();
        var subject = string.IsNullOrWhiteSpace(row.Subject) ? "(no subject)" : row.Subject.Trim();
        return Truncate($"{sender}: {subject}", 90);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";
}
