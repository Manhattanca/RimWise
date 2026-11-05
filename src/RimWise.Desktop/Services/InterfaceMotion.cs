using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Services;

internal static class InterfaceMotion
{
    internal static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

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
            && window.Content is FrameworkElement content) Reveal(content);
    }

    internal static void Reveal(FrameworkElement element)
    {
        if (!Enabled) return;
        // Animate presentation only: no delayed focus, dialog results or button actions.
        var animation = new DoubleAnimation(0, element.Opacity,
            TimeSpan.FromMilliseconds(167)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => element.BeginAnimation(UIElement.OpacityProperty, null);
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    internal static void SlidePage(FrameworkElement element, bool fromRight)
    {
        // Translation only: no singular scale, delayed selection or bitmap of a large list.
        var offset = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = offset;
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        offset.X = 0;
        if (!Enabled) return;
        offset.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromRight ? 24 : -24, 0, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
}
