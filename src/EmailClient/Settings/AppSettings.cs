using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailClient.Diagnostics;

namespace EmailClient.Settings;

public sealed class SignatureEntry
{
    public string Name { get; set; } = "";

    /// <summary>The signature body. Rich-text signatures (added once the account popup's
    /// Signature tab got real formatting) store this as HTML; older saves are plain text with
    /// literal newlines — see <see cref="BodyHtml"/> for the shape actually rendered.</summary>
    public string Body { get; set; } = "";

    /// <summary>Body as HTML for display/insertion — bodies saved before rich-text signatures
    /// existed are plain text with literal newlines, so anything that doesn't already look like
    /// HTML is treated as that legacy shape instead of dumping raw "\n"s onto the page.</summary>
    public string BodyHtml =>
        string.IsNullOrEmpty(Body) || Body.Contains('<')
            ? Body
            : string.Join("<br>", Body.Replace("\r\n", "\n").Split('\n').Select(System.Net.WebUtility.HtmlEncode));
}

public sealed class AppSettings
{
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 720;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Last size the account window was resized to (native OS resize borders) —
    /// remembered across sessions the same way the main window's bounds are.</summary>
    public double AccountWindowWidth { get; set; } = 640;
    public double AccountWindowHeight { get; set; } = 520;

    /// <summary>
    /// Legacy single signature field, kept only so settings.json files written before named/multiple
    /// signatures existed still migrate correctly — see <see cref="Load"/>. New code should read
    /// <see cref="Signatures"/> instead.
    /// </summary>
    public string Signature { get; set; } = "";

    /// <summary>
    /// Named signatures, auto-appended into new/reply/forward compose bodies (using
    /// <see cref="DefaultSignatureIndex"/>) and offered as a pick-list in Compose when there's more
    /// than one. Not sensitive — kept in the same plain settings.json as window bounds rather than
    /// the DPAPI-encrypted account file.
    /// </summary>
    public List<SignatureEntry> Signatures { get; set; } = new();

    public int DefaultSignatureIndex { get; set; }

    /// <summary>Email addresses flagged VIP (Apple Mail's term) — mail from these senders gets a
    /// visual priority marker in the message list. Kept as plain addresses, not display names,
    /// since a sender's address is the one thing that doesn't change between messages.</summary>
    public List<string> VipSenders { get; set; } = new();

    /// <summary>
    /// Seconds Send holds a message before it actually goes out, with an "Undo" option in the
    /// snackbar — Gmail calls this "Undo Send". No settings UI exposes this yet; edit settings.json
    /// directly to change it. Clamped to a sane range on read since a hand-edited 0 or a huge value
    /// would otherwise break the undo affordance entirely.
    /// </summary>
    public int UndoSendSeconds { get; set; } = 5;

    /// <summary>
    /// Whether new mail raises a desktop notification. On by default — the point of leaving the
    /// app in the tray is to be told when something arrives. No settings UI exposes this yet; edit
    /// settings.json directly, same as <see cref="UndoSendSeconds"/>.
    /// </summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Whether the window close button sends the app to the tray instead of quitting. On by
    /// default; set false to make X actually exit for anyone who'd rather not have a resident app.
    /// Exit from the tray menu always quits regardless.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Set once the app has explained where it went the first time it hid itself, so the "still
    /// running in the tray" notification never becomes a recurring annoyance.
    /// </summary>
    public bool TrayHintShown { get; set; }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IITBWebmailWrapper");

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    // Migrate the pre-multi-signature field into the new list, once.
                    if (settings.Signatures.Count == 0 && !string.IsNullOrWhiteSpace(settings.Signature))
                    {
                        settings.Signatures.Add(new SignatureEntry { Name = "Default", Body = settings.Signature });
                        settings.DefaultSignatureIndex = 0;
                    }
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable settings file — fall back to defaults.
            Log.Warn("Couldn't read settings.json — falling back to defaults", ex);
        }

        return new AppSettings();
    }

    /// <summary>Clamped 1-60s — a hand-edited settings.json shouldn't be able to break the undo
    /// affordance. JsonIgnore because it's derived from <see cref="UndoSendSeconds"/>: without it
    /// System.Text.Json writes it into settings.json as a phantom key that nothing ever reads back
    /// (it has no setter), which just invites someone to edit the one that does nothing.</summary>
    [JsonIgnore]
    public int ClampedUndoSendSeconds => Math.Clamp(UndoSendSeconds, 1, 60);

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(SettingsPath, json);
    }
}
