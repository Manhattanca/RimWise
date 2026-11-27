#if DEBUG
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>Walks the real sandbox entry with synthetic data; never starts a game or network client.</summary>
internal static class SortingGuideSmokeCheck
{
    internal static void Attach(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try { await Run(main, output); main.ViewModel.MarkExported(); Application.Current.Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); main.ViewModel.MarkExported(); Application.Current.Shutdown(1); }
        };
    }

    private static async Task Run(MainWindow main, string output)
    {
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        main.LoadSample(); main.ViewModel.MarkExported();
        new AutomaticSortingPreferencesStore(UserStorage.DirectoryPath).AcknowledgeNotice();
        var preferenceRoot = Path.Combine(output, "guide-preferences");
        Check(SortingGuidePreferences.ShouldAsk(preferenceRoot), "新用户首次进入自动排序前会被询问是否需要教程");
        SortingGuidePreferences.MarkDeclined(preferenceRoot);
        Check(!SortingGuidePreferences.ShouldAsk(preferenceRoot), "选择不需要后不再弹出且仍可从帮助重看");
        var completedRoot = Path.Combine(output, "guide-completed");
        SortingGuidePreferences.MarkCompleted(completedRoot);
        Check(!SortingGuidePreferences.ShouldAsk(completedRoot), "已完成教程的用户不再被询问");
        var legacyRoot = Path.Combine(output, "guide-legacy-response");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllBytes(Path.Combine(legacyRoot, "sorting-guide-v1.seen"), [1]);
        Check(!SortingGuidePreferences.ShouldAsk(legacyRoot), "旧版已处理过教程提示的用户升级后不重复询问");
        var usedRoot = Path.Combine(output, "guide-used");
        SortingGuidePreferences.MarkAutoSortUsed(usedRoot);
        Check(!SortingGuidePreferences.ShouldAsk(usedRoot), "已实际运行过自动排序的用户不再被询问");
        var existingUserRoot = Path.Combine(output, "existing-user-used-sort");
        using (new AiSettingsStore(existingUserRoot).Tasks.Create(new(AiTaskKind.Sorting, AiTaskPhase.Prepared,
            new string('a', 64), ["existing.mod"], ["existing.mod"], DateTimeOffset.UtcNow))) { }
        Check(!SortingGuidePreferences.ShouldAsk(existingUserRoot), "升级用户的历史排序任务也会抑制首次教程询问");
        var panel = main.OpenAiSorting(); await Idle(main);
        Check(SortingGuidePreferences.ShouldAsk(UserStorage.DirectoryPath), "首次进入自动排序页前仍待处理教程询问");
        var originalOrder = main.ViewModel.Rows.Select(r => r.PackageId).ToArray();
        var revision = main.ViewModel.ContentRevision;
        var before = SettingsHashes(UserStorage.DirectoryPath);
        var tasks = new AiSettingsStore(UserStorage.DirectoryPath).Tasks.Inspect().Count;
        var themes = ((App)Application.Current).Themes; var appearance = themes.Current; var image = themes.Image;
        var windows = Application.Current.Windows.Count;
        var promptCount = 0;
        var promptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        promptTimer.Tick += (_, _) =>
        {
            var prompt = Application.Current.Windows.OfType<MessageDialogWindow>()
                .FirstOrDefault(window => window.Title == "自动排序教程");
            if (prompt is null) return;
            promptCount++;
            var need = ((StackPanel)prompt.FindName("ButtonsPanel")!).Children.OfType<Button>()
                .Single(button => Equals(button.Content, "需要"));
            need.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        };
        promptTimer.Start();
        main.AiSortingPageTab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle(main);
        promptTimer.Stop();
        Check(promptCount == 1 && main.SortingTutorialHost.IsVisible,
            "首次点击自动排序显示教程询问，选择需要后直接开始主界面内教程");
        Check(SortingGuidePreferences.ShouldAsk(UserStorage.DirectoryPath), "教程未完成前会保留首次询问状态");
        void Open() => main.SortingGuideMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Open(); await Idle(main);
        var guide = (SortingGuideView)main.SortingTutorialHost.Content;
        Check(main.SortingTutorialHost.IsVisible && panel.SortingWorkspace.IsVisible && Window.GetWindow(guide) == main, "真实教程按钮打开主窗口内模拟界面");
        Check(Application.Current.Windows.Count == windows, "教学不新建窗口");
        Check(guide.DataContext is null, "模拟场景不继承真实编辑器数据上下文");
        Check(Math.Abs(guide.ActualWidth - main.SortingTutorialHost.ActualWidth) < 1, "模拟教学填满主页面");
        try
        {
            foreach (var small in new[] { false, true })
            {
                main.Width = small ? main.MinWidth : 1120; main.Height = small ? main.MinHeight : 800;
                guide.Restart(); await Idle(main);
                for (var i = 0; i < SortingGuideView.StepCount; i++)
                {
                    var label = $"{(small ? "小窗口" : "标准窗口")}第{i + 1}步";
                    Check(guide.StepIndex == i && guide.Counter.Text == $"第 {i + 1} / {SortingGuideView.StepCount} 步", label + "编号正确");
                    Check(guide.Heading.Text.Length > 2 && guide.Description.Text.Length > 30, label + "有中文说明");
                    var card = guide.CardBounds;
                    var key = guide.TargetKey;
                    if (i == 0) Check(Equals(guide.Continue.Content, "开始教程") && guide.Description.Text.Contains("不会连接 AI")
                        && guide.Description.Text.Contains("更改你的实际排序"), label + "欢迎页说明这是不会改动真实数据的演示");
                    Check(guide.Referral.IsVisible == (key == "endpoint") && guide.Referral.Inlines.OfType<System.Windows.Documents.Hyperlink>().Single().Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text.Contains("RelayRax")), label + "推广链接只在地址步骤显示");
                    if (key == "export") Check(guide.Description.Text.Contains("ZIP 和问题复现步骤发给作者"), label + "说明将排障材料发给作者");
                    if (key == "log") Check(Descendants(guide).OfType<TextBlock>().Any(t => t.IsVisible && t.Name == "StatusText" && t.Text == GameTestStatus.Describe(new(123, 18, 49152) { Phase = "main-menu", StartupReady = true })), label + "使用真实测试进度格式");
                    if (key == "response") Check(Descendants(guide).OfType<TextBox>().Any(t => t.IsVisible && t.Name == "ResponseText" && t.Text.Contains(AiSortingWindow.FormatRound(new(1, "正在测试第 1 次排序", 0)).Trim())), label + "显示按轮次排列的实际回报格式");
                    if (key == "model") Check(guide.Heading.Text == "选择 AI 模型" && guide.Description.Text.Contains("轮数") && guide.Description.Text.Contains("Token") && guide.Description.Text.Contains("gpt-6.1-sol"), label + "说明模型影响与演示选择");
                    Check(card.Width > 200 && card.Height > 80 && card.X >= 0 && card.Y >= 0
                        && card.Right <= guide.StageSize.Width + 1 && card.Bottom <= guide.StageSize.Height + 1, label + "说明卡不越界");
                    Check(Application.Current.Windows.Count == windows, label + "没有二级窗口");
                    if (key is "confirm-removal" or "attempt-limit" or "token-limit") Check(guide.ReadAnywhere && guide.ActiveTarget is null, label + "设置仅讲解，不操作");
                    if (guide.ReadAnywhere)
                    {
                        Check(guide.Description.Text.Contains("点击任意位置") && !guide.TargetBounds.IsEmpty && !card.IntersectsWith(guide.TargetBounds), label + "说明卡避开演示区域");
                        if (key is "insert" or "full") Check(Descendants(guide).OfType<ComboBoxItem>().Any(r => r.IsVisible && r.IsSelected && r.Parent is Border && Equals(r.Content, key == "insert" ? "尽量保留现有排序，为新增 Mod 寻找位置" : "AI 全部重排序")), label + "讲解的选项已展开并框选");
                        ReadClick(guide); ReadClick(guide); Check(guide.StepIndex == i + 1, label + "任意位置连点不跳步");
                    }
                    else if (key == "decision")
                    {
                        var button = guide.DecisionButton(!small);
                        FrameworkElement page = button;
                        while (VisualTreeHelper.GetParent(page) is FrameworkElement parent && parent is not Canvas) page = parent;
                        Check(guide.CardBounds.Left >= Canvas.GetLeft(page) + page.ActualWidth, label + "讲解卡位于结果窗口旁，不会被盖住");
                        Check(guide.Description.IsVisible && guide.Description.Text.Contains("装备扩展") && Descendants(guide).OfType<TextBlock>().Any(t => t.IsVisible && t.Name == "StatusText" && t.Text == TrialExclusionWindow.DescribeRemovalTest(false)), label + "确认窗口保留真实状态，教程使用独立说明卡");
                        foreach (var choice in new[] { guide.DecisionButton(false), guide.DecisionButton(true) })
                            Check(IsWithin(guide.InputHitTest(choice.TranslatePoint(new Point(choice.ActualWidth / 2, choice.ActualHeight / 2), guide)) as DependencyObject, choice), label + "说明不遮挡处理按钮");
                        Check(button.IsVisible && Equals(button.Content, small ? "不做处理" : "移除模组"), label + "使用真实试排除选项");
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(Descendants(guide).OfType<TextBlock>().Any(t => t.IsVisible && t.Name == "StatusText" && t.Text.StartsWith(small ? "已保留模组" : "已排序完成")), label + "模拟结果保留所选分支");
                    }
                    else if (guide.ActiveTarget is { } target)
                    {
                        Check(guide.Continue.Visibility == Visibility.Collapsed, label + "需点击指定目标而非下一页");
                        if (key is "pick-file" or "open-file")
                        {
                            var shot = Descendants(guide).OfType<Image>().Single(t => t.Name == "SelectedImage");
                            Check(shot.Source is BitmapSource { PixelWidth: 1016, PixelHeight: 798 } && shot.IsVisible == (key == "open-file"), label + "窗口内显示裁掉黑边的模拟文件选择状态");
                            var area = Descendants(guide).OfType<Border>().Single(t => t.Name == "CoachArea");
                            void CheckPlacement()
                            {
                                var stage = (Visual)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(target));
                                var bounds = area.TransformToAncestor(stage).TransformBounds(new Rect(area.RenderSize));
                                bounds.Inflate(0.1, 0.1);
                                Check(bounds.Contains(guide.CardBounds) && !guide.CardBounds.IntersectsWith(guide.TargetBounds), label + "说明完整位于选择器留白内且不挡操作");
                            }
                            CheckPlacement();
                            if (!small)
                            {
                                var width = main.Width; main.Width = 1440; await Idle(main); CheckPlacement();
                                main.Width = width; await Idle(main); CheckPlacement();
                            }
                        }
                        if (key is "pick-file" or "open-file" or "sorting-tab") Check(Descendants(guide).OfType<DataGrid>().Single(g => g.Name == "OrderGrid").Items.Count == (key == "sorting-tab" ? 5 : 4), label + "只在打开文件后更新演示列表");
                        var hole = guide.TargetBounds;
                        Check(!hole.IsEmpty && hole.Width > 10 && hole.Height > 10, label + "高亮有有效位置");
                        Check(hole.Left >= 0 && hole.Top >= 0 && hole.Right <= guide.StageSize.Width + 1
                            && hole.Bottom <= guide.StageSize.Height + 1, label + "高亮目标完整可见");
                        Check(!hole.IntersectsWith(card), label + "说明卡不遮挡目标");
                        var point = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), guide);
                        Check(IsWithin(guide.InputHitTest(point) as DependencyObject, target), label + "真实命中高亮目标而非遮罩");
                        // A late invocation of a non-target or old control cannot advance the state machine.
                        var decoy = Descendants(guide).OfType<Button>().First(b => !ReferenceEquals(b, target)
                            && !ReferenceEquals(b, guide.Continue) && !ReferenceEquals(b, guide.Previous) && !ReferenceEquals(b, guide.Exit));
                        decoy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(guide.StepIndex == i, label + "错误位置无动作");
                        target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (guide.OpenList is { } list)
                        {
                            const int expected = 0;
                            Check(list.IsDropDownOpen && guide.StepIndex == i, label + "下拉展开且不提前推进");
                            if (list.Items.Count > 1) {
                                list.SelectedIndex = 1 - expected; list.IsDropDownOpen = false; await Idle(main);
                                Check(guide.StepIndex == i, label + "选错服务或模式不推进");
                                target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            list.SelectedIndex = expected; list.IsDropDownOpen = false; await Idle(main);
                        }
                        target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(i == SortingGuideView.StepCount - 1 ? !guide.IsVisible : guide.StepIndex == i + 1, label + "指定点击推进或结束且迟到重复点击无效");
                    }
                    else
                    {
                        Check(guide.Continue.IsVisible && guide.Continue.Content is string { Length: > 0 }, label + "说明阶段可继续");
                        guide.Continue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    await Idle(main);
                    if (!small && i < SortingGuideView.StepCount - 1)
                        Capture(main, Path.Combine(output, $"simulation-step-{i + 2:00}.png"));
                }
                Check(main.SortingTutorialHost.Visibility == Visibility.Collapsed && panel.SortingWorkspace.IsVisible, "完成教学返回原始排序页面");
                Open(); await Idle(main);
                Check(ReferenceEquals(guide, main.SortingTutorialHost.Content) && guide.StepIndex == 0, "重新打开复用实例且回到开头");
            }
            guide.Continue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(main);
            guide.Previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(guide.StepIndex == 0 && !guide.Previous.IsEnabled, "可返回上一步且初始上一步禁用");
            main.LogsPageTab.IsChecked = true; await Idle(main); main.AiSortingPageTab.IsChecked = true; await Idle(main);
            Check(main.SortingTutorialHost.IsVisible && guide.StepIndex == 0, "切换页面返回后仍保持教学");
            themes.Preview(appearance with { PanelOpacity = 0, OtherUiOpacity = 0 }, image); await Idle(main);
            Check(AppearancePage.GetIsTooling(guide) && ((SolidColorBrush)guide.FindResource("PageBrush")).Color.A == 255
                && ((SolidColorBrush)guide.FindResource("ButtonSurfaceBrush")).Color.A == 255, "透明主题不会让教学自身不可操作");
            Capture(main, Path.Combine(output, "simulation-transparent-theme.png"));
            themes.Preview(appearance with { Preset = AppearancePreset.Light, PanelOpacity = 1, OtherUiOpacity = 1 }, null); await Idle(main);
            guide.Continue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(main);
            Check(guide.ActiveTarget is not null && !guide.TargetBounds.IntersectsWith(guide.CardBounds), "浅色主题同样高亮且不遮挡模拟目标");
            Capture(main, Path.Combine(output, "simulation-light-theme.png"));
            Escape(guide); Check(!main.SortingTutorialHost.IsVisible && panel.SortingWorkspace.IsVisible, "Esc随时退出且不退出主窗口");
            Open(); await Idle(main);
            guide.Exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(!guide.IsVisible, "退出教学按钮即时退出");
        }
        finally { themes.Preview(appearance, image); }
        await Idle(main); // Fluent replaces the window template on light/dark changes.
        Check(!SortingGuidePreferences.ShouldAsk(UserStorage.DirectoryPath), "完整走完教程后不再询问且永久入口保留");
        Check(main.SortingGuideMenu.IsEnabled, "退出后可重新学习");
        Check(!panel.IsWorking && main.ViewModel.ContentRevision == revision && originalOrder.SequenceEqual(main.ViewModel.Rows.Select(r => r.PackageId)), "模拟导入测试保存全程不改真实顺序或执行任务");
        Check(before.SequenceEqual(SettingsHashes(UserStorage.DirectoryPath)), "模拟密钥模型连接和保存不改真实配置");
        Check(tasks == new AiSettingsStore(UserStorage.DirectoryPath).Tasks.Inspect().Count, "没有创建AI付费任务");
        var client = new Client();
        var seenPanel = new AiSortingWindow(main.ViewModel, UserStorage.DirectoryPath, client: client, secrets: new Secrets());
        Check(SortingGuidePreferences.WasCompleted(UserStorage.DirectoryPath), "重建自动排序页仍保留已完成状态"); seenPanel.Close();
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    private static bool IsWithin(DependencyObject? value, DependencyObject ancestor)
    {
        while (value is not null) { if (ReferenceEquals(value, ancestor)) return true; value = value is Visual ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value); }
        return false;
    }
    private static void Escape(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, Key.Escape)
    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static void ReadClick(UIElement target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
    private static string[] SettingsHashes(string root) => Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(f => Path.GetRelativePath(root, f) + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).ToArray();
    private static async Task Idle(FrameworkElement element)
    { await element.Dispatcher.InvokeAsync(() => element.UpdateLayout(), DispatcherPriority.ApplicationIdle); await Task.Delay(530); element.UpdateLayout(); }
    private static void Capture(MainWindow window, string path)
    {
        var element = (FrameworkElement)window.Content; var size = element.RenderSize;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 1.5), (int)Math.Ceiling(size.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class Secrets : ISecretStore
    { public string? Read(string name) => null; public void Write(string name, string secret) => throw new InvalidOperationException(); public void Delete(string name) => throw new InvalidOperationException(); }
    private sealed class Client : IAiClient
    {
        internal int Calls;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) { Calls++; throw new InvalidOperationException("禁止网络请求"); }
        public Task<AiClientResult> ProbeModelAsync(AiRequest request, CancellationToken token) { Calls++; throw new InvalidOperationException("禁止模型校验"); }
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token) { Calls++; throw new InvalidOperationException("禁止付费请求"); }
    }
}
#endif
