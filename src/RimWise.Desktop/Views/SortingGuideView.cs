using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RimWise.Desktop.Services;

namespace RimWise.Desktop.Views;

/// <summary>A sandbox, not a proxy to the real UI. No editor, client or execution callbacks.</summary>
internal sealed partial class SortingGuideView : UserControl
{
    private enum Scene { Order, Sorting, Settings, Progress, Analysis, Result }
    private sealed record Step(Scene Scene, string? Target, string Title, string Body, string? Continue = null);
    private static readonly Step[] Steps =
    [
        new(Scene.Order, null, "自动排序", "这份教程会演示导入排序、设置 AI、选择排序方式，以及查看测试结果。内容都是示例，不会连接 AI、启动游戏或更改你的实际排序。", "开始教程"),
        new(Scene.Order, "import", "导入排序文件", "要使用别人分享的排序，或读取以前保存的排序文件，请点“导入排序”。\n\n如果要整理当前正在使用的排序，可以跳过导入，直接进入自动排序。"),
        new(Scene.Order, "pick-file", "选择排序文件", "在文件选择框中选中“示例排序.xml”。实际使用时，请选择你要导入的排序文件。"),
        new(Scene.Order, "open-file", "确认并导入", "确认文件名后，点右下角“打开”。排序会载入列表；本教程只展示模拟结果。"),
        new(Scene.Order, "sorting-tab", "进入自动排序", "自动排序处理的是“模组排序”页面中已启用的模组。点上方“自动排序”继续。"),
        new(Scene.Sorting, "settings", "设置 AI 服务", "开始排序前，需要配置服务商、API 地址、密钥和模型。点“AI 设置…”打开设置。"),
        new(Scene.Settings, "provider", "选择服务商", "本教程用中转服务演示：打开“服务”，选择“自定义 / 中转服务”。使用中转站提供的密钥时，即使模型是 GPT，也应选择中转服务。\n\n直接使用官方 API 的话，请选择对应服务商；软件会按服务商补全地址。"),
        new(Scene.Settings, "endpoint", "填写 API 地址", "点地址框查看示例地址：https://relayrax.com/v1。实际使用时，填写所选服务商提供的 API 地址。教程不需要真实密钥。"),
        new(Scene.Settings, "key", "填写 API 密钥", "点密钥框会填入教程用的示例内容。实际排序需要换成有效密钥；请勿把密钥发给他人。示例不会读取本机密钥或连接网络。"),
        new(Scene.Settings, "fetch", "获取模型列表", "点“获取模型”。实际使用时，软件会向已配置的服务查询可用模型；本教程只显示一条示例，不会发送请求。"),
        new(Scene.Settings, "model", "选择 AI 模型", "模型会影响排序效果、测试轮数和 Token 用量。展开列表，选择本教程使用的 gpt-6.1-sol。这里不会调用模型或产生用量。"),
        new(Scene.Settings, "connection", "测试连接", "点“测试 AI 连接”。教程会显示模拟的成功结果，不会发起连接。实际使用时，可在结果提示中查看连接是否成功。"),
        new(Scene.Settings, "connection-result", "查看测试结果", "连接结果显示在按钮右侧。真实测试失败时，这里会显示原因；根据提示检查服务商、地址和密钥后再试。"),
        new(Scene.Settings, "save", "保存 AI 设置", "点“保存”结束设置演示。实际使用时，这会保存你填写的服务配置；本教程不会改动现有设置。"),
        new(Scene.Sorting, "insert", "为新模组找位置", "适合现有排序基本可用、只新增了模组的情况。软件会尽量保留旧模组的相对顺序，为新模组安排位置。"),
        new(Scene.Sorting, "full", "重新排序", "需要调整整份列表时选这个模式。AI 可以重新安排已有模组之间的顺序。本教程接下来演示此模式。"),
        new(Scene.Sorting, "confirm-removal", "移除前征求确认", "启用后，如果测试发现移除某个模组后问题消失，软件会先询问你，再保存结果。移除只会将模组从启用列表中移出，不会删除文件。"),
        new(Scene.Sorting, "attempt-limit", "测试轮数上限", "限制一次排序最多启动多少轮游戏测试。达到上限后任务停止；这不代表所有报错都已解决。"),
        new(Scene.Sorting, "token-limit", "Token 用量上限", "为本次任务设置 Token 上限，避免用量超过预期。它限制的是 Token 数量，不是费用；任务结束后仍会显示实际用量。"),
        new(Scene.Sorting, "start", "开始排序和测试", "点“自动排序并测试”后，软件会调用 AI 并启动游戏进行测试。开始前先保存游戏进度。本教程只切换到模拟进度，不会执行排序。"),
        new(Scene.Progress, "log", "查看测试进度", "第一轮会按当前顺序启动游戏，先检查现有问题。这里显示测试阶段、等待时间和日志状态；发现报错后，再看分析结果。"),
        new(Scene.Analysis, "response", "查看分析和调整记录", "这里按轮次显示测试结果和 AI 建议。示例中，调整顺序后一个报错消失；“装备扩展”仍有问题，移除后再次测试通过。"),
        new(Scene.Analysis, "decision", "选择如何处理模组", "示例测试显示，移除“装备扩展”后可以通过。要从启用列表中移出它，点“移除模组”；要保留它，点“不做处理”。两种选择都会保存已完成的排序调整。"),
        new(Scene.Result, "export", "导出排障日志", "遇到持续报错、卡住或排序失败时，可以点“导出完整排障日志”，再把生成的 ZIP 和问题复现步骤发给作者。本教程不会真的导出文件。"),
    ];

