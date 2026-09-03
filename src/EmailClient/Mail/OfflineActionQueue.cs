using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailClient.Diagnostics;

namespace EmailClient.Mail;

public enum OfflineActionKind { Delete, Archive, MarkRead, MarkUnread, Move }

/// <summary>One mutating action taken while offline, waiting to be replayed against the real
/// server once the connection comes back. The UI already applied it optimistically (the message
/// left the list, the unread dot changed) at the moment it was queued, so replay is fire-and-forget
/// from the user's point of view — nothing on screen changes again when it actually goes through.</summary>
public sealed record PendingAction(
    string Id, OfflineActionKind Kind, string MessageId, string? TargetFolder, DateTime CreatedUtc,
    // The RFC 822 Message-ID header, when known at queue time (from an already-fetched/cached
    // message) — MessageId above is only an IMAP UID, stable within one UIDVALIDITY generation but
    // not guaranteed across a folder rebuild between queueing and replay. Null for actions queued
    // before this existed, or where nothing had fetched the message yet; replay falls back to
    // trusting the UID alone in that case, same as before.
    string? MessageIdHeader = null);

/// <summary>
/// On-disk queue for mutating actions (delete/archive/mark read/mark unread/move) taken while
/// offline, per account — same reasoning and shape as <see cref="ScheduledSendStore"/>. Replayed in
/// order on reconnect (see MainWindow.OfflineActions.cs) so a message deleted then archived offline
/// doesn't race and land in the wrong place.
/// </summary>
public sealed class OfflineActionQueue
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;

    public OfflineActionQueue(string accountEmail)
        : this(accountEmail, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IITBWebmailWrapper", "offline-actions"))
    {
    }

    /// <summary>Explicit root — lets tests work in a temp directory instead of the real profile.</summary>
    public OfflineActionQueue(string accountEmail, string root)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accountEmail.ToLowerInvariant()));
        _path = Path.Combine(root, Convert.ToHexString(hash, 0, 10) + ".json");
    }

    public List<PendingAction> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<PendingAction>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't read the offline-action queue", ex);
            return [];
        }
    }

    private void Save(List<PendingAction> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(items, Json));
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't save the offline-action queue", ex);
        }
    }

    public string Add(OfflineActionKind kind, string messageId, string? targetFolder = null, string? messageIdHeader = null)
    {
        var items = Load();
        var id = Guid.NewGuid().ToString("N");
        items.Add(new PendingAction(id, kind, messageId, targetFolder, DateTime.UtcNow, messageIdHeader));
        Save(items);
        return id;
    }

    /// <returns>False if no pending action with that id existed.</returns>
    public bool Remove(string id)
    {
        var items = Load();
        var removed = items.RemoveAll(a => a.Id == id) > 0;
        if (removed)
            Save(items);
        return removed;
    }
}
