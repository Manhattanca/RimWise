using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Services;

/// <summary>Short, replaceable feedback inside a stable native Button hit area. No timers or click handlers.</summary>
public static class ButtonFeedbackMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ButtonFeedbackMotion), new PropertyMetadata(false, Changed));
    public static bool GetIsEnabled(DependencyObject value) => (bool)value.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject value, bool enabled) => value.SetValue(IsEnabledProperty, enabled);
    internal static double TintOpacity(bool pressed, bool hovered) => pressed ? 1 : hovered ? .22 : 0;
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Feedback), typeof(ButtonFeedbackMotion));
    private static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached(
        "Pressed", typeof(bool), typeof(ButtonFeedbackMotion), new PropertyMetadata(false,
            (sender, _) => (sender.GetValue(StateProperty) as Feedback)?.Apply()));

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Button button) return;
        if (args.NewValue is true)
        {
            if (button.GetValue(StateProperty) is Feedback existing) { existing.Apply(); return; }
            var feedback = new Feedback(button);
            button.SetValue(StateProperty, feedback);
            BindingOperations.SetBinding(button, PressedProperty, new Binding(nameof(Button.IsPressed)) { Source = button });
            feedback.Apply();
        }
        else (button.GetValue(StateProperty) as Feedback)?.Reset();
    }

    private sealed class Feedback
    {
        private readonly Button button;
        private Border? face, hover;
        private readonly ScaleTransform scale = new();
        private Transform? originalTransform;
        private int revision;
        private bool? pressed, hovered;

        internal Feedback(Button button)
        {
            this.button = button;
            button.Loaded += (_, _) => Apply(animate: false);
            button.Unloaded += (_, _) => Reset();
            button.MouseEnter += (_, _) => Apply();
            button.MouseLeave += (_, _) => Apply();
            button.IsEnabledChanged += (_, _) => Apply(animate: false);
            button.IsVisibleChanged += (_, _) => { if (!button.IsVisible) Reset(); else Apply(animate: false); };
        }

        internal void Apply(bool animate = true)
        {
            if (!GetIsEnabled(button) || !button.IsLoaded || !button.IsVisible) { Reset(); return; }
            var nextFace = button.Template?.FindName("Chrome", button) as Border;
            if (!ReferenceEquals(face, nextFace))
            {
                Reset();
                var nextHover = button.Template?.FindName("HoverFace", button) as Border;
                if (nextFace is null || nextHover is null) return;
                face = nextFace;
                hover = nextHover;
                originalTransform = face.RenderTransform;
                face.SetCurrentValue(UIElement.RenderTransformProperty, scale);
            }
            if (face is null || hover is null) return;
            var nextPressed = button.IsEnabled && button.IsPressed;
            var nextHovered = button.IsEnabled && button.IsMouseOver;
            if (pressed == nextPressed && hovered == nextHovered && animate && InterfaceMotion.Enabled) return;
            pressed = nextPressed; hovered = nextHovered;
            var fromScale = scale.ScaleX; var fromHover = hover.Opacity;
            var toScale = nextPressed ? .985 : 1;
            var toHover = TintOpacity(nextPressed, nextHovered);
            var current = ++revision;
            Stop();
            scale.ScaleX = scale.ScaleY = toScale; hover.Opacity = toHover;
            if (!animate || !InterfaceMotion.Enabled || !button.IsEnabled) return;
            scale.ScaleX = scale.ScaleY = fromScale; hover.Opacity = fromHover;
            var motion = InterfaceMotion.Motion(fromScale, toScale, nextPressed ? 80 : 120);
            motion.FillBehavior = FillBehavior.HoldEnd;
            motion.Completed += (_, _) =>
            {
                if (current != revision) return;
                scale.ScaleX = scale.ScaleY = toScale; hover.Opacity = toHover; Stop();
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, motion, HandoffBehavior.SnapshotAndReplace);
            var vertical = InterfaceMotion.Motion(fromScale, toScale, nextPressed ? 80 : 120);
            vertical.FillBehavior = FillBehavior.HoldEnd;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, vertical, HandoffBehavior.SnapshotAndReplace);
            var tint = InterfaceMotion.Motion(fromHover, toHover, nextPressed ? 80 : 120);
            tint.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            tint.FillBehavior = FillBehavior.HoldEnd;
            hover.BeginAnimation(UIElement.OpacityProperty, tint, HandoffBehavior.SnapshotAndReplace);
        }

        private void Stop()
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            hover?.BeginAnimation(UIElement.OpacityProperty, null);
        }

        internal void Reset()
        {
            revision++;
            Stop();
            scale.ScaleX = scale.ScaleY = 1;
            if (face is not null) face.SetCurrentValue(UIElement.RenderTransformProperty, originalTransform ?? Transform.Identity);
            if (hover is not null) hover.Opacity = 0;
            face = hover = null; originalTransform = null;
            pressed = hovered = null;
        }
    }
}
