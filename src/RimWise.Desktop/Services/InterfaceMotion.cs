using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Services;

internal static class InterfaceMotion
{
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(InterfaceMotion), new PropertyMetadata(false));
    internal static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    internal static void Install()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
        EventManager.RegisterClassHandler(typeof(Popup), FrameworkElement.LoadedEvent, new RoutedEventHandler(PopupLoaded));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => Reveal((FrameworkElement)sender)));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
            new RoutedEventHandler((sender, _) => Reveal((FrameworkElement)sender)));
    }

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || e.OriginalSource != window) return;
        WindowFrameTheme.Apply(window);
        if (window is not MainWindow && window.GetType().Assembly == typeof(App).Assembly
            && window.Content is FrameworkElement content) Reveal(content);
    }

    private static void PopupLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Popup popup || (bool)popup.GetValue(AttachedProperty)) return;
        popup.SetValue(AttachedProperty, true);
        popup.Opened += (_, _) =>
        {
            if (popup.Child is FrameworkElement child && child is not ContextMenu and not ToolTip) Reveal(child);
        };
        if (popup.IsOpen && popup.Child is FrameworkElement initial && initial is not ContextMenu and not ToolTip) Reveal(initial);
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
}
