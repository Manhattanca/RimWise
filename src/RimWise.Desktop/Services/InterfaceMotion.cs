using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Services;

internal static class InterfaceMotion
{
    internal static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast
#if DEBUG
        && !ReduceMotionForCheck
#endif
        ;
#if DEBUG
    internal static bool ReduceMotionForCheck { get; set; }
#endif
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(PresentationState), typeof(InterfaceMotion));

    internal static void Install()
    {
        RefreshPopupAnimation();
        SystemParameters.StaticPropertyChanged += (_, _) =>
        {
            var dispatcher = Application.Current.Dispatcher;
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(RefreshPopupAnimation);
        };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
        EventManager.RegisterClassHandler(typeof(Popup), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, e) =>
            {
                if (sender is Popup popup && ReferenceEquals(e.OriginalSource, popup))
                    AnchoredPopupMotion.Attach(popup);
            }));
        EventManager.RegisterClassHandler(typeof(MenuItem), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, e) =>
            {
                // Fluent submenu triggers otherwise suppress motion when switching headers.
                if (sender is MenuItem item && ReferenceEquals(e.OriginalSource, item)
                    && item.Template?.FindName("PART_Popup", item) is Popup popup)
                    AnchoredPopupMotion.Attach(popup);
            }));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler((sender, e) =>
            {
                // A MenuItem can acquire its submenu template after Loaded as its role changes.
                if (sender is MenuItem item && ReferenceEquals(e.OriginalSource, item)
                    && item.Template?.FindName("PART_Popup", item) is Popup popup)
                    AnchoredPopupMotion.Attach(popup);
            }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is ContextMenu { Parent: Popup popup }) AnchoredPopupMotion.Attach(popup);
            }));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is ToolTip { Parent: Popup popup }) AnchoredPopupMotion.Attach(popup);
            }));
    }

    internal static void RefreshPopupAnimation()
    {
        // Reserve WPF's native 150ms close lifetime. AnchoredPopupMotion replaces
        // its opacity clock with geometry animation; commands and capture stay native.
        var animation = Enabled ? PopupAnimation.Fade : PopupAnimation.None;
        var resources = Application.Current.Resources;
        resources["RimWisePopupAnimation"] = animation;
        resources[SystemParameters.MenuPopupAnimationKey] = animation;
        resources[SystemParameters.ToolTipPopupAnimationKey] = animation;
        resources[SystemParameters.ComboBoxPopupAnimationKey] = animation;
    }

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || e.OriginalSource != window) return;
        WindowFrameTheme.Apply(window);
        if (window is not MainWindow && window.GetType().Assembly == typeof(App).Assembly
            && window.Content is FrameworkElement content) SlidePage(content, fromRight: true);
    }

    internal static void Reveal(FrameworkElement element)
    {
        State(element).Reveal();
    }

    internal static void RevealNotification(FrameworkElement element)
    {
        var state = State(element);
        state.Reveal();
        state.Slide(horizontal: false, initial: 5);
    }

    internal static void SlidePage(FrameworkElement element, bool fromRight)
    {
        State(element).Slide(horizontal: true, initial: fromRight ? 12 : -12);
    }

    private static PresentationState State(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is PresentationState state) return state;
        state = new PresentationState(element);
        element.SetValue(StateProperty, state);
        return state;
    }

    private sealed class PresentationState
    {
        private readonly FrameworkElement element;
        private readonly TranslateTransform offset = new();
        private Transform? installedTransform;
        private bool sliding, fading;
        private int slideRevision, fadeRevision;

        internal PresentationState(FrameworkElement element)
        {
            this.element = element;
            element.Unloaded += (_, _) => Reset();
            element.IsVisibleChanged += (_, _) => { if (!element.IsVisible) Reset(); };
        }

        internal void Reveal()
        {
            if (!Enabled) { Reset(); return; }
            var from = fading ? element.Opacity : 0;
            var to = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
            var revision = ++fadeRevision;
            fading = true;
            var animation = Motion(from, to, 150);
            animation.Completed += (_, _) =>
            {
                if (revision != fadeRevision) return;
                fading = false;
                element.BeginAnimation(UIElement.OpacityProperty, null);
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        internal void Slide(bool horizontal, double initial)
        {
            // Preserve caller-owned transforms; only animate our presentation offset.
            if (!ReferenceEquals(element.RenderTransform, installedTransform))
            {
                ResetSlide();
                var original = element.RenderTransform;
                installedTransform = ReferenceEquals(original, Transform.Identity) ? offset
                    : new TransformGroup { Children = new TransformCollection { original, offset } };
                element.SetCurrentValue(UIElement.RenderTransformProperty, installedTransform);
            }
            if (!Enabled) { Reset(); return; }
            var property = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
            var from = sliding ? (double)offset.GetValue(property) : initial;
            var revision = ++slideRevision;
            sliding = true;
            var animation = Motion(from, 0, horizontal ? 160 : 150);
            animation.Completed += (_, _) =>
            {
                if (revision == slideRevision) ResetSlide();
            };
            offset.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void ResetSlide()
        {
            slideRevision++;
            sliding = false;
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            offset.BeginAnimation(TranslateTransform.YProperty, null);
            offset.X = offset.Y = 0;
        }

        private void Reset()
        {
            ResetSlide();
            fadeRevision++;
            if (fading) element.BeginAnimation(UIElement.OpacityProperty, null);
            fading = false;
        }
    }

    internal static DoubleAnimation Motion(double from, double to, double milliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
}
