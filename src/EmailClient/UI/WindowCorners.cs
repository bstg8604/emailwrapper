using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmailClient.UI;

/// <summary>
/// Restores the rounded window corners every one of our windows loses by using
/// <c>WindowStyle="None"</c> + <c>WindowChrome</c>. Windows 11's compositor rounds top-level
/// windows automatically — it's why File Explorer, Settings, and every Win11-native app already
/// look like this — but taking over the non-client area for a custom title bar opts a window out
/// of that by default, which is why ours were rendering as sharp rectangles while every inner
/// element (buttons, cards, dropdowns) was already rounded. Discord, Slack and other custom-chrome
/// apps hit the same gap and fix it the same way: telling DWM directly what corner style to use,
/// rather than leaving it to a default that only applies to unmodified windows.
/// </summary>
public static class WindowCorners
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Call once the window's handle exists — typically from a <c>Loaded</c> handler.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var preference = DWMWCP_ROUND;
        try
        {
            // Fails harmlessly pre-Windows 11 (no such attribute) — the window just keeps its
            // square corners there, which matches that OS's own convention anyway.
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // Non-fatal cosmetic call.
        }
    }
}
