using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Services;

/// <summary>Expands from the trigger-facing edge and contracts back to the same point.</summary>
public sealed class AnchoredPopupMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AnchoredPopupMotion), new PropertyMetadata(false,
            (sender, args) => { if (args.NewValue is true && sender is Popup popup) Attach(popup); }));
    public static bool GetIsEnabled(DependencyObject value) => (bool)value.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject value, bool enabled) => value.SetValue(IsEnabledProperty, enabled);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(AnchoredPopupMotion), typeof(AnchoredPopupMotion));
    private static readonly DependencyProperty ObservedOpenProperty = DependencyProperty.RegisterAttached(
        "ObservedOpen", typeof(bool), typeof(AnchoredPopupMotion), new PropertyMetadata(false, OpenChanged));
    private readonly Popup popup;
    private FrameworkElement? surface;
    private Transform? originalTransform;
    private ScaleTransform? scale;
    private bool closing;
    private const double CollapsedScale = 0;

    private AnchoredPopupMotion(Popup popup) => this.popup = popup;

    internal static void Attach(Popup popup)
    {
        popup.SetResourceReference(Popup.PopupAnimationProperty, "RimWisePopupAnimation");
        if (popup.GetValue(StateProperty) is AnchoredPopupMotion existing)
        {
            if (popup.IsOpen && existing.surface is null) existing.Open();
            return;
        }
        var state = new AnchoredPopupMotion(popup);
        popup.SetValue(StateProperty, state);
        popup.Opened += (_, _) => state.Open();
        popup.Closed += (_, _) => state.Reset();
        // A self-binding avoids a global DependencyPropertyDescriptor retaining closed windows.
        BindingOperations.SetBinding(popup, ObservedOpenProperty,
            new Binding(nameof(Popup.IsOpen)) { Source = popup, Mode = BindingMode.OneWay });
        if (popup.IsOpen) state.Open();
    }

    private static void OpenChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && sender.GetValue(StateProperty) is AnchoredPopupMotion state)
            state.Close();
    }

    private void Open()
    {
        if (!InterfaceMotion.Enabled || !popup.AllowsTransparency || popup.Child is null) return;
        DependencyObject root = popup.Child;
        while (VisualTreeHelper.GetParent(root) is { } parent) root = parent;
        if (root is not FrameworkElement element || PresentationSource.FromVisual(element) is null) return;
        var resume = ReferenceEquals(surface, element) && closing && scale is not null;
        if (!ReferenceEquals(surface, element))
        {
            Reset();
            surface = element;
            surface.UpdateLayout();
            originalTransform = surface.RenderTransform;
            var anchor = FindAnchor(surface);
            scale = new ScaleTransform(1, 1, anchor.X, anchor.Y);
            var transforms = new TransformGroup();
            transforms.Children.Add(originalTransform);
            transforms.Children.Add(scale);
            surface.SetCurrentValue(UIElement.RenderTransformProperty, transforms);
        }
        closing = false;
        // PopupRoot starts a Fade before Opened; keep the content fully opaque.
        surface!.BeginAnimation(UIElement.OpacityProperty, null);
        Animate(resume ? scale!.ScaleY : CollapsedScale, 1, 120, EasingMode.EaseOut);
    }

    private Point FindAnchor(FrameworkElement root)
    {
        Point point;
        if (popup.Placement is PlacementMode.Mouse or PlacementMode.MousePoint)
            point = Mouse.GetPosition(root);
        else if ((popup.PlacementTarget ?? popup.TemplatedParent as UIElement) is UIElement target
            && PresentationSource.FromVisual(target) is not null)
            point = root.PointFromScreen(target.PointToScreen(new Point(target.RenderSize.Width / 2, target.RenderSize.Height / 2)));
        else point = new Point(root.ActualWidth / 2, 0);
        return ClampAnchor(point, root.RenderSize);
    }

    internal static Point ClampAnchor(Point point, Size size) =>
        new(Math.Clamp(point.X, 0, size.Width), Math.Clamp(point.Y, 0, size.Height));

    private void Close()
    {
        if (surface is null || scale is null) return;
        // WPF has already released capture and scheduled destruction. Replace only
        // presentation, finishing within its 150ms close lifetime (including frame slack).
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        if (!InterfaceMotion.Enabled) { Reset(); return; }
        closing = true;
        Animate(scale.ScaleY, CollapsedScale, 90, EasingMode.EaseIn);
    }

    private void Animate(double from, double to, double milliseconds, EasingMode easing)
    {
        if (scale is null) return;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = easing }, FillBehavior = FillBehavior.Stop
        };
        scale.ScaleY = to;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void Reset()
    {
        if (surface is not null)
        {
            scale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            surface.SetCurrentValue(UIElement.RenderTransformProperty, originalTransform ?? Transform.Identity);
        }
        surface = null;
        originalTransform = null;
        scale = null;
        closing = false;
    }
}