    internal int StepIndex { get; private set; }
    internal bool IsCompleted { get; private set; }
    internal string? TargetKey => Steps[StepIndex].Target;
    internal static int StepCount => Steps.Length;
    internal Button? ActiveTarget { get; private set; }
    internal Button Previous { get; } = new() { Content = "上一步", MinWidth = 78 };
    internal Button Exit { get; } = new() { Content = "结束教程", MinWidth = 86 };
    internal Button Continue { get; } = new() { MinWidth = 110 };
    internal TextBlock Counter { get; } = Text("", 13);
    internal TextBlock Heading { get; } = Text("", 20, true);
    internal TextBlock Description { get; } = Text("", 14);
    internal Rect TargetBounds { get; private set; }
    internal Rect CardBounds { get; private set; }
    internal Size StageSize => stage.RenderSize;
    internal event EventHandler? Finished;
    private readonly Grid stage = new() { ClipToBounds = true };
    private readonly Grid scene = new();
    private readonly Canvas foreground = new() { Background = Brushes.Transparent };
    private readonly SpotlightShade shade = new();
    private readonly Border ring = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(7), IsHitTestVisible = false };
    private readonly TextBlock pointer = Text("▼ 点这里", 13, true);
    private readonly Border card;
    private readonly ScaleTransform coachScale = new();
    private FrameworkElement? anchor;
    private readonly TextBlock referral = Text("", 13);
    internal TextBlock Referral => referral;
    private bool chromeAttached;
    private bool positioning;
    private long enteredAt;
    private bool Ready => System.Diagnostics.Stopwatch.GetElapsedTime(enteredAt).TotalMilliseconds >= 500;
    private bool ModeLesson => Steps[StepIndex].Target is "insert" or "full";
    internal bool ReadAnywhere => ModeLesson || Steps[StepIndex].Target is "log" or "response" or "connection-result" or "confirm-removal" or "attempt-limit" or "token-limit";

    internal SortingGuideView()
    {
        DataContext = null; // Do not inherit the real editor into a practice scene.
        AppearancePage.SetIsTooling(this, true);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Focusable = true;
        Description.LineHeight = 23;
        Heading.Margin = new Thickness(0, 0, 0, 12);
        Counter.VerticalAlignment = VerticalAlignment.Center;
        var root = new DockPanel();
        var toolbar = new DockPanel { Margin = new Thickness(14, 10, 14, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Previous); actions.Children.Add(Exit);
        DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
        var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(Text("自动排序教程", 16, true)); Counter.Margin = new Thickness(16, 0, 0, 0); identity.Children.Add(Counter);
        toolbar.Children.Add(identity); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var safety = Text("示例操作：不会连接 AI、启动游戏或更改实际排序", 12);
        safety.Margin = new Thickness(16, 10, 16, 10); safety.SetResourceReference(ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(safety, Dock.Bottom); root.Children.Add(safety);
        var content = new StackPanel();
        content.Children.Add(Heading); content.Children.Add(Description);
        referral.Margin = new Thickness(0, 10, 0, 0);
        referral.Inlines.Add("手里没现成密钥？可以看看 ");
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("RelayRax 中转站（推广链接）")) { ToolTip = AiSettingsWindow.SuggestedServiceUrl };
        link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "LinkBrush");
        link.Click += (_, _) => OpenReferral(); referral.Inlines.Add(link); content.Children.Add(referral);
        Continue.Margin = new Thickness(0, 16, 0, 0); Continue.HorizontalAlignment = HorizontalAlignment.Right;
        Continue.SetResourceReference(BackgroundProperty, "ButtonAccentBrush");
        Continue.SetResourceReference(ForegroundProperty, "AccentTextBrush"); content.Children.Add(Continue);
        card = Surface(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, "PanelBrush", 18);
        card.BorderThickness = new Thickness(1); card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        card.RenderTransformOrigin = new Point(.5, .5); card.RenderTransform = coachScale;
        ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); pointer.SetResourceReference(ForegroundProperty, "AccentBrush");
        pointer.IsHitTestVisible = false;
        foreground.Children.Add(ring); foreground.Children.Add(pointer); foreground.Children.Add(card);
        stage.Children.Add(scene); stage.Children.Add(shade); stage.Children.Add(foreground); root.Children.Add(stage);
        Content = Surface(root, "PageBrush");
        stage.SizeChanged += (_, _) => PositionCoach();
        stage.LayoutUpdated += (_, _) => PositionCoach();
        Previous.Click += (_, _) => { if (Ready && StepIndex > 0) SetStep(StepIndex - 1); };
        Exit.Click += (_, _) => Finish();
        Continue.Click += (_, _) => { if (Steps[StepIndex].Continue is not null) Advance(); };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!ReadAnywhere || IsWithin(e.OriginalSource as DependencyObject, Previous) || IsWithin(e.OriginalSource as DependencyObject, Exit)) return;
            e.Handled = true; Advance();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Finish(); e.Handled = true; }
            else if (ReadAnywhere && e.Key is (Key.Enter or Key.Space) && !Previous.IsKeyboardFocusWithin && !Exit.IsKeyboardFocusWithin)
            { e.Handled = true; if (!e.IsRepeat) Advance(); }
        };
        // Synthetic controls only; non-target input never advances or reaches application commands.
        KeyboardNavigation.SetTabNavigation(scene, KeyboardNavigationMode.None);
        scene.Focusable = false;
        PreviewDragOver += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; };
        PreviewDrop += (_, e) => e.Handled = true;
        Loaded += (_, _) =>
        {
            if (!chromeAttached && Window.GetWindow(this) is { } owner)
            { AppearanceEditorChrome.Attach(this, owner); chromeAttached = true; }
            PositionCoach();
            if (ActiveTarget is { } target) target.Focus(); else Continue.Focus();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearFocus(); };
        Restart();
    }

    internal void Restart() { IsCompleted = false; SetStep(0); }
    private void Advance()
    {
        if (!Ready) return;
        if (StepIndex == Steps.Length - 1) Finish(completed: true); else SetStep(StepIndex + 1);
    }
    private void Finish(bool completed = false) { IsCompleted = completed; ClearFocus(); Finished?.Invoke(this, EventArgs.Empty); }
    private void ClearFocus() { if (IsKeyboardFocusWithin) Keyboard.ClearFocus(); }
    private void SetStep(int index)
    {
        StepIndex = index;
        var step = Steps[index];
        Heading.Text = step.Title; Description.Text = step.Body + (ReadAnywhere ? "\n\n读完后，点击任意位置继续。" : ""); Counter.Text = $"第 {index + 1} / {Steps.Length} 步";
        Previous.IsEnabled = index > 0;
        Continue.Content = step.Continue; Continue.Visibility = step.Continue is not null ? Visibility.Visible : Visibility.Collapsed;
        referral.Visibility = step.Target == "endpoint" ? Visibility.Visible : Visibility.Collapsed;
        if (ActiveTarget != null) foreground.Children.Remove(ActiveTarget);
        ActiveTarget = null; anchor = null;
        BuildScene(step.Scene);
        if (!ReadAnywhere && anchor is { } part)
        {
            var proxy = new Button { Opacity = 0, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, Margin = new Thickness(0), Cursor = Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(proxy, step.Title);
            ActiveTarget = proxy; foreground.Children.Insert(0, proxy);
            proxy.Click += (_, _) => { if (Ready && IsVisible && ReferenceEquals(ActiveTarget, proxy)) Activate(part, step.Target!); };
        }
        // Focus and template changes can cause a layout pass before the new scene is complete.
        positionedStep = -1;
        PositionCoach();
        if (ActiveTarget is { } target) target.Focus(); else Continue.Focus();
        enteredAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var stepIndex = StepIndex;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible || StepIndex != stepIndex) return;
            anchor?.BringIntoView();
            var pop = InterfaceMotion.Enabled ? InterfaceMotion.Motion(.90, 1, 180) : null;
            coachScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            coachScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            if (ActiveTarget is { } expected) expected.Focus(); else if (ReadAnywhere) Focus(); else if (step.Target == "decision") DecisionButton(false).Focus(); else Continue.Focus();
        }));
    }

    private static bool IsWithin(DependencyObject? value, DependencyObject? ancestor)
    {
        if (ancestor is null) return false;
        while (value is not null)
        {
            if (ReferenceEquals(value, ancestor)) return true;
            value = value is Visual ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
        }
        return false;
    }

    private static TextBlock Text(string value, double size = 14, bool bold = false) => new()
    { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static Border Surface(UIElement child, string brush, double padding = 0)
    {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(8), Padding = new Thickness(padding) };
        border.SetResourceReference(Border.BackgroundProperty, brush); return border;
    }
}
