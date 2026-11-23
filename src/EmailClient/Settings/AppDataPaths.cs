using System.IO;

namespace EmailClient.Settings;

/// <summary>
/// The single source of truth for where the app's own data lives under %LOCALAPPDATA% — settings,
/// the saved account, the local mail cache/database, logs, avatars, attachments, scheduled sends
/// and the offline action queue all live under this one root.
///
/// The folder is named "Purplemail" — every call site used to build this path inline with the
/// app's original working name, "IITBWebmailWrapper", left over from before the app was renamed.
/// <see cref="Root"/> migrates an existing install's data across the first time it's touched after
/// upgrading, so a user's saved account/credentials, cache and settings survive the rename instead
/// of silently vanishing (which would otherwise look exactly like being signed out).
/// </summary>
public static class AppDataPaths
{
    private const string LegacyFolderName = "IITBWebmailWrapper";
    private const string FolderName = "Purplemail";

    /// <summary>Evaluated once per process, on first access — by design, since every caller wants
    /// the same folder and the migration below only needs to run once.</summary>
    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(localAppData, FolderName);
        var legacy = Path.Combine(localAppData, LegacyFolderName);
        MigrateIfNeeded(legacy, current);
        return current;
    }

    private static void MigrateIfNeeded(string legacy, string current)
    {
        try
        {
            // Nothing to migrate (a fresh install, or migration already happened) — and if `current`
            // already exists, moving into it would throw anyway (Directory.Move needs the
            // destination to not exist), so this also protects a mid-migration retry from clobbering
            // whatever's already there.
            if (!Directory.Exists(legacy) || Directory.Exists(current))
                return;

            Directory.Move(legacy, current);
        }
        catch
        {
            // Same-drive (LOCALAPPDATA is always one volume) so this should always succeed, but a
            // locked file or a permissions hiccup shouldn't take the app down before it's even
            // logged anything — falling through leaves `current` to be created fresh, same as any
            // other first run. Nothing to log yet either: Log.Directory itself depends on Root.
        }
    }
}
