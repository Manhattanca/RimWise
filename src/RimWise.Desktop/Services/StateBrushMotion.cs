using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RimWise.Desktop.Services;

/// <summary>
/// A presentation-only brush for interaction state. Target retains resource/binding ownership;
/// only realized controls with a changed Key or hover state animate. Theme changes and recycled rows snap.
/// </summary>
public static class StateBrushMotion
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(Brush), typeof(StateBrushMotion), new PropertyMetadata(null, Changed));
    public static Brush? GetTarget(DependencyObject value) => (Brush?)value.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject value, Brush? brush) => value.SetValue(TargetProperty, brush);
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(object), typeof(StateBrushMotion), new PropertyMetadata(null, Changed));
    public static object? GetKey(DependencyObject value) => value.GetValue(KeyProperty);
    public static void SetKey(DependencyObject value, object? key) => value.SetValue(KeyProperty, key);
    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered", typeof(bool), typeof(StateBrushMotion), new PropertyMetadata(false, Changed));
    public static bool GetIsHovered(DependencyObject value) => (bool)value.GetValue(IsHoveredProperty);
    public static void SetIsHovered(DependencyObject value, bool hovered) => value.SetValue(IsHoveredProperty, hovered);
    private static readonly DependencyPropertyKey DisplayKey = DependencyProperty.RegisterAttachedReadOnly(
        "Display", typeof(Brush), typeof(StateBrushMotion), new PropertyMetadata(null));
    public static readonly DependencyProperty DisplayProperty = DisplayKey.DependencyProperty;
    public static Brush? GetDisplay(DependencyObject value) => (Brush?)value.GetValue(DisplayProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Presentation), typeof(StateBrushMotion));

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (element.GetValue(StateProperty) is not Presentation state)
        {
            state = new Presentation(element);
            element.SetValue(StateProperty, state);
        }
        state.Queue();
    }

    private sealed class Presentation
    {
        private readonly FrameworkElement element;
        private DispatcherOperation? pending;
        private SolidColorBrush? animated;
        private Brush? animatedTarget;
        private object? key;
        private bool hovered;
        private object? context;
        private bool initialized;
        private bool recycled;
        private int revision;

        internal Presentation(FrameworkElement element)
        {
            this.element = element;
            element.Loaded += (_, _) => { Snap(); recycled = false; };
            element.Unloaded += (_, _) => { Snap(); initialized = false; };
            element.IsVisibleChanged += (_, _) => { if (!element.IsVisible) Snap(); };
            element.DataContextChanged += (_, _) => { Snap(); recycled = true; };
        }

        internal void Queue()
        {
            if (!element.IsLoaded || !element.IsVisible) { Snap(); return; }
            // Resource triggers and Key bindings can arrive in either order in one input turn.
            // Commit once after both have updated, without delaying any native selection event.
            pending ??= element.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                pending = null;
                Apply();
            }));
        }

        private void Apply()
        {
            var nextKey = GetKey(element);
            var nextHovered = GetIsHovered(element);
            var target = GetTarget(element);
            var animate = initialized && !recycled && (!Equals(key, nextKey) || hovered != nextHovered) && ReferenceEquals(context, element.DataContext)
                && element.IsLoaded && element.IsVisible && InterfaceMotion.Enabled;
            var previous = GetDisplay(element);
            key = nextKey; hovered = nextHovered; context = element.DataContext; initialized = true; recycled = false;
            // A hover change on an already-selected item must not stop/restart its selection fade.
            if (animate && ReferenceEquals(animatedTarget, target) && animated is not null) return;
            // Never mutate shared/frozen resource brushes or flatten a gradient into a color.
            if (!animate || !TryColor(previous, out var from) || !TryColor(target, out var to) || from == to)
            {
                Settle(target); return;
            }
            Stop();
            animatedTarget = target;
            var current = revision;
            // The base value must also be the current presentation: WPF may not sample
            // a newly attached clock until the next render tick.
            var brush = animated = new SolidColorBrush(from);
            element.SetValue(DisplayKey, brush);
            var motion = new CompositedColorAnimation(from, to, TimeSpan.FromMilliseconds(140))
            {
                FillBehavior = FillBehavior.HoldEnd
            };
            motion.Completed += (_, _) =>
            {
                if (current == revision) Settle(GetTarget(element));
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, motion, HandoffBehavior.SnapshotAndReplace);
        }

        private void Snap()
        {
            pending?.Abort(); pending = null;
            key = GetKey(element); hovered = GetIsHovered(element); context = element.DataContext; initialized = true;
            Settle(GetTarget(element));
        }

        private void Settle(Brush? target)
        {
            Stop(); element.SetValue(DisplayKey, target);
        }

        private void Stop()
        {
            revision++;
            animated?.BeginAnimation(SolidColorBrush.ColorProperty, null);
            animated = null;
            animatedTarget = null;
        }

        private static bool TryColor(Brush? brush, out Color color)
        {
            color = Colors.Transparent;
            if (brush is null) return true;
            if (brush is not SolidColorBrush solid) return false;
            color = solid.Color;
            color.A = (byte)Math.Round(color.A * solid.Opacity);
            return solid.Transform.Value.IsIdentity && solid.RelativeTransform.Value.IsIdentity;
        }
    }

    // Interpolate visible (premultiplied) contributions, not the hidden RGB of transparent
    // white. Straight ARGB interpolation can introduce a bright band halfway through a
    // transparent-to-dark fade, especially over a custom background image.
    internal static Color Blend(Color from, Color to, double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        var a = from.A * (1 - t) + to.A * t;
        if (a <= 0) return Color.FromArgb(0, to.R, to.G, to.B);
        byte Channel(byte x, byte y) => (byte)Math.Clamp(Math.Round(
            (x * from.A * (1 - t) + y * to.A * t) / a), 0, 255);
        return Color.FromArgb((byte)Math.Round(a), Channel(from.R, to.R),
            Channel(from.G, to.G), Channel(from.B, to.B));
    }

    private sealed class CompositedColorAnimation : ColorAnimationBase
    {
        private readonly Color from, to;
        internal CompositedColorAnimation(Color from, Color to, TimeSpan duration)
        { this.from = from; this.to = to; Duration = duration; }
        protected override Freezable CreateInstanceCore() => new CompositedColorAnimation(from, to, Duration.TimeSpan);
        protected override Color GetCurrentValueCore(Color defaultOriginValue, Color defaultDestinationValue, AnimationClock clock)
        {
            var t = clock.CurrentProgress ?? 1;
            // Gentle acceleration and deceleration without an initial brightness jump.
            return Blend(from, to, t * t * (3 - 2 * t));
        }
    }
}
