using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Presentation;

/// <summary>Keeps a usable thumb on long lists without changing the scroll range.</summary>
public static class ModScrollInteraction
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ModScrollInteraction), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyPropertyKey IsExpandedPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsExpanded", typeof(bool), typeof(ModScrollInteraction), new PropertyMetadata(false));
    public static readonly DependencyProperty IsExpandedProperty = IsExpandedPropertyKey.DependencyProperty;
    private static readonly DependencyProperty ExpansionProperty = DependencyProperty.RegisterAttached(
        "Expansion", typeof(double), typeof(ModScrollInteraction), new PropertyMetadata(0d, ExpansionChanged));
    private static readonly DependencyPropertyKey GripWidthPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "GripWidth", typeof(double), typeof(ModScrollInteraction), new PropertyMetadata(6d));
    public static readonly DependencyProperty GripWidthProperty = GripWidthPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey GripOpacityPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "GripOpacity", typeof(double), typeof(ModScrollInteraction), new PropertyMetadata(0.6d));
    public static readonly DependencyProperty GripOpacityProperty = GripOpacityPropertyKey.DependencyProperty;
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetIsExpanded(DependencyObject target) => (bool)target.GetValue(IsExpandedProperty);
    public static double GetGripWidth(DependencyObject target) => (double)target.GetValue(GripWidthProperty);
    public static double GetGripOpacity(DependencyObject target) => (double)target.GetValue(GripOpacityProperty);

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ScrollBar bar) return;
        if ((bool)e.NewValue)
        {
            bar.Loaded += Loaded;
            bar.Unloaded += Unloaded;
            bar.SizeChanged += SizeChanged;
            bar.MouseEnter += Enter;
            bar.MouseLeave += Leave;
            bar.LostMouseCapture += CaptureLost;
            bar.GotMouseCapture += CaptureGained;
            Refresh(bar, bar.IsMouseOver, animate: false);
        }
        else
        {
            bar.Loaded -= Loaded;
            bar.Unloaded -= Unloaded;
            bar.SizeChanged -= SizeChanged;
            bar.MouseEnter -= Enter;
            bar.MouseLeave -= Leave;
            bar.LostMouseCapture -= CaptureLost;
            bar.GotMouseCapture -= CaptureGained;
            bar.BeginAnimation(ExpansionProperty, null);
            bar.Resources.Remove(SystemParameters.VerticalScrollBarButtonHeightKey);
            bar.ClearValue(IsExpandedPropertyKey);
        }
    }

    private static void Loaded(object sender, RoutedEventArgs e) => Refresh((ScrollBar)sender, ((ScrollBar)sender).IsMouseOver, animate: false);
    private static void Unloaded(object sender, RoutedEventArgs e) => StopAnimation((ScrollBar)sender);
    private static void SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGeometry((ScrollBar)sender);
    private static void Enter(object sender, MouseEventArgs e) => Refresh((ScrollBar)sender, true);
    private static void Leave(object sender, MouseEventArgs e)
    {
        var bar = (ScrollBar)sender;
        if (!bar.IsMouseCaptureWithin) Refresh(bar, false);
    }
    private static void CaptureLost(object sender, MouseEventArgs e)
    {
        var bar = (ScrollBar)sender;
        if (!bar.IsMouseCaptureWithin) Refresh(bar, bar.IsMouseOver);
    }

    private static void CaptureGained(object sender, MouseEventArgs e)
    {
        // A moving Track changes its drag-distance conversion. Freeze its current
        // geometry while captured, then resume easing after the drag is released.
        var bar = (ScrollBar)sender;
        if (bar.IsMouseCaptureWithin) StopAnimation(bar);
    }

    private static void Refresh(ScrollBar bar, bool expanded, bool animate = true)
    {
        bar.SetValue(IsExpandedPropertyKey, expanded);
        if (bar.IsMouseCaptureWithin) return;
        var current = (double)bar.GetValue(ExpansionProperty);
        var target = expanded ? 1d : 0d;
        bar.BeginAnimation(ExpansionProperty, null);
        bar.SetValue(ExpansionProperty, target);
        if (!animate || !bar.IsLoaded || !SystemParameters.ClientAreaAnimation || Math.Abs(current - target) < 0.001) return;
        var motion = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        bar.BeginAnimation(ExpansionProperty, motion, HandoffBehavior.SnapshotAndReplace);
    }

    private static void StopAnimation(ScrollBar bar)
    {
        var current = (double)bar.GetValue(ExpansionProperty);
        bar.BeginAnimation(ExpansionProperty, null);
        bar.SetValue(ExpansionProperty, current);
    }

    private static void ExpansionChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) => UpdateGeometry((ScrollBar)target);

    private static void UpdateGeometry(ScrollBar bar)
    {
        var progress = Math.Clamp((double)bar.GetValue(ExpansionProperty), 0, 1);
        bar.SetValue(GripWidthPropertyKey, 6 + 8 * progress);
        bar.SetValue(GripOpacityPropertyKey, 0.6 + 0.4 * progress);
        // WPF Track uses half this resource as its minimum thumb length and factors
        // it into drag distance. Thumb.MinHeight alone would break that mapping.
        var minimum = Math.Min(40 + 24 * progress, Math.Max(0, bar.ActualHeight / 2));
        bar.Resources[SystemParameters.VerticalScrollBarButtonHeightKey] = minimum * 2;
        if (bar.Template?.FindName("PART_Track", bar) is Track track) track.InvalidateArrange();
    }
}
