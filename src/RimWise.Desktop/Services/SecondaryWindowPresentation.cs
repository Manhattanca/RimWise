using System.Windows;

namespace RimWise.Desktop.Services;

/// <summary>Initialize themed HWNDs before Show, then reveal only after the first composed frame.</summary>
public static class SecondaryWindowPresentation
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SecondaryWindowPresentation), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty PresentationProperty = DependencyProperty.RegisterAttached(
        "Presentation", typeof(StartupWindowPresentation), typeof(SecondaryWindowPresentation));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    internal static StartupWindowPresentation? GetPresentation(Window window) => (StartupWindowPresentation?)window.GetValue(PresentationProperty);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not true || target is not Window window || window is MainWindow
            || window.GetType().Assembly != typeof(App).Assembly || GetPresentation(window) is not null) return;
        window.SourceInitialized += (_, _) => WindowFrameTheme.Apply(window);
        window.SetValue(PresentationProperty, new StartupWindowPresentation(window));
    }
}
