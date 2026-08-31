using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using EmailClient.Diagnostics;
using Drawing = System.Drawing;

namespace EmailClient.UI;

/// <summary>
/// Ctrl+Shift+S: drag out a rectangle within the current Purplemail window and copy it to the
/// clipboard — the same gesture as Brave's page-screenshot tool, scoped to the app rather than the
/// whole desktop. Registered once for the whole app (see <c>App.xaml.cs</c>'s class handler), so
/// it works from any of the app's windows — the main window, Compose, the account page, an
/// attachment viewer — not just one of them, capturing whichever one had focus.
///
/// Deliberately a real screen-region capture (GDI <c>CopyFromScreen</c>, clipped to that window's
/// on-screen rectangle) rather than rendering the WPF visual tree directly. The reading pane,
/// Compose's editor and the signature editor are all WebView2 — a separate native surface
/// composited on top by the OS, not part of WPF's own render tree — so a visual-tree capture would
/// render that entire area blank, which is exactly where the content worth screenshotting usually
/// lives. Clipping the *source region* to the window's bounds is what keeps this from reaching
/// content outside the app, without losing WebView2 content to get there.
/// </summary>
public static class ScreenshotTool
{
    /// <summary>Set while an overlay is open, so a second Ctrl+Shift+S (or the class handler
    /// re-firing for the overlay window itself) can't stack a second one on top.</summary>
    private static bool _active;

    public static void Capture(Window? sourceWindow)
    {
        if (_active || sourceWindow is null)
            return;

        // Nothing sensible to screenshot for a window that isn't actually on screen right now —
        // GetWindowRect on a minimized window returns a meaningless off-screen rectangle.
        if (sourceWindow.WindowState == WindowState.Minimized || !sourceWindow.IsVisible)
            return;

        Drawing.Rectangle bounds;
        Drawing.Bitmap capture;
        try
        {
            bounds = GetWindowBounds(sourceWindow);
            capture = CaptureRegion(bounds);
        }
        catch (Exception ex)
        {
            // A failed capture (a locked-down GDI session, e.g. over some remote-desktop setups)
            // should be invisible rather than surface a dialog for what is a convenience feature.
            Log.Warn("Screenshot capture failed", ex);
            return;
        }

        _active = true;
        var overlay = new ScreenshotOverlayWindow(capture, bounds);
        overlay.Closed += (_, _) => _active = false;
        overlay.Show();
    }

    /// <summary>The app window's own on-screen rectangle, in physical pixels (matching what
    /// <see cref="CaptureRegion"/> and <see cref="PlaceWindowAtPhysicalBounds"/> both work in).</summary>
    private static Drawing.Rectangle GetWindowBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        GetWindowRect(handle, out var r);
        return Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Captures exactly this rectangle of the screen, in physical pixels.</summary>
    private static Drawing.Bitmap CaptureRegion(Drawing.Rectangle bounds)
    {
        var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
        using var g = Drawing.Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// Positions a window at an exact physical-pixel rectangle, bypassing WPF's own DPI-scaled
    /// Left/Top/Width/Height. The overlay is placed to match the captured app window's rectangle
    /// exactly, and Win32 SetWindowPos is the unambiguous way to do that regardless of which
    /// monitor (and DPI scale) that window happens to be on.
    /// </summary>
    public static void PlaceWindowAtPhysicalBounds(Window window, Drawing.Rectangle bounds)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Crops the captured bitmap to a rectangle given in the overlay window's own DIP coordinates,
    /// converting via the ratio between the window's actual (DIP) size and the bitmap's physical
    /// size — rather than assuming a particular DPI scale, which lets this stay correct whichever
    /// monitor (and whichever scale factor) the selection was dragged on.
    /// </summary>
    public static BitmapSource? CropToClipboardSource(Drawing.Bitmap capturedWindow, Rect selectionDip, System.Windows.Size windowDipSize)
    {
        if (windowDipSize.Width <= 0 || windowDipSize.Height <= 0)
            return null;

        var scaleX = capturedWindow.Width / windowDipSize.Width;
        var scaleY = capturedWindow.Height / windowDipSize.Height;

        var region = Drawing.Rectangle.FromLTRB(
            (int)Math.Round(selectionDip.Left * scaleX),
            (int)Math.Round(selectionDip.Top * scaleY),
            (int)Math.Round(selectionDip.Right * scaleX),
            (int)Math.Round(selectionDip.Bottom * scaleY));

        region.Intersect(new Drawing.Rectangle(0, 0, capturedWindow.Width, capturedWindow.Height));
        if (region.Width <= 0 || region.Height <= 0)
            return null;

        using var crop = capturedWindow.Clone(region, capturedWindow.PixelFormat);
        return ToBitmapSource(crop);
    }

    /// <summary>GDI bitmap to a frozen (cross-thread-safe, and cheap to hold onto) WPF image
    /// source. The handle round-trip is the standard way to bridge System.Drawing and WPF; the
    /// GDI object it creates must be deleted manually or it leaks a GDI handle per call.</summary>
    public static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    /// <summary>
    /// The clipboard is a shared, briefly-lockable OS resource — another process (a clipboard
    /// manager, a paste target that just finished reading it) can hold it for a few milliseconds,
    /// which surfaces as a transient <see cref="System.Runtime.InteropServices.COMException"/>
    /// rather than a real failure. Retrying briefly is the standard, and only, fix.
    /// </summary>
    public static bool TrySetClipboardImage(BitmapSource image)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetImage(image);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException ex) when (attempt < 4)
            {
                Log.Debug($"Clipboard busy, retrying ({attempt + 1}/5): {ex.Message}");
                System.Threading.Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't copy the screenshot to the clipboard", ex);
                return false;
            }
        }
        return false;
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
