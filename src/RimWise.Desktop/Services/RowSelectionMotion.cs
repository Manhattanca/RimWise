using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Services;

public enum RowSelectionMotionMode
{
    None,
    AccentBar,
    ContentSlide,
    ThumbnailScale
}

/// <summary>
/// Spring-eased left accent bar, subtle row content nudge, thumbnail micro-scale,
/// inspector title slide, and count badge bump for mod rows and list headers.
/// </summary>
public static class RowSelectionMotion
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(RowSelectionMotionMode), typeof(RowSelectionMotion),
        new PropertyMetadata(RowSelectionMotionMode.None, Changed));

    public static RowSelectionMotionMode GetMode(DependencyObject value) =>
        (RowSelectionMotionMode)value.GetValue(ModeProperty);

    public static void SetMode(DependencyObject value, RowSelectionMotionMode mode) =>
        value.SetValue(ModeProperty, mode);

    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered", typeof(bool), typeof(RowSelectionMotion),
        new PropertyMetadata(false, Changed));

    public static bool GetIsHovered(DependencyObject value) =>
        (bool)value.GetValue(IsHoveredProperty);

    public static void SetIsHovered(DependencyObject value, bool hovered) =>
        value.SetValue(IsHoveredProperty, hovered);

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(RowSelectionMotion),
        new PropertyMetadata(false, Changed));

    public static bool GetIsSelected(DependencyObject value) =>
        (bool)value.GetValue(IsSelectedProperty);

    public static void SetIsSelected(DependencyObject value, bool selected) =>
        value.SetValue(IsSelectedProperty, selected);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(RowMotionState), typeof(RowSelectionMotion));

    private static readonly DependencyProperty PulseStateProperty = DependencyProperty.RegisterAttached(
        "PulseState", typeof(PulseMotionState), typeof(RowSelectionMotion));

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (element.GetValue(StateProperty) is not RowMotionState state)
        {
            state = new RowMotionState(element);
            element.SetValue(StateProperty, state);
        }
        state.Apply();
    }

    internal static void PulseTitle(FrameworkElement? element)
    {
        if (element is null) return;
        Pulse(element).SlideTitle();
    }

    internal static void BumpBadge(FrameworkElement? element)
    {
        if (element is null) return;
        Pulse(element).BumpBadge();
    }

    private static PulseMotionState Pulse(FrameworkElement element)
    {
        if (element.GetValue(PulseStateProperty) is PulseMotionState state) return state;
        state = new PulseMotionState(element);
        element.SetValue(PulseStateProperty, state);
        return state;
    }

    private sealed class RowMotionState
    {
        private readonly FrameworkElement element;
        private readonly ScaleTransform scale = new(1, 1);
        private readonly TranslateTransform translate = new(0, 0);
        private Transform? original;
        private Point origin;
        private bool attached;
        private RowSelectionMotionMode? activeMode;
        private bool? lastHovered;
        private bool? lastSelected;
        private int revision;

        internal RowMotionState(FrameworkElement element)
        {
            this.element = element;
            element.Loaded += (_, _) => Apply(animate: false);
            element.Unloaded += (_, _) => Reset();
            element.DataContextChanged += (_, _) => Apply(animate: false);
            element.IsVisibleChanged += (_, _) =>
            {
                if (element.IsVisible) Apply(animate: false);
                else Reset();
            };
        }

        internal void Apply(bool animate = true)
        {
            var mode = GetMode(element);
            if (mode == RowSelectionMotionMode.None || !element.IsLoaded || !element.IsVisible)
            {
                Reset();
                return;
            }

            if (!attached || activeMode != mode)
            {
                if (attached) DetachTransforms();
                original = element.RenderTransform;
                origin = element.RenderTransformOrigin;
                var group = new TransformGroup();
                if (original is not null && !ReferenceEquals(original, Transform.Identity))
                    group.Children.Add(original);
                group.Children.Add(scale);
                group.Children.Add(translate);
                element.SetCurrentValue(UIElement.RenderTransformProperty, group);
                element.SetCurrentValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
                attached = true;
                activeMode = mode;
                animate = false;
            }

            var hovered = GetIsHovered(element);
            var selected = GetIsSelected(element);
            var hadState = lastHovered.HasValue && lastSelected.HasValue;
            if (hadState && lastHovered == hovered && lastSelected == selected && animate && InterfaceMotion.Enabled)
                return;

            lastHovered = hovered;
            lastSelected = selected;
            var current = ++revision;
            ClearClocks();

            switch (mode)
            {
                case RowSelectionMotionMode.AccentBar:
                {
                    var targetY = selected ? 1.0 : hovered ? 0.5 : 0.0;
                    var fromY = scale.ScaleY;
                    scale.ScaleX = 1.0;
                    translate.X = translate.Y = 0.0;
                    if (!animate || !hadState || !InterfaceMotion.Enabled || Math.Abs(fromY - targetY) < 0.001)
                    {
                        scale.ScaleY = targetY;
                        return;
                    }
                    scale.ScaleY = fromY;
                    var anim = new DoubleAnimation(fromY, targetY, TimeSpan.FromMilliseconds(selected || hovered ? 180 : 150))
                    {
                        EasingFunction = selected || hovered
                            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.42 }
                            : new CubicEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    anim.Completed += (_, _) =>
                    {
                        if (current != revision) return;
                        scale.ScaleY = targetY;
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim, HandoffBehavior.SnapshotAndReplace);
                    break;
                }
                case RowSelectionMotionMode.ContentSlide:
                {
                    var targetX = selected ? 3.2 : hovered ? 2.0 : 0.0;
                    var fromX = translate.X;
                    scale.ScaleX = scale.ScaleY = 1.0;
                    translate.Y = 0.0;
                    if (!animate || !hadState || !InterfaceMotion.Enabled || Math.Abs(fromX - targetX) < 0.01)
                    {
                        translate.X = targetX;
                        return;
                    }
                    translate.X = fromX;
                    var anim = new DoubleAnimation(fromX, targetX, TimeSpan.FromMilliseconds(160))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    anim.Completed += (_, _) =>
                    {
                        if (current != revision) return;
                        translate.X = targetX;
                        translate.BeginAnimation(TranslateTransform.XProperty, null);
                    };
                    translate.BeginAnimation(TranslateTransform.XProperty, anim, HandoffBehavior.SnapshotAndReplace);
                    break;
                }
                case RowSelectionMotionMode.ThumbnailScale:
                {
                    var targetScale = selected || hovered ? 1.07 : 1.0;
                    var fromScale = scale.ScaleX;
                    translate.X = translate.Y = 0.0;
                    if (!animate || !hadState || !InterfaceMotion.Enabled || Math.Abs(fromScale - targetScale) < 0.001)
                    {
                        scale.ScaleX = scale.ScaleY = targetScale;
                        return;
                    }
                    scale.ScaleX = scale.ScaleY = fromScale;
                    var easing = targetScale > 1.0
                        ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.38 }
                        : new CubicEase { EasingMode = EasingMode.EaseOut };
                    var duration = TimeSpan.FromMilliseconds(targetScale > 1.0 ? 180 : 150);
                    var animX = new DoubleAnimation(fromScale, targetScale, duration)
                    {
                        EasingFunction = easing,
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    var animY = new DoubleAnimation(fromScale, targetScale, duration)
                    {
                        EasingFunction = easing,
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    animX.Completed += (_, _) =>
                    {
                        if (current != revision) return;
                        scale.ScaleX = scale.ScaleY = targetScale;
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, animX, HandoffBehavior.SnapshotAndReplace);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, animY, HandoffBehavior.SnapshotAndReplace);
                    break;
                }
            }
        }

        private void ClearClocks()
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
        }

        private void DetachTransforms()
        {
            ClearClocks();
            scale.ScaleX = scale.ScaleY = 1.0;
            translate.X = translate.Y = 0.0;
            if (attached)
            {
                element.SetCurrentValue(UIElement.RenderTransformProperty, original ?? Transform.Identity);
                element.SetCurrentValue(UIElement.RenderTransformOriginProperty, origin);
            }
            attached = false;
            original = null;
        }

        private void Reset()
        {
            revision++;
            DetachTransforms();
            activeMode = null;
            lastHovered = null;
            lastSelected = null;
        }
    }

    private sealed class PulseMotionState
    {
        private readonly FrameworkElement element;
        private readonly TranslateTransform offset = new();
        private readonly ScaleTransform scale = new(1, 1);
        private Transform? installedTransform;
        private int revision;

        internal PulseMotionState(FrameworkElement element)
        {
            this.element = element;
            element.Unloaded += (_, _) => Reset();
            element.IsVisibleChanged += (_, _) => { if (!element.IsVisible) Reset(); };
        }

        internal void SlideTitle()
        {
            if (!element.IsLoaded || !element.IsVisible || !InterfaceMotion.Enabled)
            {
                Reset();
                return;
            }
            EnsureInstalled();
            var current = ++revision;
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            offset.X = 4.0;
            var anim = new DoubleAnimation(4.0, 0.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (_, _) =>
            {
                if (current == revision) Reset();
            };
            offset.BeginAnimation(TranslateTransform.XProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        internal void BumpBadge()
        {
            if (!element.IsLoaded || !element.IsVisible || !InterfaceMotion.Enabled)
            {
                Reset();
                return;
            }
            EnsureInstalled();
            var current = ++revision;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            var framesX = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220), FillBehavior = FillBehavior.Stop };
            framesX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
            framesX.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromPercent(0.45), new CubicEase { EasingMode = EasingMode.EaseOut }));
            framesX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new CubicEase { EasingMode = EasingMode.EaseInOut }));

            var framesY = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220), FillBehavior = FillBehavior.Stop };
            framesY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
            framesY.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromPercent(0.45), new CubicEase { EasingMode = EasingMode.EaseOut }));
            framesY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new CubicEase { EasingMode = EasingMode.EaseInOut }));

            framesX.Completed += (_, _) =>
            {
                if (current == revision) Reset();
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, framesX, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, framesY, HandoffBehavior.SnapshotAndReplace);
        }

        private void EnsureInstalled()
        {
            if (ReferenceEquals(element.RenderTransform, installedTransform)) return;
            var original = element.RenderTransform;
            var group = new TransformGroup();
            if (original is not null && !ReferenceEquals(original, Transform.Identity))
                group.Children.Add(original);
            group.Children.Add(scale);
            group.Children.Add(offset);
            installedTransform = group;
            element.SetCurrentValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
            element.SetCurrentValue(UIElement.RenderTransformProperty, installedTransform);
        }

        private void Reset()
        {
            revision++;
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            offset.X = offset.Y = 0;
            scale.ScaleX = scale.ScaleY = 1.0;
        }
    }
}
