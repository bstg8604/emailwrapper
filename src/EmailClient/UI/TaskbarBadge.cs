using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
// UseWindowsForms puts System.Drawing and System.Windows.Forms in scope project-wide, and both
// collide with the WPF types this file draws with. Aliased once here rather than fully qualifying
// every use below.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace EmailClient.UI;

/// <summary>
/// Draws the unread count as an overlay on the taskbar button — the Outlook/Mail convention, and
/// the one unread indicator that stays visible when the window is hidden in the tray and the
/// notification toast has expired.
/// </summary>
public static class TaskbarBadge
{
    /// <summary>Above this the badge shows "99+": four digits don't fit in a 16px circle.</summary>
    private const int MaxDisplayed = 99;

    private static readonly Brush BadgeFill = new SolidColorBrush(Color.FromRgb(0x6E, 0x4B, 0xC4));
    private static readonly Brush BadgeText = Brushes.White;

    static TaskbarBadge()
    {
        // Brushes crossing onto the render thread must be frozen, and these are shared statics.
        BadgeFill.Freeze();
    }

    public static void Apply(Window window, int unread)
    {
        window.TaskbarItemInfo ??= new TaskbarItemInfo();

        if (unread <= 0)
        {
            window.TaskbarItemInfo.Overlay = null;
            window.TaskbarItemInfo.Description = "Purplemail";
            return;
        }

        window.TaskbarItemInfo.Overlay = Render(unread);
        window.TaskbarItemInfo.Description = $"Purplemail — {unread} unread";
    }

    /// <summary>
    /// Renders at 32px and lets the shell scale down: drawing straight at the 16px the overlay is
    /// shown at leaves the digits illegible on a high-DPI display.
    /// </summary>
    private static ImageSource Render(int unread)
    {
        const int size = 32;
        var label = unread > MaxDisplayed ? "99+" : unread.ToString(CultureInfo.InvariantCulture);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var centre = new Point(size / 2.0, size / 2.0);
            dc.DrawEllipse(BadgeFill, null, centre, size / 2.0, size / 2.0);

            // Font shrinks as the label grows so "99+" stays inside the circle that "7" sits in.
            var fontSize = label.Length switch { 1 => 21, 2 => 17, _ => 13 };
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                fontSize,
                BadgeText,
                // The overlay is a fixed-size bitmap, not part of the window's visual tree, so
                // there's no per-monitor DPI to inherit here — 96 is simply this bitmap's own.
                pixelsPerDip: 1.0);

            dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
