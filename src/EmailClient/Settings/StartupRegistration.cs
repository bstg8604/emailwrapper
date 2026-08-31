using System.IO;
using Microsoft.Win32;

namespace EmailClient.Settings;

/// <summary>
/// "Start Purplemail when I sign in to Windows", via the per-user Run key.
///
/// The registry is the single source of truth rather than a bool in settings.json: the user can
/// turn this off from Task Manager's Startup tab or msconfig without the app ever knowing, so a
/// cached copy would drift and show a checkbox that disagrees with what Windows actually does.
/// HKCU (not HKLM) keeps this a per-user setting needing no elevation.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Purplemail";

    /// <summary>Command-line flag telling the app to start hidden in the tray. See <c>App.OnStartup</c>.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>
    /// The app's own executable. Under `dotnet run` the host process is dotnet.exe, which would
    /// register a startup entry that launches the SDK rather than the app — so a non-exe path is
    /// treated as "can't register", which is what <see cref="IsAvailable"/> reports.
    /// </summary>
    private static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return path is not null && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                   && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
    }

    public static bool IsAvailable => ExecutablePath is not null;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception)
            {
                // A locked-down or policy-managed Run key reads as "not enabled" rather than
                // taking down whatever screen asked.
                return false;
            }
        }
    }

    /// <summary>Returns whether the registry now matches <paramref name="enabled"/>.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
                return IsEnabled;

            if (enabled)
            {
                if (ExecutablePath is not { } exe)
                    return false;
                // Quoted: an unquoted path containing spaces is parsed as a command plus arguments.
                // TrayArgument keeps a sign-in launch out of the user's face — the app comes up in
                // the tray watching for mail, rather than throwing a window onto a fresh desktop.
                key.SetValue(ValueName, $"\"{exe}\" {TrayArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Fall through — report whatever the registry actually says now.
        }

        return IsEnabled;
    }
}
