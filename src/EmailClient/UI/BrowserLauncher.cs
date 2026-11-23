using System.Diagnostics;
using System.IO;
using EmailClient.Diagnostics;
using EmailClient.Settings;

namespace EmailClient.UI;

/// <summary>
/// The one place a link/media URL turns into a launched process — every other call site
/// (ReadingPane's link-open, AccountWindow's Hyperlink handler) routes through this instead of
/// calling Process.Start directly, so the "open links with a specific browser" setting only has to
/// be wired up once. Opening a downloaded local file (an attachment, the log) stays a direct
/// UseShellExecute call at its own call site — that's "whatever handles this file type", a
/// different question from "which browser handles a URL".
/// </summary>
public static class BrowserLauncher
{
    /// <returns>False when the URL couldn't be handed to anything — the OS has no default browser
    /// registered, or the configured browser .exe no longer exists at that path.</returns>
    public static bool Open(AppSettings settings, string url)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.BrowserPath) && File.Exists(settings.BrowserPath))
            {
                Process.Start(new ProcessStartInfo(settings.BrowserPath, $"\"{url}\""));
                return true;
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't open the link {url}", ex);
            return false;
        }
    }

    /// <summary>One (friendly name, real .exe path) entry for the Settings picker.</summary>
    public readonly record struct DetectedBrowser(string Name, string Path);

    // The registry's "App Paths" key is what Explorer/Start itself uses to resolve a bare exe name —
    // it's keyed by filename regardless of where that browser is actually installed, which is
    // exactly what's needed here: Chrome/Brave/Firefox are all normally per-user installs under
    // %LOCALAPPDATA%, not Program Files, so a plain file-picker starting in Program Files (the
    // previous "Choose…" behavior) could never find them without manually hunting.
    private static readonly (string Name, string ExeName)[] KnownBrowsers =
    [
        ("Google Chrome", "chrome.exe"),
        ("Microsoft Edge", "msedge.exe"),
        ("Mozilla Firefox", "firefox.exe"),
        ("Brave", "brave.exe"),
        ("Opera", "opera.exe"),
        ("Opera GX", "opera_gx.exe"),
        ("Vivaldi", "vivaldi.exe"),
    ];

    /// <summary>Every known browser actually installed on this machine, resolved via the same
    /// App Paths registry key Windows itself uses — works regardless of a per-user vs. per-machine
    /// install, and regardless of whether a Desktop icon for it even exists (that icon is normally
    /// just a .lnk shortcut anyway, not the real exe a file picker filtered to *.exe would show).</summary>
    public static List<DetectedBrowser> DetectInstalledBrowsers()
    {
        var found = new List<DetectedBrowser>();
        foreach (var (name, exeName) in KnownBrowsers)
        {
            var path = ResolveAppPath(exeName);
            if (path is not null)
                found.Add(new DetectedBrowser(name, path));
        }
        return found;
    }

    private static string? ResolveAppPath(string exeName)
    {
        const string keyBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(keyBase + exeName);
                if (key?.GetValue(null) is string path && File.Exists(path))
                    return path;
            }
            catch (Exception)
            {
                // A malformed or inaccessible key just means this browser isn't detected — not
                // worth surfacing, since KnownBrowsers is a "nice to have" convenience list and
                // "Browse for another app…" always covers whatever this misses.
            }
        }
        return null;
    }

    /// <summary>Resolves a .lnk shortcut to whatever .exe it actually points at, via the same
    /// Shell COM object Explorer itself uses — late-bound (no WSH type-library reference needed)
    /// since this is the only place in the app that needs it. Returns null for anything that isn't
    /// a shortcut, or whose target can't be resolved.</summary>
    public static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;
            var shell = Activator.CreateInstance(shellType);
            var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [lnkPath]);
            var target = shortcut?.GetType()
                .InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't resolve the shortcut '{lnkPath}'", ex);
            return null;
        }
    }
}
