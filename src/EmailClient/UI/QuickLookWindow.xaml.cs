using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EmailClient.UI;

/// <summary>
/// A lightweight, non-modal image preview — the macOS Quick Look pattern (spacebar in Finder)
/// rather than opening a full document window: a small floating panel sized to the image, no
/// resize/maximize chrome, dismissed with Escape or Space. Non-image attachments (PDFs, text)
/// keep going to <see cref="AttachmentViewerWindow"/>, since a static <c>Image</c> control can't
/// render them and Edge's own PDF viewer is already the better experience for those anyway.
/// </summary>
public partial class QuickLookWindow : Window
{
    private readonly string _filePath;
    private const double MinZoom = 1.0;
    private const double MaxZoom = 5.0;
    private double _fitWidth, _fitHeight;
    private double _zoomFactor = 1.0;

    /// <summary>Raster formats WPF's <see cref="BitmapImage"/> can decode on its own.</summary>
    private static readonly HashSet<string> PreviewableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };

    // .svg is deliberately excluded even though the extension "looks like an image": it's markup,
    // not raster data, and rendering it would mean parsing/executing untrusted content — the same
    // reasoning AttachmentViewerWindow uses to show SVG as source instead of rendering it.
    public static bool CanPreview(string fileName) =>
        PreviewableExtensions.Contains(Path.GetExtension(fileName));

    public QuickLookWindow(string filePath, string displayName)
    {
        InitializeComponent();

        _filePath = filePath;
        Title = displayName;
        FileNameText.Text = displayName;

        try
        {
            FileSizeText.Text = AttachmentViewerWindow.FormatSize(new FileInfo(filePath).Length);
        }
        catch (Exception)
        {
            FileSizeText.Text = "";
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // OnLoad reads and releases the file immediately, rather than keeping it open for
            // the life of the window.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage.Source = bitmap;

            // An unconstrained Image inside a SizeToContent window doesn't reliably settle on
            // the bitmap's own pixel size during layout, so the display size is computed and set
            // explicitly instead: natural size for a small image, scaled down (never up) to a
            // comfortable fraction of the screen for a large one — "quick look," not "full window."
            var naturalWidth = bitmap.PixelWidth * 96.0 / bitmap.DpiX;
            var naturalHeight = bitmap.PixelHeight * 96.0 / bitmap.DpiY;

            var work = SystemParameters.WorkArea;
            var maxWidth = Math.Max(320, work.Width * 0.7);
            var maxHeight = Math.Max(240, work.Height * 0.65);
            var scale = Math.Min(1.0, Math.Min(maxWidth / naturalWidth, maxHeight / naturalHeight));

            _fitWidth = naturalWidth * scale;
            _fitHeight = naturalHeight * scale;
            PreviewImage.Width = _fitWidth;
            PreviewImage.Height = _fitHeight;

            // How far zooming in is allowed to grow the window itself before the ScrollViewer takes
            // over for panning instead — a more generous fraction than the initial fit-to-screen
            // size above, since this is the "how big can the window get" ceiling, not the opening size.
            ImageArea.MaxWidth = work.Width * 0.94;
            ImageArea.MaxHeight = work.Height * 0.9;
        }
        catch (Exception)
        {
            FileSizeText.Text = "Couldn't render this image";
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Space)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>Ctrl+wheel zooms in/out, anchored on whatever point in the picture is under the
    /// cursor — the same feel as the Windows Photos app or a browser, not a fixed center point.
    /// Below the point where the zoomed image would exceed a comfortable screen fraction, the
    /// window itself grows with it too (so zooming in actually shows you more, not just a bigger
    /// picture behind a small porthole); past that, the window stops growing and the ScrollViewer's
    /// own panning takes over for the rest.</summary>
    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        e.Handled = true;
        ApplyZoom(_zoomFactor * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(PreviewImage), e.GetPosition(ImageScrollViewer));
    }

    /// <summary>Double-click toggles between "fit" (1x) and a comfortable closer-in zoom — the
    /// common image-viewer shortcut for "let me see this properly" without reaching for Ctrl+wheel.</summary>
    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;
        var newZoom = _zoomFactor > MinZoom ? MinZoom : 2.5;
        ApplyZoom(newZoom, e.GetPosition(PreviewImage), e.GetPosition(ImageScrollViewer));
    }

    /// <param name="pointInImage">Where the cursor is, in the image's own (pre-zoom) rendered
    /// coordinates — used as a fraction of the image so it still means "the same spot in the
    /// picture" once the image has resized.</param>
    /// <param name="pointInViewport">Where the cursor is relative to the ScrollViewer's viewport —
    /// unaffected by the window itself moving, which is what makes it safe to apply after the
    /// window has been repositioned below.</param>
    private void ApplyZoom(double newZoom, System.Windows.Point pointInImage, System.Windows.Point pointInViewport)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (newZoom == _zoomFactor)
            return;

        var fx = PreviewImage.ActualWidth > 0 ? pointInImage.X / PreviewImage.ActualWidth : 0.5;
        var fy = PreviewImage.ActualHeight > 0 ? pointInImage.Y / PreviewImage.ActualHeight : 0.5;

        _zoomFactor = newZoom;

        // The image itself always grows with zoom (uncapped) — ImageArea's MaxWidth/MaxHeight (set
        // once, in the constructor) is what stops the *window* from following past a comfortable
        // screen fraction; past that point the ScrollViewer's own scrollbars take over for panning
        // the now-larger-than-viewport image instead.
        var newWidth = _fitWidth * _zoomFactor;
        var newHeight = _fitHeight * _zoomFactor;
        PreviewImage.Width = newWidth;
        PreviewImage.Height = newHeight;

        // The window resizes to follow (SizeToContent) but always grows from its top-left corner
        // by default — recentering on the point it was centered on before is what makes it read as
        // "zooming outward from the middle" instead of drifting off toward one corner. Deferred to
        // Loaded priority so this runs after layout has actually applied the new content size.
        var centerX = Left + Width / 2;
        var centerY = Top + Height / 2;
        Dispatcher.InvokeAsync(() =>
        {
            var work = SystemParameters.WorkArea;
            Left = Math.Clamp(centerX - Width / 2, work.Left, Math.Max(work.Left, work.Right - Width));
            Top = Math.Clamp(centerY - Height / 2, work.Top, Math.Max(work.Top, work.Bottom - Height));

            // Keeps the same point in the picture under the cursor once there's more image than
            // the (possibly now-capped) window can show at once — a no-op until zoomed in enough
            // that the ScrollViewer actually has somewhere to scroll to.
            ImageScrollViewer.ScrollToHorizontalOffset(fx * newWidth - pointInViewport.X);
            ImageScrollViewer.ScrollToVerticalOffset(fy * newHeight - pointInViewport.Y);
        }, DispatcherPriority.Loaded);
    }

    // No WindowChrome here (it conflicts with AllowsTransparency), so dragging is wired manually.
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Title,
            Title = "Save image",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.Copy(_filePath, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't save the file: {ex.Message}", "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenExternallyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't open the file: {ex.Message}", "Open failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
