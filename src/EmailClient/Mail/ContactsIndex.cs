using MailKit;
using MailKit.Net.Imap;

namespace EmailClient.Mail;

/// <summary>
/// Recipient autocomplete built from your own mail history rather than a directory lookup.
/// IITB's institute-wide address-book suggestions come from an internal LDAP directory that only
/// Roundcube (running inside the campus network) can reach — this machine can't query it from
/// off-campus, so there's no institute-wide autocomplete available here. What's still genuinely
/// useful is everyone you've actually corresponded with, scanned from Sent and Inbox headers and
/// ranked by how often you've mailed them — the same idea as Gmail's "Other contacts".
/// </summary>
public sealed class ContactsIndex
{
    private readonly record struct Contact(string Address, string DisplayName, int Score);

    private readonly Dictionary<string, Contact> _byAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>How many recent messages per folder to scan — enough to be useful, cheap enough to run at startup.</summary>
    private const int MessagesPerFolder = 400;

    public bool IsReady { get; private set; }

    public async Task BuildAsync(ImapClient imap, string selfAddress)
    {
        try
        {
            await ScanAsync(imap.Inbox, selfAddress);

            var sent = TryGetSpecial(imap, MailKit.SpecialFolder.Sent);
            if (sent is not null)
                await ScanAsync(sent, selfAddress);
        }
        catch (Exception)
        {
            // Autocomplete is a convenience; a scan failure shouldn't affect anything else.
        }
        finally
        {
            IsReady = true;
        }
    }

    private static IMailFolder? TryGetSpecial(ImapClient imap, MailKit.SpecialFolder special)
    {
        try { return imap.GetFolder(special); } catch (Exception) { return null; }
    }

    private async Task ScanAsync(IMailFolder folder, string selfAddress)
    {
        var reopen = !folder.IsOpen;
        if (reopen)
            await folder.OpenAsync(FolderAccess.ReadOnly);

        try
        {
            var total = folder.Count;
            if (total == 0)
                return;

            var start = Math.Max(0, total - MessagesPerFolder);
            var summaries = await folder.FetchAsync(start, total - 1, MessageSummaryItems.Envelope);

            lock (_gate)
            {
                foreach (var summary in summaries)
                {
                    foreach (var mailbox in (summary.Envelope?.From ?? []).Concat(summary.Envelope?.To ?? [])
                                 .Concat(summary.Envelope?.Cc ?? [])
                                 .OfType<MimeKit.MailboxAddress>())
                    {
                        if (string.IsNullOrWhiteSpace(mailbox.Address)
                            || mailbox.Address.Equals(selfAddress, StringComparison.OrdinalIgnoreCase))
                            continue;

                        _byAddress.TryGetValue(mailbox.Address, out var existing);
                        var name = string.IsNullOrWhiteSpace(existing.DisplayName) && !string.IsNullOrWhiteSpace(mailbox.Name)
                            ? mailbox.Name
                            : existing.DisplayName;
                        _byAddress[mailbox.Address] = new Contact(mailbox.Address, name ?? "", existing.Score + 1);
                    }
                }
            }
        }
        finally
        {
            if (reopen)
                await folder.CloseAsync();
        }
    }

    /// <summary>Top matches for whatever's typed so far, ranked by correspondence frequency.</summary>
    /// <summary>
    /// Returns <see cref="ContactEntry"/> rather than a pre-formatted string so the caller can
    /// render a real suggestion row — avatar initial, name and address as separate lines — instead
    /// of being handed only the flattened "Name &lt;address&gt;" text a plain list has to display
    /// as-is.
    /// </summary>
    public IReadOnlyList<ContactEntry> Suggest(string query, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        lock (_gate)
        {
            return _byAddress.Values
                .Where(c => c.Address.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Score)
                .Take(max)
                .Select(c => new ContactEntry(
                    string.IsNullOrWhiteSpace(c.DisplayName) ? c.Address : c.DisplayName,
                    c.Address,
                    string.IsNullOrWhiteSpace(c.DisplayName) ? c.Address : $"{c.DisplayName} <{c.Address}>",
                    c.Score))
                .ToList();
        }
    }

    /// <summary>Exact lookup for one address — unlike <see cref="Suggest"/>, no substring matching,
    /// so this is the right one to ask "is this specific recipient someone I've actually mailed
    /// before" for (the compose window's recipient-chip coloring and click-for-details popover).</summary>
    public ContactEntry? FindContact(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        lock (_gate)
        {
            if (!_byAddress.TryGetValue(address, out var c))
                return null;
            return new ContactEntry(
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.Address : c.DisplayName,
                c.Address,
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.Address : $"{c.DisplayName} <{c.Address}>",
                c.Score);
        }
    }

    /// <summary>
    /// Every known contact, browsable rather than typed-ahead — the list behind an address-book
    /// picker (Apple Mail's "Address" button opens the same idea: a scrollable panel of everyone
    /// you can mail, not just a search box). Sorted by how often you've corresponded, then name.
    /// </summary>
    public IReadOnlyList<ContactEntry> ListAll()
    {
        lock (_gate)
        {
            return _byAddress.Values
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ContactEntry(
                    string.IsNullOrWhiteSpace(c.DisplayName) ? c.Address : c.DisplayName,
                    c.Address,
                    string.IsNullOrWhiteSpace(c.DisplayName) ? c.Address : $"{c.DisplayName} <{c.Address}>",
                    c.Score))
                .ToList();
        }
    }
}

/// <summary>One entry in the browsable contact list, as the address-book picker and the recipient
/// autocomplete dropdown both display it.</summary>
public readonly record struct ContactEntry(string DisplayName, string Address, string Formatted, int Score = 0)
{
    /// <summary>Avatar-circle letter — same rule as <c>InboxRow.Initial</c>, for the same reason:
    /// the first letter or digit someone would actually recognise, not just DisplayName[0], which
    /// breaks on a name that starts with punctuation or an emoji.</summary>
    public string Initial
    {
        get
        {
            var trimmed = DisplayName.Trim();
            if (trimmed.Length == 0)
                return "?";
            var firstLetter = trimmed.FirstOrDefault(char.IsLetterOrDigit);
            return firstLetter == default ? "?" : char.ToUpperInvariant(firstLetter).ToString();
        }
    }

    /// <summary>False when there's no real name on file — DisplayName then just repeats Address
    /// (see <see cref="ContactsIndex.Suggest"/>'s and <see cref="MainWindow.ListAllContacts"/>'s
    /// fallback) — so a suggestion row shows that once, not the same text on two lines.</summary>
    public bool HasName => !DisplayName.Equals(Address, StringComparison.OrdinalIgnoreCase);
}
