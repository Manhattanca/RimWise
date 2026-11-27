using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RimWise.Desktop.Services;

namespace RimWise.Desktop.Presentation;

/// <summary>Short, interruptible list scrolling; retains native virtualization and hit testing.</summary>
public static class ModWheelMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ModWheelMotion), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ModWheelMotion));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not DataGrid grid) return;
        (grid.GetValue(StateProperty) as State)?.Dispose();
        grid.SetValue(StateProperty, (bool)e.NewValue ? new State(grid) : null);
    }

    private sealed class State : IDisposable
    {
        private readonly DataGrid grid;
        private ScrollViewer? scroll;
        private IScrollInfo? content;
        private Window? window;
        private ScrollBar? thumbBar;
        private Thumb? draggingThumb;
        private bool thumbMotion;
        private bool awaitThumbFrame;
        private bool running;
        private double duration = 150;
        private double start, target, requested;
        private long started;
        private long thumbFrameStamp;
        private TimeSpan lastFrame = TimeSpan.MinValue;
        internal bool Running => running;

        internal State(DataGrid grid)
        {
            this.grid = grid;
            grid.Loaded += Loaded;
            grid.Unloaded += Unloaded;
            grid.IsVisibleChanged += AvailabilityChanged;
            grid.IsEnabledChanged += AvailabilityChanged;
            grid.PreviewMouseWheel += Wheel;
            grid.PreviewMouseDown += PointerDown;
            grid.PreviewTouchDown += TouchDown;
            grid.PreviewKeyDown += KeyDown;
            grid.RequestBringIntoView += BringIntoView;
            grid.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(DragStarted));
            grid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(DragCompleted));
            CommandManager.AddPreviewExecutedHandler(grid, PreviewCommand);
            ((INotifyCollectionChanged)grid.Items).CollectionChanged += ItemsChanged;
            if (grid.IsLoaded) Attach();
        }

        private void Loaded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, grid)) Attach(); }
        private void Unloaded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, grid)) Detach(); }
        private void AvailabilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        { if (!grid.IsVisible || !grid.IsEnabled) Stop(cancelPending: true); }
        private void PointerDown(object sender, MouseButtonEventArgs e) => Stop(cancelPending: true);
        private void TouchDown(object? sender, TouchEventArgs e) => Stop(cancelPending: true);
        private void KeyDown(object sender, KeyEventArgs e) => Stop(cancelPending: true);
        private void BringIntoView(object sender, RequestBringIntoViewEventArgs e) => Stop(cancelPending: true);
        private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Stop(cancelPending: true);
        private void Deactivated(object? sender, EventArgs e) => Stop(cancelPending: true);

        private void DragStarted(object sender, DragStartedEventArgs e)
        {
            if (e.OriginalSource is not Thumb thumb || scroll is null) return;
            var bar = AncestorBar(thumb);
            // Row-height sliders, nested scrolling surfaces and horizontal bars
            // retain their native behavior. Only the owning list's thumb qualifies.
            if (bar is not { Orientation: Orientation.Vertical } || !ReferenceEquals(bar.TemplatedParent, scroll)) return;
            Stop(cancelPending: true);
            draggingThumb = thumb;
            thumbBar = bar;
        }

        private void DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, draggingThumb)) return;
            draggingThumb = null;
            if (e.Canceled || !running) Stop(cancelPending: true);
            // A normal release finishes only the short visible tail, never a queue
            // of all the intermediate positions traversed by a long-list drag.
        }

        private void PreviewCommand(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Handled || e.Command != ScrollBar.ScrollToVerticalOffsetCommand
                || !ReferenceEquals(e.OriginalSource, scroll) || draggingThumb is null
                || thumbBar is null || e.Parameter is not double next || !double.IsFinite(next)) return;
            if (!InterfaceMotion.Enabled || !grid.IsLoaded || !grid.IsVisible || !grid.IsEnabled
                || VirtualizingPanel.GetScrollUnit(grid) != ScrollUnit.Pixel
                || scroll is not { CanContentScroll: true, ScrollableHeight: > 0, ViewportHeight: > 0 }
                || content is null || !ReferenceEquals(content.ScrollOwner, scroll))
            { Stop(cancelPending: true); return; }

            next = Math.Clamp(next, 0, scroll.ScrollableHeight);
            var current = content.VerticalOffset;
            target = next;
            thumbMotion = true;
            // Keep the native thumb under the pointer, independently of the eased
            // content. SetCurrentValue preserves the template's offset binding.
            thumbBar.SetCurrentValue(RangeBase.ValueProperty, next);
            // Do not animate through thousands of offscreen rows. Bound the visual
            // lag to half a viewport and ease only the visible arrival, in 90ms.
            var maximumLag = Math.Min(240d, scroll.ViewportHeight * 0.5);
            if (Math.Abs(next - current) > maximumLag)
            {
                current = next - Math.Sign(next - current) * maximumLag;
                requested = current;
                content.SetVerticalOffset(current);
            }
            start = current;
            duration = 90;
            started = Stopwatch.GetTimestamp();
            if (!running) awaitThumbFrame = true;
            StartRendering(current);
            // Intercept before ScrollViewer queues its native immediate jump.
            e.Handled = true;
        }

        private static ScrollBar? AncestorBar(DependencyObject source)
        {
            for (var parent = VisualTreeHelper.GetParent(source); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollBar bar) return bar;
            return null;
        }

        private void Attach()
        {
            Detach();
            scroll = FindScroll(grid);
            content = scroll is null ? null : FindContent(grid, scroll);
            if (scroll is not null) scroll.ScrollChanged += ScrollChanged;
            window = Window.GetWindow(grid);
            if (window is not null) window.Deactivated += Deactivated;
        }

        private void Detach()
        {
            Stop();
            if (scroll is not null) scroll.ScrollChanged -= ScrollChanged;
            if (window is not null) window.Deactivated -= Deactivated;
            scroll = null; content = null; window = null;
        }

        private void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            if (thumbMotion || draggingThumb is not null) Stop(cancelPending: true);
            if (!InterfaceMotion.Enabled || !grid.IsEnabled || !grid.IsVisible || Math.Abs((long)e.Delta) < 120
                || Keyboard.Modifiers != ModifierKeys.None || Mouse.LeftButton == MouseButtonState.Pressed
                || VirtualizingPanel.GetScrollUnit(grid) != ScrollUnit.Pixel || SystemParameters.WheelScrollLines == 0)
            { Stop(cancelPending: true); return; }
            if (scroll is not { IsLoaded: true } || !scroll.IsDescendantOf(grid) || content is null
                || !ReferenceEquals(content.ScrollOwner, scroll)) Attach();
            if (scroll is not { CanContentScroll: true, ScrollableHeight: > 0, ViewportHeight: > 0 }) return;
            if (content is null) return;
            // A nested input/scroll surface owns its own wheel; only the list viewport is animated.
            for (var source = e.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, grid);
                 source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
                if (source is ScrollViewer nested && !ReferenceEquals(nested, scroll)) return;
            // Preserve the physical line distance used by WPF pixel virtualization;
            // row height changes must not silently multiply wheel sensitivity.
            var distance = -e.Delta / 120d * (SystemParameters.WheelScrollLines < 0
                ? scroll.ViewportHeight : SystemParameters.WheelScrollLines * 16d);
            var current = scroll.VerticalOffset;
            // Continue the same direction without a queue of separate animations. Reversal
            // drops the old destination, so the first new frame moves with the user's input.
            var origin = running && Math.Sign(target - current) == Math.Sign(distance) ? target : current;
            var next = Math.Clamp(origin + distance, 0, scroll.ScrollableHeight);
            if (Math.Abs(next - current) < 0.1) { Stop(cancelPending: true); e.Handled = true; return; }
            start = current; target = next; duration = 150; started = Stopwatch.GetTimestamp();
            StartRendering(current);
            e.Handled = true;
        }

        private void StartRendering(double current)
        {
            if (!running)
            {
                requested = current; lastFrame = TimeSpan.MinValue; running = true;
                CompositionTarget.Rendering += Render;
            }
        }

        private void Render(object? sender, EventArgs e)
        {
            if (!InterfaceMotion.Enabled || !grid.IsLoaded || !grid.IsVisible || !grid.IsEnabled || scroll is null
                || content is null || !ReferenceEquals(content.ScrollOwner, scroll))
            { Stop(cancelPending: true); return; }
            // WPF can raise Rendering twice for the same frame after layout work.
            if (e is RenderingEventArgs frame)
            {
                if (lastFrame == frame.RenderingTime) return;
                lastFrame = frame.RenderingTime;
            }
            // Materializing a distant viewport may itself take a frame. Start the
            // short arrival from its first render, rather than spending the whole
            // transition on that layout and presenting only its final position.
            if (awaitThumbFrame)
            {
                awaitThumbFrame = false;
                started = thumbFrameStamp = Stopwatch.GetTimestamp();
            }
            var t = Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / duration, 0, 1);
            var remaining = 1 - t;
            var next = start + (target - start) * (1 - remaining * remaining * remaining);
            if (thumbMotion)
            {
                // A frame-based follower keeps moving even if pointer events keep
                // replacing the destination faster than a 90ms easing can finish.
                var elapsed = Stopwatch.GetElapsedTime(thumbFrameStamp).TotalMilliseconds;
                thumbFrameStamp = Stopwatch.GetTimestamp();
                var current = content.VerticalOffset;
                next = t >= 1 ? target : current + (target - current) * (1 - Math.Exp(-elapsed / 24d));
            }
            requested = Math.Clamp(next, 0, scroll.ScrollableHeight);
            // No forced UpdateLayout, per-row transforms, bitmap copies or pre-realization.
            // Use the public native pixel-scroll provider, as WPF's wheel handler
            // does. Queuing ScrollViewer commands here can later overwrite a native
            // fine-wheel action when easing is cancelled in the same input cycle.
            content.SetVerticalOffset(requested);
            if (t >= 1) Stop(preserveDrag: draggingThumb is not null);
        }

        private void ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, scroll)) return;
            if (!running && !thumbMotion) return;
            // Search, sort, row height, diagnostics navigation and native thumb dragging
            // own their position. Never ease back toward a stale wheel destination.
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0
                || (e.VerticalChange != 0 && Math.Abs(e.VerticalOffset - requested) > 1)) { Stop(); return; }
            if (thumbMotion && thumbBar is not null)
                thumbBar.SetCurrentValue(RangeBase.ValueProperty, target);
        }

        private void Stop(bool cancelPending = false, bool preserveDrag = false)
        {
            var wasRunning = running;
            running = false;
            awaitThumbFrame = false;
            CompositionTarget.Rendering -= Render;
            var bar = thumbBar;
            if (!preserveDrag)
            {
                draggingThumb = null; thumbBar = null; thumbMotion = false;
                if (bar is not null && scroll is not null)
                    bar.SetCurrentValue(RangeBase.ValueProperty, scroll.VerticalOffset);
            }
            if (wasRunning && cancelPending && scroll is { IsLoaded: true } && content is not null
                && ReferenceEquals(content.ScrollOwner, scroll)) content.SetVerticalOffset(scroll.VerticalOffset);
        }

        public void Dispose()
        {
            Detach();
            grid.Loaded -= Loaded; grid.Unloaded -= Unloaded;
            grid.IsVisibleChanged -= AvailabilityChanged; grid.IsEnabledChanged -= AvailabilityChanged;
            grid.PreviewMouseWheel -= Wheel; grid.PreviewMouseDown -= PointerDown;
            grid.PreviewTouchDown -= TouchDown; grid.PreviewKeyDown -= KeyDown;
            grid.RequestBringIntoView -= BringIntoView;
            grid.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(DragStarted));
            grid.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(DragCompleted));
            CommandManager.RemovePreviewExecutedHandler(grid, PreviewCommand);
            ((INotifyCollectionChanged)grid.Items).CollectionChanged -= ItemsChanged;
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

    private static IScrollInfo? FindContent(DependencyObject parent, ScrollViewer owner)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is VirtualizingStackPanel panel && ReferenceEquals(panel.ScrollOwner, owner)) return panel;
            if (FindContent(child, owner) is { } found) return found;
        }
        return null;
    }

#if DEBUG
    internal static bool IsRunningForCheck(DataGrid grid) => (grid.GetValue(StateProperty) as State)?.Running == true;
#endif
}
