using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailClient.Diagnostics;
using EmailClient.UI;

namespace EmailClient.Mail;

/// <summary>A message queued to go out later, not now — "Send Later" from the compose window.
/// Attachments persist as their staged file paths (see ComposeResult/ComposeAttachment), the same
/// shape a normal compose already carries, so nothing extra needs copying into the store itself —
/// only ever a problem if the user deletes/moves the original file before the scheduled time, the
/// same fragility a real draft already has.</summary>
public sealed record ScheduledSend(string Id, ComposeResult Result, DateTime SendAtUtc);

/// <summary>
/// On-disk queue for scheduled sends, per account (two accounts' scheduled mail must never answer
/// for each other, same reasoning as MessageCache). A background timer in MainWindow polls this
/// periodically and sends whatever's due — see MainWindow.ScheduledSend.cs. Unlike MessageCache,
/// this genuinely is the source of truth for "what's still waiting to go out": nothing else in the
/// app remembers a scheduled send once the compose window that created it has closed.
/// </summary>
public sealed class ScheduledSendStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;

    public ScheduledSendStore(string accountEmail)
        : this(accountEmail, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IITBWebmailWrapper", "scheduled"))
    {
    }

    /// <summary>Explicit root — lets tests work in a temp directory instead of the real profile.</summary>
    public ScheduledSendStore(string accountEmail, string root)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accountEmail.ToLowerInvariant()));
        _path = Path.Combine(root, Convert.ToHexString(hash, 0, 10) + ".json");
    }

    public List<ScheduledSend> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<ScheduledSend>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't read the scheduled-send queue", ex);
            return [];
        }
    }

    private void Save(List<ScheduledSend> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(items, Json));
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't save the scheduled-send queue", ex);
        }
    }

    public string Add(ComposeResult result, DateTime sendAtUtc)
    {
        var items = Load();
        var id = Guid.NewGuid().ToString("N");
        items.Add(new ScheduledSend(id, result, sendAtUtc));
        Save(items);
        return id;
    }

    /// <returns>False if no scheduled send with that id existed (already sent, already cancelled).</returns>
    public bool Remove(string id)
    {
        var items = Load();
        var removed = items.RemoveAll(s => s.Id == id) > 0;
        if (removed)
            Save(items);
        return removed;
    }
}
