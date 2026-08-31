using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailClient.Automation;
using EmailClient.Diagnostics;
using EmailClient.Settings;

namespace EmailClient.Mail;

/// <summary>
/// An on-disk copy of what has already been fetched, so the app has something to show before the
/// server answers — and something to show at all when it never does.
///
/// Without it, Purplemail with no connection is an empty window: the message list only ever held
/// what the current session had downloaded, so a dropped Wi-Fi connection or a slow sign-in meant
/// mail that was on screen a minute ago was simply gone. Read-only by nature — this is a display
/// cache, never a source of truth. Anything the server says wins, and a cache miss just means
/// falling back to the network the way the app always did.
/// </summary>
public sealed class MessageCache
{
    /// <summary>
    /// Roughly a few hundred opened messages. Bodies are the large part (HTML, quoted history),
    /// so this is the setting that decides whether the cache is a convenience or a disk problem.
    /// </summary>
    private const int MaxDetails = 300;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _root;

    public MessageCache(string accountEmail)
        : this(accountEmail, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IITBWebmailWrapper", "cache"))
    {
    }

    /// <summary>Explicit cache root — lets tests work in a temp directory instead of the real
    /// profile.</summary>
    public MessageCache(string accountEmail, string cacheRoot)
    {
        // Per account: two accounts' messages must never be able to answer for each other, and
        // signing out of one shouldn't blow away the other's cache.
        _root = Path.Combine(cacheRoot, Key(accountEmail));
    }

    private string RowsPath(string mailbox) => Path.Combine(_root, "rows", Key(mailbox) + ".json");

    private string DetailsDir => Path.Combine(_root, "messages");

    private string DetailPath(string id) => Path.Combine(DetailsDir, Key(id) + ".json");

    /// <summary>
    /// A filesystem-safe, stable name. Mailbox names and message ids can both contain characters
    /// that are illegal in a path (and mailbox names are user-defined), so they're hashed rather
    /// than escaped — the name only has to be unique and reproducible, never readable.
    /// </summary>
    private static string Key(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 10);
    }

    // ---- Message lists ---------------------------------------------------------------------

    public IReadOnlyList<InboxRow> LoadRows(string mailbox)
    {
        try
        {
            var path = RowsPath(mailbox);
            if (!File.Exists(path))
                return [];
            return JsonSerializer.Deserialize<List<InboxRow>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read the cached message list for '{mailbox}'", ex);
            return [];
        }
    }

    public void SaveRows(string mailbox, IReadOnlyList<InboxRow> rows)
    {
        try
        {
            var path = RowsPath(mailbox);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(rows, Json));
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't cache the message list for '{mailbox}'", ex);
        }
    }

    // ---- Message bodies --------------------------------------------------------------------

    public MessageDetail? LoadDetail(string id)
    {
        try
        {
            var path = DetailPath(id);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<MessageDetail>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read cached message {id}", ex);
            return null;
        }
    }

    public void SaveDetail(string id, MessageDetail detail)
    {
        try
        {
            Directory.CreateDirectory(DetailsDir);
            AtomicFile.WriteAllText(DetailPath(id), JsonSerializer.Serialize(detail, Json));
            Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't cache message {id}", ex);
        }
    }

    /// <summary>Drops the least recently written bodies once past <see cref="MaxDetails"/>.</summary>
    private void Trim()
    {
        try
        {
            var files = new DirectoryInfo(DetailsDir).GetFiles("*.json");
            if (files.Length <= MaxDetails)
                return;

            foreach (var stale in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxDetails))
                stale.Delete();
        }
        catch (Exception ex)
        {
            Log.Debug("Trimming the message cache failed: " + ex.Message);
        }
    }

    /// <summary>Removes this account's whole cache — used when signing the account out.</summary>
    public void Clear()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't clear the message cache", ex);
        }
    }
}
