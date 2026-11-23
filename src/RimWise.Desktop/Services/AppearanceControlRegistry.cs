using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Services;

internal sealed record AppearanceTarget(FrameworkElement Element, string Key, string Label, AppearanceSurface Surface);

/// <summary>Scopes semantic background brushes to a stable, named control. Never fades the entire control.</summary>
internal sealed class AppearanceControlRegistry : IDisposable
{
    private readonly MainWindow main;
    private readonly ThemeService themes;
    private readonly Dictionary<FrameworkElement, AppearanceTarget> targets = [];
    private readonly Dictionary<FrameworkElement, Dictionary<string, object?>> overridden = [];
    private System.Windows.Threading.DispatcherOperation? pendingRefresh;
    private bool disposed;
    internal IReadOnlyCollection<AppearanceTarget> Targets => targets.Values;
    static AppearanceControlRegistry()
    {
        // Loaded/Unloaded are direct events, so an owner handler cannot observe descendant loads.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, e) =>
            {
                if (sender is FrameworkElement element && Window.GetWindow(element) is MainWindow window)
                    window.AppearanceControls?.Register(element);
            }), handledEventsToo: true);
    }
    internal AppearanceControlRegistry(MainWindow main, ThemeService themes)
    {
        this.main = main; this.themes = themes;
        themes.Changed += Changed;
        main.Loaded += MainLoaded;
        if (main.IsLoaded) Refresh();
    }
    private void MainLoaded(object sender, RoutedEventArgs e) => Refresh();
    internal void RequestRefresh()
    {
        if (disposed || pendingRefresh is not null) return;
        pendingRefresh = main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            pendingRefresh = null; if (!disposed) Refresh();
        }));
    }
    internal void Refresh()
    {
        var visited = new HashSet<DependencyObject>();
        void Visit(DependencyObject node)
        {
            if (!visited.Add(node) || node is DataGridRow or DataGridCell or ListBoxItem or ComboBoxItem) return;
            if (node is FrameworkElement element) Register(element);
            if (node is MenuItem) return;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Visit(child);
            if (node is Visual)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(main);
    }
    private void Register(FrameworkElement element)
    {
        if (element.Name.Length == 0 || element.TemplatedParent is not null || Window.GetWindow(element) != main
            || (AppearancePage.GetIsTooling(element) && !IsNavigationButton(element))) return;
        var surface = Surface(element); if (surface is null) return;
        string scope = nameof(MainWindow);
        for (DependencyObject? parent = element; parent is not null && parent != main; parent = Parent(parent))
        {
            // Template parts and item containers have no persistent instance identity.
            if (parent is DataGridRow or DataGridCell or ListBoxItem or ComboBoxItem
                || parent is MenuItem && !IsTopMenu(element)) return;
            if (parent is UserControl page) scope = page.GetType().Name;
        }
        scope = AppearancePage.GetScope(element) ?? scope;
        var key = scope + "." + element.Name;
        if (!AppearanceControlOverrides.IsKey(key) || targets.Values.Any(target => target.Key == key && target.Element != element)) return;
        var label = System.Windows.Automation.AutomationProperties.GetName(element);
        if (label.Length == 0) label = element switch
        {
            ContentControl { Content: string content } => content,
            MenuItem { Header: string header } => header + "菜单按键",
            _ when element == main.SearchBox => "模组搜索框",
            _ when element == main.OrderGrid => "当前排序列表",
            _ when element == main.AvailableGrid => "未启用模组列表",
            _ when element == main.MainMenu => "菜单栏",
            _ when element == main.ModEditorFooter => "底栏",
            _ when element == main.AppCaption => "窗口标题栏",
            ListBox => "列表",
            _ => surface switch { AppearanceSurface.Inputs => "搜索 / 输入框", AppearanceSurface.Selectors => "选择框",
                AppearanceSurface.Panels => "详情面板", AppearanceSurface.Lists => "列表", AppearanceSurface.Content => "内容区", _ => "操作按钮" }
        };
        var target = new AppearanceTarget(element, key, label.Replace('_', ' ').Trim(), surface.Value);
        if (!targets.ContainsKey(element)) element.AddHandler(FrameworkElement.UnloadedEvent, new RoutedEventHandler(Unloaded), true);
        targets[element] = target; Apply(target);
    }
    private AppearanceSurface? Surface(FrameworkElement element) => element switch
    {
        _ when element == main.MainMenu || element == main.AppCaption => null,
        _ when IsTopButton(element) => AppearanceSurface.Buttons,
        _ when element == main.ModEditorFooter => AppearanceSurface.Bars,
        DataGrid => AppearanceSurface.Lists,
        // These templates and their rows use Content* brushes, unlike the mod DataGrids.
        ListBox => AppearanceSurface.Content,
        TextBox { IsReadOnly: true } => AppearanceSurface.Content,
        TextBox or PasswordBox => AppearanceSurface.Inputs,
        Selector or ToggleButton => AppearanceSurface.Selectors,
        Button => AppearanceSurface.Buttons,
        Border { Background: not null } => AppearanceSurface.Panels,
        _ => null
    };
    private bool IsTopMenu(FrameworkElement element) => element is MenuItem item && main.MainMenu.Items.Contains(item);
    internal bool IsNavigationButton(FrameworkElement element) => element == main.ModsPageTab || element == main.AiSortingPageTab
        || element == main.LogsPageTab || element == main.MarketPageTab;
    private bool IsTopButton(FrameworkElement element) => IsTopMenu(element) || IsNavigationButton(element);
    private void Unloaded(object sender, RoutedEventArgs e)
    {
        // Window.GetWindow can already be null after detaching dynamic content.
        if (sender is FrameworkElement element && targets.Remove(element, out var target))
        {
            Clear(target); element.RemoveHandler(FrameworkElement.UnloadedEvent, new RoutedEventHandler(Unloaded));
        }
    }
    private void Changed(object? sender, EventArgs e) { foreach (var target in targets.Values) Apply(target); }
    internal AppearanceTarget? Resolve(DependencyObject? source)
    {
        for (var node = source; node is not null && node != main; node = Parent(node))
        {
            if (node is UIElement ui && (!ui.IsVisible || !ui.IsHitTestVisible)) return null;
            if (node is FrameworkElement element && targets.TryGetValue(element, out var target)) return target;
        }
        return null;
    }
    internal AppearanceTarget? Find(string key) => targets.Values.FirstOrDefault(target => target.Key == key);
    private static DependencyObject? Parent(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    private static string[] Keys(AppearanceSurface surface) => surface switch
    {
        AppearanceSurface.Bars => ["ChromeSurfaceBrush"],
        AppearanceSurface.Panels => ["SurfaceBrush", "SurfaceAlternateBrush", "ContentHeaderBrush", "ContentSurfaceBrush"],
        _ => Enumerable.Range(0, 7).Select(i => (surface switch { AppearanceSurface.Inputs => "Input", AppearanceSurface.Selectors => "Selector",
            AppearanceSurface.Lists => "List", AppearanceSurface.Buttons => "Button", _ => "Content" })
                + new[] { "SurfaceBrush", "HeaderBrush", "AlternateBrush", "HoverBrush", "SelectionBrush", "AccentBrush", "BorderBrush" }[i]).ToArray()
    };
    private void Apply(AppearanceTarget target)
    {
        // Retired container keys are inert; they must not become hidden inherited
        // button rules that cannot be cleared by choosing Follow in the inspector.
        var rule = themes.Current.Controls.Get(target.Key);
        if (rule is not { Mode: not SurfaceOpacityMode.Follow }) { Clear(target); return; }
        var keys = IsTopButton(target.Element) ? Keys(target.Surface).Append("TopButtonSurfaceBrush").ToArray() : Keys(target.Surface);
        if (!overridden.TryGetValue(target.Element, out var previous))
        {
            previous = []; overridden.Add(target.Element, previous);
            foreach (var key in keys)
                previous[key] = target.Element.Resources.Contains(key) ? target.Element.Resources[key] : null;
        }
        foreach (var key in keys)
        {
            if (Application.Current.TryFindResource(key == "TopButtonSurfaceBrush" ? "ButtonSurfaceBrush" : key) is not SolidColorBrush source) continue;
            var color = source.Color;
            color.A = (byte)Math.Round((SystemParameters.HighContrast || rule.Mode == SurfaceOpacityMode.Opaque ? 1 : rule.Opacity) * 255);
            if (target.Element.Resources.Contains(key) && target.Element.Resources[key] is SolidColorBrush { IsFrozen: false } brush && !ReferenceEquals(brush, previous[key])) brush.Color = color;
            else target.Element.Resources[key] = new SolidColorBrush(color);
        }
    }
    private void Clear(AppearanceTarget target)
    {
        if (!overridden.Remove(target.Element, out var previous)) return;
        foreach (var (key, value) in previous)
            if (value is null) target.Element.Resources.Remove(key);
            else target.Element.Resources[key] = value;
    }
    public void Dispose()
    {
        disposed = true; pendingRefresh?.Abort(); pendingRefresh = null;
        themes.Changed -= Changed; main.Loaded -= MainLoaded;
        foreach (var target in targets.Values)
        {
            Clear(target); target.Element.RemoveHandler(FrameworkElement.UnloadedEvent, new RoutedEventHandler(Unloaded));
        }
        targets.Clear();
    }
}
