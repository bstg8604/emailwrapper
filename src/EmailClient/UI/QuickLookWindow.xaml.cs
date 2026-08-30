using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

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

            PreviewImage.Width = naturalWidth * scale;
            PreviewImage.Height = naturalHeight * scale;
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
