using System.Windows;

namespace RimWise.Desktop.Services;

/// <summary>Stable scope for any first-level page, including lazily hosted and embedded content.</summary>
public static class AppearancePage
{
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(string), typeof(AppearancePage), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, ScopeChanged));
    public static string? GetScope(DependencyObject element) => (string?)element.GetValue(ScopeProperty);
    public static void SetScope(DependencyObject element, string value) => element.SetValue(ScopeProperty, value);
    public static readonly DependencyProperty IsToolingProperty = DependencyProperty.RegisterAttached(
        "IsTooling", typeof(bool), typeof(AppearancePage), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsTooling(DependencyObject element) => (bool)element.GetValue(IsToolingProperty);
    public static void SetIsTooling(DependencyObject element, bool value) => element.SetValue(IsToolingProperty, value);
    private static void ScopeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        // WPF can skip Loaded broadcasts on newly created subtrees with only class handlers.
        // A scoped instance handler makes the lazy-page lifecycle observable as well.
        element.Loaded -= Loaded;
        // Keep lifecycle hooks on scope hosts and named non-template controls,
        // including controls inserted into an already-loaded page. Per-template
        // descendant hooks otherwise rescan the window when recycled rows load.
        if (e.NewValue is string && (element.ReadLocalValue(ScopeProperty) != DependencyProperty.UnsetValue
            || element is { Name.Length: > 0, TemplatedParent: null }))
            element.Loaded += Loaded;
        if (element.IsLoaded && Window.GetWindow(element) is MainWindow main) main.AppearanceControls?.RequestRefresh();
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && Window.GetWindow(element) is MainWindow main)
            main.AppearanceControls?.RequestRefresh();
    }
}
