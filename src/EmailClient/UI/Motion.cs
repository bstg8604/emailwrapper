using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EmailClient.UI;

/// <summary>
/// One small set of reusable transitions instead of a hand-rolled Storyboard at every call site.
/// WPF can't animate Visibility directly, so every "reveal" here follows the same shape: flip
/// Visibility to Visible first (so layout/measure happens), then animate Opacity (and optionally a
/// translate offset) in; every "hide" animates Opacity out, then flips Visibility to Collapsed in
/// the Completed callback so the element stops taking hit-test/layout space once it's gone.
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    public static void FadeIn(this UIElement element, double ms = 150)
    {
        element.Visibility = Visibility.Visible;
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

    public static void FadeOut(this UIElement element, double ms = 120)
    {
        var anim = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn };
        anim.Completed += (_, _) => element.Visibility = Visibility.Collapsed;
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Fade plus a short downward settle - for banners/bars that drop in from just above
    /// their resting place (images-blocked bar, second toolbar row).</summary>
    public static void SlideDownReveal(this FrameworkElement element, double ms = 160, double fromOffset = -8)
    {
        element.Visibility = Visibility.Visible;
        var transform = new TranslateTransform(0, fromOffset);
        element.RenderTransform = transform;
        element.Opacity = 0;

        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromOffset, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

    public static void SlideUpHide(this FrameworkElement element, double ms = 120, double toOffset = -8)
    {
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;

        var move = new DoubleAnimation(0, toOffset, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn };
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn };
        fade.Completed += (_, _) => element.Visibility = Visibility.Collapsed;
        transform.BeginAnimation(TranslateTransform.YProperty, move);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Slide up from below - the send/undo snackbar's entrance.</summary>
    public static void SlideUpReveal(this FrameworkElement element, double ms = 180, double fromOffset = 16)
    {
        element.Visibility = Visibility.Visible;
        var transform = new TranslateTransform(0, fromOffset);
        element.RenderTransform = transform;
        element.Opacity = 0;

        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromOffset, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

    public static void SlideDownHide(this FrameworkElement element, double ms = 140, double toOffset = 16)
    {
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;

        var move = new DoubleAnimation(0, toOffset, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn };
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn };
        fade.Completed += (_, _) => element.Visibility = Visibility.Collapsed;
        transform.BeginAnimation(TranslateTransform.YProperty, move);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Fade plus a slight scale-from-top - the standard entrance for a Popup's content,
    /// called from the Popup's Opened event since the Popup itself (a separate visual root) can't
    /// be animated the normal Show/Visibility way.</summary>
    public static void PopIn(this FrameworkElement element, double ms = 130)
    {
        var scale = new ScaleTransform(0.94, 0.94, 0.5, 0);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0);
        element.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }

    /// <summary>Wires PopIn to fire every time this Popup opens - the one-line call site for every
    /// swatch/emoji/contacts/signature/menu popup in the app.</summary>
    public static void AnimateOnOpen(this Popup popup)
    {
        popup.Opened += (_, _) =>
        {
            if (popup.Child is FrameworkElement child)
                child.PopIn();
        };
    }

    /// <summary>
    /// Keeps a Popup from spilling off the right edge of the screen — the default Bottom placement
    /// always opens flush with the anchor's left edge and grows rightward, which looks fine for a
    /// narrow menu but a wide picker (a multi-column swatch grid, say) anchored under a button that
    /// isn't near the window's own left edge routinely overflows past the screen (or floats
    /// awkwardly past the parent window with nothing behind it). This flips to right-aligning the
    /// popup against its anchor instead, the same edge-aware behaviour browsers/OS context menus use.
    ///
    /// Computed once via a plain HorizontalOffset at Opened, not PlacementMode.Custom with a
    /// CustomPopupPlacementCallback — that callback re-fires on every layout pass, including a
    /// content resize after opening (e.g. this picker's own "+" expanding a hex-entry panel below
    /// the swatches), and each re-invocation was closing the popup outright instead of just
    /// repositioning it.
    /// </summary>
    public static void KeepOnScreen(this Popup popup)
    {
        popup.Placement = PlacementMode.Bottom;
        popup.Opened += (_, _) =>
        {
            if (popup.Child is not FrameworkElement child || popup.PlacementTarget is not FrameworkElement anchor)
                return;

            var popupWidth = child.ActualWidth > 0 ? child.ActualWidth : child.DesiredSize.Width;
            var screenTopLeft = anchor.PointToScreen(new System.Windows.Point(0, 0));
            var workArea = SystemParameters.WorkArea;

            popup.HorizontalOffset = screenTopLeft.X + popupWidth > workArea.Right
                ? anchor.ActualWidth - popupWidth   // right-align: popup's right edge meets the anchor's
                : 0;                                 // default: popup's left edge meets the anchor's
        };
    }

    /// <summary>A quick scale "pop" - the star-toggle delight moment, Twitter/Gmail-style.</summary>
    public static void Pulse(this UIElement element, double ms = 220)
    {
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        if (element is FrameworkElement fe)
            fe.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        var grow = TimeSpan.FromMilliseconds(ms * 0.4);
        var settle = TimeSpan.FromMilliseconds(ms * 0.6);
        var keyframes = new DoubleAnimationUsingKeyFrames();
        keyframes.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromTimeSpan(grow), new CubicEase { EasingMode = EasingMode.EaseOut }));
        keyframes.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(grow + settle), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, keyframes);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, (DoubleAnimationUsingKeyFrames)keyframes.Clone());
    }

    /// <summary>Fades a whole window in from just after it's shown - Show()/ShowDialog() otherwise
    /// snap to full opacity instantly.</summary>
    public static void FadeInOnShow(this Window window, double ms = 160)
    {
        window.Opacity = 0;
        window.ContentRendered += (_, _) =>
            window.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }
}
