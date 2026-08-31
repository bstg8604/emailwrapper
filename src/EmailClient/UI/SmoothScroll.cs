using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EmailClient.UI;

/// <summary>
/// Animated mouse-wheel scrolling, as an attached behaviour: <c>ui:SmoothScroll.Enabled="True"</c>.
///
/// WPF's default is to jump the scroll offset instantly — three "lines" per wheel notch, with
/// nothing drawn between where the content was and where it lands. That reads as a low frame rate
/// rather than as a deliberate step, which is exactly the "refresh rate feels low" complaint.
/// Every browser and modern mail client interpolates that gap instead, which is what this does.
///
/// Deliberately a continuous per-frame "chase toward a target" rather than firing a fresh
/// <see cref="System.Windows.Media.Animation.DoubleAnimation"/> on every wheel event: a real mouse
/// with a precision/free-spin wheel, or a trackpad, sends a rapid burst of many small-delta wheel
/// events rather than isolated notches. Restarting a one-shot eased animation on every one of
/// those — faster than each can settle — was measured to turn a clean, steady ~16ms-per-frame
/// cadence into an erratic one (gaps up to 60ms+), which is what actually read as "not smooth":
/// a single isolated notch looked fine, but real scrolling, which is a burst, didn't. Chasing a
/// target instead means new wheel events only ever update *where the chase is heading*, never
/// interrupt or restart *how it's moving* — so a burst of ticks blends into one continuous glide.
///
/// Attach it to the element that *contains* the ScrollViewer (a ListBox, say) as readily as to a
/// ScrollViewer itself — the inner one is resolved from the visual tree on use, since a templated
/// control has no ScrollViewer at all until its template has been applied.
/// </summary>
public static class SmoothScroll
{
    /// <summary>
    /// Pixels per wheel notch (a notch being <see cref="MouseWheelEventArgs.Delta"/> of 120, the
    /// standard unit — a precision wheel's smaller ticks scale down proportionally). Larger than
    /// WPF's own ~48px (three lines) on purpose: chasing a target eases the motion, and an eased
    /// approach to a given distance reads as slower than an instant jump of that same distance, so
    /// matching WPF's raw number here would make the list feel sluggish rather than smooth.
    /// </summary>
    private const double PixelsPerNotch = 100;

    /// <summary>Fraction of the remaining distance closed each frame. Higher tracks the target more
    /// tightly (snappier, less glide); lower glides further after input stops. 0.28 clears ~99% of
    /// a step in roughly 15 frames (~250ms at 60Hz) — close to the feel of the old fixed-duration
    /// animation, but as a continuous chase rather than a one-shot curve that a burst would keep
    /// restarting.</summary>
    private const double ChaseFactor = 0.28;

    /// <summary>Below this distance-to-target, snap exactly and stop the chase — an ever-decaying
    /// exponential approach never mathematically reaches zero on its own.</summary>
    private const double SnapThreshold = 0.5;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    /// <summary>Where the chase is currently heading.</summary>
    private static readonly DependencyProperty TargetOffsetProperty =
        DependencyProperty.RegisterAttached(
            "TargetOffset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0));

    /// <summary>Every ScrollViewer currently being chased, stepped once per frame by the single
    /// shared <see cref="CompositionTarget.Rendering"/> subscription below. A HashSet rather than
    /// one subscription per viewer, so multiple smooth-scrolling lists in the app (the message
    /// list, a settings page) share one render-frame hook instead of each running their own.</summary>
    private static readonly HashSet<ScrollViewer> ActiveViewers = [];
    private static bool _renderingHooked;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true)
            element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject host)
            return;

        // Shift+wheel scrolls horizontally and Ctrl+wheel zooms in most apps. Neither is ours.
        if (Keyboard.Modifiers is not ModifierKeys.None)
            return;

        if (FindViewer(host) is not { } viewer)
            return;

        // Nothing to scroll here — leave the event alone so an outer scrollable region still sees it.
        if (viewer.ScrollableHeight <= 0)
            return;

        var chasing = ActiveViewers.Contains(viewer);

        // With no chase running the stored target is stale: the user may have dragged the
        // scrollbar, pressed a key, or had the whole list replaced since the last notch.
        var from = chasing ? (double)viewer.GetValue(TargetOffsetProperty) : viewer.VerticalOffset;

        var target = Math.Clamp(
            from - e.Delta / 120.0 * PixelsPerNotch,
            0,
            viewer.ScrollableHeight);

        // Already pinned at that end — don't swallow the event, so a parent can take over.
        if (!chasing && Math.Abs(target - viewer.VerticalOffset) < 0.5)
            return;

        viewer.SetValue(TargetOffsetProperty, target);
        StartChasing(viewer);
        e.Handled = true;
    }

    private static void StartChasing(ScrollViewer viewer)
    {
        if (!ActiveViewers.Add(viewer))
            return; // already chasing this one — the frame tick will pick up the new target itself

        if (_renderingHooked)
            return;

        _renderingHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>
    /// One frame's worth of easing for every viewer currently mid-chase. Runs off
    /// <see cref="CompositionTarget.Rendering"/> — WPF's own per-render-frame hook — rather than a
    /// <see cref="System.Windows.Threading.DispatcherTimer"/>, so the step cadence always matches
    /// whatever WPF is actually rendering at, instead of an independently-ticking timer that could
    /// drift out of step with it.
    /// </summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        // Snapshotted: a viewer finishing this frame removes itself from ActiveViewers, which
        // can't happen safely while the set is being enumerated directly.
        foreach (var viewer in ActiveViewers.ToArray())
        {
            var target = (double)viewer.GetValue(TargetOffsetProperty);
            var current = viewer.VerticalOffset;
            var diff = target - current;

            if (Math.Abs(diff) <= SnapThreshold)
            {
                viewer.ScrollToVerticalOffset(target);
                ActiveViewers.Remove(viewer);
                continue;
            }

            viewer.ScrollToVerticalOffset(current + diff * ChaseFactor);
        }

        if (ActiveViewers.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    /// <summary>
    /// The ScrollViewer this behaviour drives: the element itself, or the first one found in its
    /// visual tree. Resolved per wheel event rather than cached when the property is attached,
    /// because a templated control has no visual children until its template is applied.
    /// </summary>
    private static ScrollViewer? FindViewer(DependencyObject element)
    {
        if (element is ScrollViewer self)
            return self;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindViewer(VisualTreeHelper.GetChild(element, i)) is { } found)
                return found;
        }

        return null;
    }
}
