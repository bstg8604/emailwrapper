using System.Windows;
using System.Windows.Media;
using Drawing = System.Drawing;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;

namespace EmailClient.UI;

/// <summary>
/// The drag-to-select surface for <see cref="ScreenshotTool"/>. One instance covers every
/// monitor; see <see cref="ScreenshotTool.PlaceWindowAtPhysicalBounds"/> for why that has to be
/// done via Win32 rather than the window's own Left/Top/Width/Height.
/// </summary>
public partial class ScreenshotOverlayWindow : Window
{
    /// <summary>Small enough that a plain click (no real drag) reads as "cancel", not "capture a
    /// sliver" — the same threshold Snipping Tool uses.</summary>
    private const double MinSelectionSize = 4;

    private readonly Drawing.Bitmap _screen;
    private readonly Drawing.Rectangle _bounds;
    private System.Windows.Point? _dragStart;

    /// <summary>
    /// Guards against a real crash: closing this window is itself what triggers a deactivation
    /// (WPF sends WM_ACTIVATE as the window tears down and focus moves elsewhere), which re-enters
    /// Window_Deactivated and called Close() a second time on a window already mid-close — WPF's
    /// Window.VerifyNotClosing() throws InvalidOperationException for exactly that. Every path
    /// that closes this window goes through RequestClose() instead of calling Close() directly, so
    /// the re-entrant call becomes a no-op instead of a crash.
    /// </summary>
    private bool _closing;

    private void RequestClose()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }

    public ScreenshotOverlayWindow(Drawing.Bitmap screen, Drawing.Rectangle bounds)
    {
        InitializeComponent();
        _screen = screen;
        _bounds = bounds;

        SourceInitialized += (_, _) =>
            ScreenshotTool.PlaceWindowAtPhysicalBounds(this, _bounds);

        Loaded += (_, _) =>
        {
            ScreenImage.Source = ScreenshotTool.ToBitmapSource(_screen);
            Keyboard.Focus(this);
        };

        // The dim layer's "everything" rectangle has to match actual layout size, not a value
        // guessed at construction time — SizeChanged also covers the first layout pass, so a
        // separate Loaded-time assignment isn't needed on top of this.
        SizeChanged += (_, _) =>
            FullRectGeometry.Rect = new Rect(0, 0, ActualWidth, ActualHeight);

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        HintBar.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start)
            return;

        var current = e.GetPosition(this);
        var selection = new Rect(start, current);

        HoleGeometry.Rect = selection;

        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Width = selection.Width;
        SelectionBorder.Height = selection.Height;
        SelectionBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        SelectionBorder.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        SelectionBorder.Margin = new Thickness(selection.Left, selection.Top, 0, 0);

        SizeLabel.Visibility = Visibility.Visible;
        SizeLabelText.Text = $"{(int)selection.Width} × {(int)selection.Height}";
        // Follows just below-right of the cursor, flipping above it near the bottom edge so the
        // label never gets clipped off the last few pixels of the (possibly multi-monitor) canvas.
        var labelY = current.Y + 18;
        if (labelY + 28 > ActualHeight)
            labelY = current.Y - 30;
        SizeLabel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        SizeLabel.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        SizeLabel.Margin = new Thickness(current.X + 14, labelY, 0, 0);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_dragStart is not { } start)
            return;

        var end = e.GetPosition(this);
        var selection = new Rect(start, end);
        _dragStart = null;

        if (selection.Width < MinSelectionSize || selection.Height < MinSelectionSize)
        {
            RequestClose();
            return;
        }

        var image = ScreenshotTool.CropToClipboardSource(_screen, selection, new System.Windows.Size(ActualWidth, ActualHeight));
        var copied = image is not null && ScreenshotTool.TrySetClipboardImage(image);

        // PointToScreen returns physical device pixels, but Window.Left/Top on the toast — like
        // any not-yet-shown WPF window — are read as DIP; using the physical value there would
        // place the toast further off than intended on any monitor that isn't at 100% scale. Divide
        // by this window's own DPI scale (safe here: the toast is meant to land at essentially the
        // same screen location this overlay currently occupies, so its DPI context is the same
        // one this conversion is based on) to get back to the DIP space Window.Left/Top expects.
        var centerPhysical = PointToScreen(new System.Windows.Point(
            selection.Left + selection.Width / 2,
            selection.Top + selection.Height / 2));
        var dpi = VisualTreeHelper.GetDpi(this);
        var centerDip = new System.Windows.Point(centerPhysical.X / dpi.DpiScaleX, centerPhysical.Y / dpi.DpiScaleY);

        // Shown only after the overlay is gone: it's what's topmost right now, so a toast raised
        // while it's still open would just be occluded by it.
        RequestClose();

        if (copied)
            ScreenshotToast.ShowNear(centerDip);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            RequestClose();
    }

    /// <summary>Alt-Tabbing away, or anything else stealing focus, cancels the pick rather than
    /// leaving an invisible full-screen topmost window the user has no obvious way back to. Also
    /// fires as a side effect of this window's own close — RequestClose's guard is what makes it
    /// safe to just call it again here rather than needing to distinguish the two cases.</summary>
    private void Window_Deactivated(object sender, EventArgs e) => RequestClose();

    protected override void OnClosed(EventArgs e)
    {
        _screen.Dispose();
        base.OnClosed(e);
    }
}
