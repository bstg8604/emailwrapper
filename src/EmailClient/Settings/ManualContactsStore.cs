using System.IO;
using System.Text.Json;

namespace EmailClient.Settings;

public sealed class ManualContact
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>
/// Manually added contacts — separate from the auto-learned "people you've emailed" list
/// (<see cref="EmailClient.Mail.ContactsIndex"/>), which is derived from mail history each
/// session and never persisted on its own. Not sensitive, so plain JSON like
/// <see cref="AppSettings"/> rather than the DPAPI-encrypted account store.
/// </summary>
public static class ManualContactsStore
{
    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IITBWebmailWrapper");

    private static string ContactsPath => Path.Combine(DataDir, "contacts.json");

    public static List<ManualContact> Load()
    {
        try
        {
            if (!File.Exists(ContactsPath))
                return [];
            var json = File.ReadAllText(ContactsPath);
            return JsonSerializer.Deserialize<List<ManualContact>>(json) ?? [];
        }
        catch (Exception)
        {
            // Corrupt or unreadable file — treat as "no manual contacts" rather than crashing.
            return [];
        }
    }

    public static void Save(List<ManualContact> contacts)
    {
        Directory.CreateDirectory(DataDir);
        var json = JsonSerializer.Serialize(contacts, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(ContactsPath, json);
    }
}
