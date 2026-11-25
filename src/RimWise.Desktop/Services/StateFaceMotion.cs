using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Services;

/// <summary>Small background-only expansion; labels, layout and input geometry stay still.</summary>
public static class StateFaceMotion
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool?), typeof(StateFaceMotion), new PropertyMetadata(null, Changed));
    public static bool? GetIsActive(DependencyObject value) => (bool?)value.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject value, bool? active) => value.SetValue(IsActiveProperty, active);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Face), typeof(StateFaceMotion));
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (element.GetValue(StateProperty) is not Face face)
        { face = new Face(element); element.SetValue(StateProperty, face); }
        face.Apply();
    }

    private sealed class Face
    {
        private readonly FrameworkElement element;
        private readonly ScaleTransform scale = new();
        private Transform? original;
        private Point origin;
        private bool attached;
        private bool? active;
        private int revision;
        internal Face(FrameworkElement element)
        {
            this.element = element;
            element.Loaded += (_, _) => Apply(false);
            element.Unloaded += (_, _) => Reset();
            element.DataContextChanged += (_, _) => Apply(false);
            element.IsVisibleChanged += (_, _) => { if (element.IsVisible) Apply(false); else Reset(); };
        }
        internal void Apply(bool animate = true)
        {
            if (!element.IsLoaded || !element.IsVisible) { Reset(); return; }
            if (!attached)
            {
                original = element.RenderTransform; origin = element.RenderTransformOrigin;
                var group = new TransformGroup();
                if (original is not null) group.Children.Add(original);
                group.Children.Add(scale);
                element.SetCurrentValue(UIElement.RenderTransformProperty, group);
                element.SetCurrentValue(UIElement.RenderTransformOriginProperty, new Point(.5, .5));
                attached = true;
                animate = false;
            }
            var next = GetIsActive(element) == true;
            if (active == next && animate && InterfaceMotion.Enabled) return;
            var from = scale.ScaleY;
            var target = !InterfaceMotion.Enabled || next ? 1 : .94;
            var current = ++revision;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            var hadState = active.HasValue;
            active = next;
            if (!animate || !hadState || !InterfaceMotion.Enabled || Math.Abs(from - target) < .00001)
            { scale.ScaleY = target; return; }
            scale.ScaleY = from;
            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(next ? 140 : 160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            animation.Completed += (_, _) =>
            {
                if (current != revision) return;
                scale.ScaleY = target;
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        private void Reset()
        {
            revision++;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleY = 1;
            if (attached)
            {
                element.SetCurrentValue(UIElement.RenderTransformProperty, original ?? Transform.Identity);
                element.SetCurrentValue(UIElement.RenderTransformOriginProperty, origin);
            }
            attached = false; active = null; original = null;
        }
    }
}
