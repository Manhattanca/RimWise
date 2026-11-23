using System.Windows;
using System.Windows.Media;

namespace RimWise.Desktop.Services;

/// <summary>The inspector is tooling, not a preview target. Its local palette never changes global alpha.</summary>
internal sealed class AppearanceEditorChrome
{
    private readonly Window window;
    private readonly FrameworkElement scope;
    private readonly ThemeService themes;
    private AppearanceEditorChrome(FrameworkElement scope, Window window, ThemeService themes)
    {
        this.scope = scope; this.window = window; this.themes = themes;
        Update(); themes.Changed += Changed;
        window.Closed += Closed;
    }
    internal static void Attach(Window window) => Attach(window, window);
    internal static void Attach(FrameworkElement scope, Window lifetime) => _ = new AppearanceEditorChrome(scope, lifetime, ((App)Application.Current).Themes);
    private void Changed(object? sender, EventArgs e) => Update();
    private void Closed(object? sender, EventArgs e)
    {
        themes.Changed -= Changed; window.Closed -= Closed;
    }
    private void Update()
    {
        void Copy(string key, string basis)
        {
            if (Application.Current.TryFindResource(basis) is not SolidColorBrush source) return;
            var color = source.Color; color.A = 255;
            if (scope.Resources[key] is SolidColorBrush brush)
            {
                if (brush.Color == color) return;
                if (!brush.IsFrozen) { brush.Color = color; return; }
            }
            scope.Resources[key] = new SolidColorBrush(color);
        }
        // Keep the tooling's base palette local too: deferred application resource
        // replacement must not leave the editor margins or text on the previous theme.
        foreach (var key in new[] { "PageBrush", "PanelBrush", "TextBrush", "MutedBrush", "DisabledTextBrush",
            "HeaderBrush", "InputBrush", "AlternateBrush", "HoverBrush", "SelectionBrush", "AccentBrush",
            "AccentTextBrush", "BorderBrush", "NavigationAccentBrush", "LinkBrush" }) Copy(key, key);
        Copy("SurfaceBrush", "PanelBrush"); Copy("SurfaceAlternateBrush", "AlternateBrush"); Copy("ChromeSurfaceBrush", "PanelBrush");
        foreach (var prefix in new[] { "Input", "Selector", "Button", "Content", "List" })
        {
            Copy(prefix + "SurfaceBrush", prefix is "Input" or "Selector" ? "InputBrush" : "PanelBrush");
            foreach (var suffix in new[] { "Header", "Alternate", "Hover", "Selection", "Accent", "Border" })
                Copy(prefix + suffix + "Brush", suffix + "Brush");
        }
        if (ReferenceEquals(scope, window)) WindowFrameTheme.Apply(window);
    }
}
