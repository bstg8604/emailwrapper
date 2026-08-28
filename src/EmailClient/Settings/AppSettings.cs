using System.IO;
using System.Text.Json;

namespace EmailClient.Settings;

public sealed class AppSettings
{
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 720;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IITBWebmailWrapper");

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static string WebView2UserDataFolder => Path.Combine(DataDir, "WebView2");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
