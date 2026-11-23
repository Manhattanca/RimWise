using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Views;

namespace RimWise.Desktop.Services;

/// <summary>Temporarily intercepts editor input. A selection never invokes the selected business action.</summary>
internal sealed class AppearanceControlPicker : IDisposable
{
    private readonly MainWindow main;
    private readonly AppearanceWindow editor;
    private readonly AppearanceControlRegistry registry;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AppearanceTarget? pressed;
    private Highlight? highlight;
    private AdornerLayer? layer;
    private bool disposed, opening;
    internal Task Completion => completion.Task;
    internal bool HasHighlight => highlight is not null;
    internal void PageChanged()
    {
        pressed = null; HighlightTarget(null); registry.Refresh();
        main.OpacityEditingTitle.Text = "正在调整" + (main.MarketplaceHost.IsVisible ? "排序市场" : main.GameLogHost.IsVisible ? "游戏日志"
            : main.AiSortingHost.IsVisible ? "自动排序" : "模组排序") + "透明度";
    }
    internal AppearanceControlPicker(MainWindow main, AppearanceWindow editor, AppearanceControlRegistry registry)
    {
        this.main = main; this.editor = editor; this.registry = registry;
        registry.Refresh();
        main.PreviewMouseDown += Down; main.PreviewMouseUp += Up; main.PreviewMouseMove += Move;
        main.PreviewKeyDown += KeyDown; main.PreviewTextInput += TextInput;
        main.PreviewDragOver += Drag; main.PreviewDrop += Drag;
        main.ContextMenuOpening += Context; CommandManager.AddPreviewExecutedHandler(main, Executing);
        main.OpacityEditingBar.Visibility = Visibility.Visible;
        PageChanged();
        main.OpacityEditingDone.Click += Done; main.OpacityEditTitle.Click += Title;
        main.OpacityEditingBar.Focus();
    }
    private bool IsUtility(DependencyObject? source)
    {
        for (var node = source; node is not null && node != main; node = Parent(node))
            if (node == main.OpacityEditingBar || node == main.MainPageTabs || node == main.CaptionButtons) return true;
        return false;
    }
    private static DependencyObject? Parent(DependencyObject node) => node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    private AppearanceTarget? At(Point point)
    {
        AppearanceTarget? target = null;
        // Geometry-only hit testing includes collapsed pages and non-input overlays.
        // Mirror WPF input visibility, but keep disabled business buttons editable.
        VisualTreeHelper.HitTest(main,
            node => node is UIElement element && (!element.IsVisible || !element.IsHitTestVisible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            hit => { target = registry.Resolve(hit.VisualHit); return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(point));
        return target;
    }
    internal AppearanceTarget? TargetAt(Point point) => At(point);
    private bool NavigationEdit(MouseButtonEventArgs e) => e.ChangedButton == MouseButton.Right
        && At(e.GetPosition(main)) is { } target && registry.IsNavigationButton(target.Element);
    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (!NavigationEdit(e) && IsUtility(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        pressed = e.ChangedButton == MouseButton.Left || NavigationEdit(e) ? registry.Resolve(e.OriginalSource as DependencyObject) ?? At(e.GetPosition(main)) : null;
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!NavigationEdit(e) && IsUtility(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        var target = pressed; pressed = null;
        if ((e.ChangedButton == MouseButton.Left || NavigationEdit(e)) && target is not null
            && target == (registry.Resolve(e.OriginalSource as DependencyObject) ?? At(e.GetPosition(main))))
            main.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (!disposed) Inspect(target); }));
    }
    private void Move(object sender, MouseEventArgs e)
    {
        var target = IsUtility(e.OriginalSource as DependencyObject) ? null : At(e.GetPosition(main));
        HighlightTarget(target);
    }
    private void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Dispose(); return; }
        if ((e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            && registry.Resolve(Keyboard.FocusedElement as DependencyObject) is { } navigation
            && registry.IsNavigationButton(navigation.Element))
        { e.Handled = true; Inspect(navigation); return; }
        if (IsUtility(e.OriginalSource as DependencyObject)) return;
        if (e.Key == Key.Tab) return; // Keyboard focus remains visible and can reach the completion button.
        e.Handled = true;
        if (e.Key is Key.Enter or Key.Space && registry.Resolve(Keyboard.FocusedElement as DependencyObject) is { } target)
            Inspect(target);
    }
    private void TextInput(object sender, TextCompositionEventArgs e) => e.Handled = !IsUtility(e.OriginalSource as DependencyObject);
    private void Drag(object sender, DragEventArgs e) { e.Handled = true; e.Effects = DragDropEffects.None; }
    private void Context(object sender, ContextMenuEventArgs e) => e.Handled = true;
    private void Executing(object sender, ExecutedRoutedEventArgs e) { if (!IsUtility(e.OriginalSource as DependencyObject)) e.Handled = true; }
    private void Done(object sender, RoutedEventArgs e) => Dispose();
    private void Title(object sender, RoutedEventArgs e) { if (registry.Find("MainWindow.FileMenu") is { } target) Inspect(target); }
    internal void Inspect(AppearanceTarget target)
    {
        if (disposed || opening || !target.Element.IsLoaded || !target.Element.IsVisible || !target.Element.IsHitTestVisible
            || registry.Find(target.Key) != target) return;
        opening = true; HighlightTarget(target);
        var themes = ((App)Application.Current).Themes;
        var original = themes.Current.Controls.Get(target.Key) ?? new() { Opacity = themes.Current.SurfaceOpacity(target.Surface) };
        var committed = false;
        try
        {
            var dialog = new ControlOpacityWindow(main, target.Label, original, rule => editor.SetControlOpacity(target.Key, rule));
            committed = dialog.ShowDialog() == true;
        }
        finally
        {
            if (!committed) editor.SetControlOpacity(target.Key, original);
            opening = false; HighlightTarget(null);
        }
    }
    private void HighlightTarget(AppearanceTarget? target)
    {
        if (highlight?.AdornedElement == target?.Element) return;
        if (highlight is not null) layer?.Remove(highlight);
        highlight = null; layer = null;
        main.OpacityEditingLabel.Text = target is null ? "点击控件调整；左键页签切页，右键页签调整。Esc 返回外观设置。" : "正在选择：" + target.Label + "（不会执行原功能）";
        if (target is null) return;
        layer = AdornerLayer.GetAdornerLayer(target.Element);
        if (layer is not null) { highlight = new Highlight(target.Element); layer.Add(highlight); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        main.PreviewMouseDown -= Down; main.PreviewMouseUp -= Up; main.PreviewMouseMove -= Move;
        main.PreviewKeyDown -= KeyDown; main.PreviewTextInput -= TextInput;
        main.PreviewDragOver -= Drag; main.PreviewDrop -= Drag; main.ContextMenuOpening -= Context;
        CommandManager.RemovePreviewExecutedHandler(main, Executing);
        main.OpacityEditingDone.Click -= Done; main.OpacityEditTitle.Click -= Title;
        HighlightTarget(null); main.OpacityEditingBar.Visibility = Visibility.Collapsed;
        completion.TrySetResult();
    }
    private sealed class Highlight : Adorner
    {
        internal Highlight(UIElement element) : base(element) { IsHitTestVisible = false; }
        // Never put an input surface above the selected real control.
        protected override void OnRender(DrawingContext context)
        {
            context.DrawRoundedRectangle(null, new Pen((Brush)FindResource("AccentBrush"), 2), new Rect(AdornedElement.RenderSize), 4, 4);
        }
    }
}
