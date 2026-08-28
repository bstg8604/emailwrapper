using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmailClient.UI;

/// <summary>
/// WindowStyle="None" + WindowChrome windows have no native frame, so Windows' default
/// maximize behavior expands them to the full monitor bounds — covering the taskbar — instead
/// of the monitor's work area. This is the standard WM_GETMINMAXINFO fix (used by most
/// chrome-less WPF apps) that corrects maximized bounds to respect the taskbar and multi-monitor
/// work areas.
/// </summary>
public static class MaximizeBoundsFix
{
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        };
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkAreaBounds(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaBounds(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref info);

            var workArea = info.rcWork;
            var monitorArea = info.rcMonitor;

            mmi.ptMaxPosition.X = workArea.Left - monitorArea.Left;
            mmi.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
            mmi.ptMaxSize.X = workArea.Right - workArea.Left;
            mmi.ptMaxSize.Y = workArea.Bottom - workArea.Top;
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
