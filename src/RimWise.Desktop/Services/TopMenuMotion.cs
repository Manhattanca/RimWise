using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RimWise.Desktop.Services;

/// <summary>
/// High-contrast top-bar menu hover, active, and press motion that stays clearly visible
/// even when the user lowers top-button or panel opacity to zero.
/// </summary>
public static class TopMenuMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(TopMenuMotion), new PropertyMetadata(false, Changed));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ItemMotion), typeof(TopMenuMotion));

    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);

    internal static void SetHoveredForCheck(MenuItem item, bool hovered)
    {
        if (item.GetValue(StateProperty) is ItemMotion state)
            state.SetHovered(hovered);
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not MenuItem item) return;
        if (item.GetValue(StateProperty) is ItemMotion existing)
        {
            existing.Detach();
            item.ClearValue(StateProperty);
        }
        if (args.NewValue is true)
        {
            var state = new ItemMotion(item);
            item.SetValue(StateProperty, state);
            state.Attach();
        }
    }

    private sealed class ItemMotion(MenuItem item)
    {
        private Border? chrome;
        private Border? hoverFace;
        private Border? hoverFill;
        private Border? accentBar;
        private ScaleTransform? hoverScale;
        private ScaleTransform? accentScale;
        private FrameworkElement? content;
        private TranslateTransform? translate;
        private ScaleTransform? contentScale;
        private bool attached;
        private bool pressed;
        private bool hovered;
        private int motionRevision;
        private int faceRevision;

        internal void Attach()
        {
            if (attached) return;
            attached = true;
            item.Loaded += Loaded;
            item.Unloaded += Unloaded;
            item.MouseEnter += MouseEntered;
            item.MouseLeave += MouseLeft;
            item.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(MouseDown), true);
            item.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(MouseUp), true);
            item.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(CaptureLost), true);
            item.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(KeyDown), true);
            item.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(KeyUp), true);
            item.SubmenuOpened += HoverChanged;
            item.SubmenuClosed += HoverChanged;
            if (item.IsLoaded) Loaded(item, new RoutedEventArgs());
        }

        internal void Detach()
        {
            if (!attached) return;
            attached = false;
            item.Loaded -= Loaded;
            item.Unloaded -= Unloaded;
            item.MouseEnter -= MouseEntered;
            item.MouseLeave -= MouseLeft;
            item.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(MouseDown));
            item.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(MouseUp));
            item.RemoveHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(CaptureLost));
            item.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(KeyDown));
            item.RemoveHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(KeyUp));
            item.SubmenuOpened -= HoverChanged;
            item.SubmenuClosed -= HoverChanged;
            pressed = false;
            hovered = false;
            Reset();
        }

        private void Loaded(object sender, RoutedEventArgs e)
        {
            item.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!attached || !item.IsLoaded) return;
                item.ApplyTemplate();
                chrome = item.Template?.FindName("TopMenuChrome", item) as Border;
                hoverFace = item.Template?.FindName("TopMenuHoverFace", item) as Border;
                hoverFill = item.Template?.FindName("TopMenuHoverFill", item) as Border;
                accentBar = item.Template?.FindName("TopMenuAccentBar", item) as Border;

                hoverScale = hoverFace?.RenderTransform as ScaleTransform;
                if (hoverFace is not null && (hoverScale is null || hoverScale.IsFrozen))
                {
                    hoverScale = new ScaleTransform(.92, .92);
                    hoverFace.SetCurrentValue(UIElement.RenderTransformProperty, hoverScale);
                }

                accentScale = accentBar?.RenderTransform as ScaleTransform;
                if (accentBar is not null && (accentScale is null || accentScale.IsFrozen))
                {
                    accentScale = new ScaleTransform(.2, 1);
                    accentBar.SetCurrentValue(UIElement.RenderTransformProperty, accentScale);
                }

                content = item.Template?.FindName("TopMenuContent", item) as FrameworkElement;
                if (chrome is null || content is null) return;
                content.SetCurrentValue(UIElement.RenderTransformOriginProperty, new Point(.5, .5));
                translate = FindTranslate(content.RenderTransform);
                contentScale = FindScale(content.RenderTransform);
                if (translate is null || contentScale is null)
                {
                    translate ??= new TranslateTransform();
                    contentScale ??= new ScaleTransform(1, 1);
                    var group = new TransformGroup();
                    group.Children.Add(contentScale);
                    group.Children.Add(translate);
                    content.SetCurrentValue(UIElement.RenderTransformProperty, group);
                }
                Update();
            }));
        }

        private void Unloaded(object sender, RoutedEventArgs e)
        {
            hovered = false;
            Reset();
            chrome = null;
            hoverFace = null;
            hoverFill = null;
            accentBar = null;
            hoverScale = null;
            accentScale = null;
            content = null;
            translate = null;
            contentScale = null;
        }

        private void MouseEntered(object? sender, RoutedEventArgs e) => SetHovered(true);
        private void MouseLeft(object? sender, RoutedEventArgs e) => SetHovered(false);
        internal void SetHovered(bool value) { hovered = value; Update(); }
        private void HoverChanged(object? sender, RoutedEventArgs e) => Update();
        private void MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) { pressed = true; Update(); } }
        private void MouseUp(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) { pressed = false; Update(); } }
        private void CaptureLost(object sender, MouseEventArgs e) { pressed = false; Update(); }
        private void KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Space or Key.Enter) { pressed = true; Update(); }
        }
        private void KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Space or Key.Enter) { pressed = false; Update(); }
        }

        private void Update()
        {
            if (chrome is null || translate is null) return;
            var isHovered = hovered || item.IsMouseOver || item.IsHighlighted;
            var isOpen = item.IsSubmenuOpen;
            var isActive = pressed || isHovered || isOpen;

            var targetX = pressed ? 1.0 : (isHovered || isOpen) ? 2.0 : 0.0;
            var targetY = pressed ? 0.4 : (isHovered || isOpen) ? -0.6 : 0.0;
            var targetContentScale = pressed ? 0.96 : (isHovered || isOpen) ? 1.03 : 1.0;

            AnimateContent(targetX, targetY, targetContentScale, ++motionRevision);
            AnimateHoverFace(isActive, pressed, isOpen);
        }

        private void AnimateHoverFace(bool active, bool isPressed, bool isOpen)
        {
            if (hoverFace is null || hoverScale is null) return;
            var opacityTarget = active ? 1d : 0d;
            var scaleTarget = !active ? .92d : isPressed ? .97d : 1d;
            var fillTarget = isPressed || isOpen ? .38d : active ? .28d : 0d;
            var barScaleTarget = !active ? .2d : isPressed ? .88d : 1d;
            var revision = ++faceRevision;

            if (!InterfaceMotion.Enabled)
            {
                hoverFace.BeginAnimation(UIElement.OpacityProperty, null);
                hoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                hoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                hoverFace.Opacity = opacityTarget;
                hoverScale.ScaleX = scaleTarget;
                hoverScale.ScaleY = scaleTarget;
                if (hoverFill is not null)
                {
                    hoverFill.BeginAnimation(UIElement.OpacityProperty, null);
                    hoverFill.Opacity = fillTarget;
                }
                if (accentScale is not null)
                {
                    accentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    accentScale.ScaleX = barScaleTarget;
                }
                return;
            }

            var opacity = new DoubleAnimation(hoverFace.Opacity, opacityTarget, TimeSpan.FromMilliseconds(active ? 115 : 150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            var scaleEase = active
                ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            var scaleX = new DoubleAnimation(hoverScale.ScaleX, scaleTarget, TimeSpan.FromMilliseconds(active ? 165 : 150))
            {
                EasingFunction = scaleEase,
                FillBehavior = FillBehavior.HoldEnd
            };
            var scaleY = new DoubleAnimation(hoverScale.ScaleY, scaleTarget, TimeSpan.FromMilliseconds(active ? 165 : 150))
            {
                EasingFunction = scaleEase,
                FillBehavior = FillBehavior.HoldEnd
            };

            var face = hoverFace;
            var transform = hoverScale;
            opacity.Completed += (_, _) =>
            {
                if (revision != faceRevision || !ReferenceEquals(hoverFace, face)) return;
                face.Opacity = opacityTarget;
                face.BeginAnimation(UIElement.OpacityProperty, null);
            };
            scaleX.Completed += (_, _) =>
            {
                if (revision != faceRevision || !ReferenceEquals(hoverScale, transform)) return;
                transform.ScaleX = scaleTarget;
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            };
            scaleY.Completed += (_, _) =>
            {
                if (revision != faceRevision || !ReferenceEquals(hoverScale, transform)) return;
                transform.ScaleY = scaleTarget;
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            };
            face.BeginAnimation(UIElement.OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX, HandoffBehavior.SnapshotAndReplace);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY, HandoffBehavior.SnapshotAndReplace);

            if (hoverFill is not null)
            {
                var fill = hoverFill;
                var fillAnim = new DoubleAnimation(fill.Opacity, fillTarget, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                fillAnim.Completed += (_, _) =>
                {
                    if (revision != faceRevision || !ReferenceEquals(hoverFill, fill)) return;
                    fill.Opacity = fillTarget;
                    fill.BeginAnimation(UIElement.OpacityProperty, null);
                };
                fill.BeginAnimation(UIElement.OpacityProperty, fillAnim, HandoffBehavior.SnapshotAndReplace);
            }

            if (accentScale is not null)
            {
                var barTransform = accentScale;
                var barEase = active
                    ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
                    : new CubicEase { EasingMode = EasingMode.EaseOut };
                var barAnim = new DoubleAnimation(barTransform.ScaleX, barScaleTarget, TimeSpan.FromMilliseconds(active ? 190 : 140))
                {
                    EasingFunction = barEase,
                    FillBehavior = FillBehavior.HoldEnd
                };
                barAnim.Completed += (_, _) =>
                {
                    if (revision != faceRevision || !ReferenceEquals(accentScale, barTransform)) return;
                    barTransform.ScaleX = barScaleTarget;
                    barTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                };
                barTransform.BeginAnimation(ScaleTransform.ScaleXProperty, barAnim, HandoffBehavior.SnapshotAndReplace);
            }
        }

        private void AnimateContent(double targetX, double targetY, double targetScale, int revision)
        {
            if (translate is null) return;
            if (!InterfaceMotion.Enabled)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.SetCurrentValue(TranslateTransform.XProperty, targetX);
                translate.SetCurrentValue(TranslateTransform.YProperty, targetY);
                if (contentScale is not null)
                {
                    contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    contentScale.ScaleX = targetScale;
                    contentScale.ScaleY = targetScale;
                }
                return;
            }
            var animX = new DoubleAnimation(translate.X, targetX, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            var animY = new DoubleAnimation(translate.Y, targetY, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            var activeTranslate = translate;
            animX.Completed += (_, _) =>
            {
                if (revision == motionRevision && ReferenceEquals(translate, activeTranslate))
                {
                    activeTranslate.SetCurrentValue(TranslateTransform.XProperty, targetX);
                    activeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                }
            };
            animY.Completed += (_, _) =>
            {
                if (revision == motionRevision && ReferenceEquals(translate, activeTranslate))
                {
                    activeTranslate.SetCurrentValue(TranslateTransform.YProperty, targetY);
                    activeTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                }
            };
            translate.BeginAnimation(TranslateTransform.XProperty, animX, HandoffBehavior.SnapshotAndReplace);
            translate.BeginAnimation(TranslateTransform.YProperty, animY, HandoffBehavior.SnapshotAndReplace);

            if (contentScale is not null)
            {
                var activeScale = contentScale;
                var animScaleX = new DoubleAnimation(activeScale.ScaleX, targetScale, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                var animScaleY = new DoubleAnimation(activeScale.ScaleY, targetScale, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                animScaleX.Completed += (_, _) =>
                {
                    if (revision == motionRevision && ReferenceEquals(contentScale, activeScale))
                    {
                        activeScale.ScaleX = targetScale;
                        activeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    }
                };
                animScaleY.Completed += (_, _) =>
                {
                    if (revision == motionRevision && ReferenceEquals(contentScale, activeScale))
                    {
                        activeScale.ScaleY = targetScale;
                        activeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    }
                };
                activeScale.BeginAnimation(ScaleTransform.ScaleXProperty, animScaleX, HandoffBehavior.SnapshotAndReplace);
                activeScale.BeginAnimation(ScaleTransform.ScaleYProperty, animScaleY, HandoffBehavior.SnapshotAndReplace);
            }
        }

        private static TranslateTransform? FindTranslate(Transform transform) => transform switch
        {
            TranslateTransform value => value,
            TransformGroup group => group.Children.OfType<TranslateTransform>().FirstOrDefault(),
            _ => null
        };

        private static ScaleTransform? FindScale(Transform transform) => transform switch
        {
            ScaleTransform value => value,
            TransformGroup group => group.Children.OfType<ScaleTransform>().FirstOrDefault(),
            _ => null
        };

        private void Reset()
        {
            motionRevision++;
            faceRevision++;
            if (translate is not null)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.SetCurrentValue(TranslateTransform.XProperty, 0d);
                translate.SetCurrentValue(TranslateTransform.YProperty, 0d);
            }
            if (contentScale is not null)
            {
                contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                contentScale.ScaleX = 1d;
                contentScale.ScaleY = 1d;
            }
            hoverFace?.BeginAnimation(UIElement.OpacityProperty, null);
            hoverScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            hoverScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            hoverFill?.BeginAnimation(UIElement.OpacityProperty, null);
            accentScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            if (hoverFace is not null) hoverFace.Opacity = 0;
            if (hoverScale is not null) { hoverScale.ScaleX = .92; hoverScale.ScaleY = .92; }
            if (hoverFill is not null) hoverFill.Opacity = 0;
            if (accentScale is not null) accentScale.ScaleX = .2;
        }
    }
}