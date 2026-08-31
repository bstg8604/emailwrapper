using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmailClient.UI;

/// <summary>
/// Makes a Window immune to ever taking focus/activation, even from a direct mouse click on it —
/// WPF's own <c>ShowActivated</c> only governs the moment <c>Show()</c> is called, not what
/// happens afterward, so a plain click on the window would still activate it and steal focus from
/// whatever the user was actually typing into. This is the standard WS_EX_NOACTIVATE technique
/// IntelliSense-style completion popups use to stay clickable without disturbing the caret.
/// </summary>
public static class NoActivateWindow
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Call from <c>SourceInitialized</c> — the window's handle must already exist.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }
}
