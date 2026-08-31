using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
// UseWindowsForms puts System.Drawing in scope project-wide, colliding with these WPF Media types.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace EmailClient.UI;

/// <summary>
/// The brief "Copied to clipboard" confirmation after a screenshot capture, as its own small
/// window rather than content living inside <see cref="ScreenshotOverlayWindow"/> — the overlay
/// closes the instant the drag ends, so anything drawn as part of it would never actually be seen.
/// A separate, short-lived window also means the big full-screen capture surface is gone
/// immediately and whatever's underneath is interactive again right away.
/// </summary>
public static class ScreenshotToast
{
    public static void ShowNear(System.Windows.Point centerScreen)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = "Copied to clipboard",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 12.5,
            FontFamily = new FontFamily("Segoe UI"),
        };

        var border = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x2E, 0xA0, 0x43)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 7, 11, 7),
            Child = text,
        };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = border,
        };

        // Centered where the drag ended, in DIP — same coordinate space the overlay itself was
        // reporting the selection in, so no separate physical-pixel conversion is needed here.
        window.Left = centerScreen.X - 70;
        window.Top = centerScreen.Y - 16;

        border.Opacity = 0;
        window.Show();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500))
        {
            BeginTime = TimeSpan.FromMilliseconds(650),
        };
        fadeOut.Completed += (_, _) => window.Close();

        border.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        border.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        // Belt-and-braces: if the animation's Completed handler is ever skipped (a dropped frame,
        // the app losing focus mid-fade), this guarantees the toast doesn't outlive its welcome.
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            if (window.IsVisible)
                window.Close();
        };
        fallback.Start();
    }
}
