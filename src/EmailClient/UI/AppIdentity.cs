using System.Runtime.InteropServices;

namespace EmailClient.UI;

/// <summary>
/// Registers this process under a stable Application User Model ID — what lets Windows treat a
/// plain, unpackaged desktop app as a first-class notifier: a real Windows 11 toast (proper Fluent
/// card, the app's own icon, an Action Center entry) instead of the legacy small-icon balloon
/// tip <see cref="TrayIcon"/> used to fall back to. Must match the AppUserModelID set on the Start
/// Menu shortcut in build/Purplemail.iss — Windows correlates the two by this exact string, which
/// is also where the toast's icon/branding in Action Center is read from when the app isn't running.
/// No registration, cost, or Microsoft account involved — this is a purely local, free Win32 call.
/// </summary>
public static class AppIdentity
{
    public const string AppUserModelId = "Purplemail.DesktopApp";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Call once, as early as possible in startup — before anything could ever show a
    /// toast notification.</summary>
    public static void Apply()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception)
        {
            // Non-fatal — worst case, toasts fall back to whatever generic identity Windows infers.
        }
    }
}
