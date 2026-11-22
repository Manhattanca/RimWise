#if DEBUG
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class MainPanelWorkflowSmokeCheck
{
    internal static void Attach(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try { await Run(main, output); Application.Current.Shutdown(0); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                main.ViewModel.MarkExported(); Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task Run(MainWindow main, string output)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks.Add(message);
            File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(checks));
        }
        await CheckNavigationFocus(main, output, Check);
        foreach (var (address, expected) in new[]
        {
            ("https://synthetic.invalid", "https://synthetic.invalid/v1/chat/completions"),
            ("https://synthetic.invalid/", "https://synthetic.invalid/v1/chat/completions"),
            ("https://synthetic.invalid/v1/", "https://synthetic.invalid/v1/chat/completions"),
            ("https://synthetic.invalid/proxy/v1", "https://synthetic.invalid/proxy/v1/chat/completions"),
            ("https://synthetic.invalid/api/", "https://synthetic.invalid/api/"),
            ("https://synthetic.invalid/compatible-mode/", "https://synthetic.invalid/compatible-mode/"),
            ("https://synthetic.invalid/v1/chat/completions", "https://synthetic.invalid/v1/chat/completions"),
            ("https://synthetic.invalid/custom/generate", "https://synthetic.invalid/custom/generate"),
            ("https://synthetic.invalid/v1beta/openai/chat/completions", "https://synthetic.invalid/v1beta/openai/chat/completions"),
            ("http://127.0.0.1:1234/", "http://127.0.0.1:1234/v1/chat/completions")
        })
        {
            var settings = (AiSettings.Default with { Endpoint = address }).Normalize();
            Check(settings.Endpoint == expected && settings.Normalize().Endpoint == expected, "幂等补全地址：" + address);
        }
        foreach (var address in new[] { "http://remote.invalid", "https://synthetic.invalid?key=secret", "https://user:pass@synthetic.invalid" })
        {
            var rejected = false;
            try { _ = (AiSettings.Default with { Endpoint = address }).Normalize(); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "保留不安全地址拒绝校验：" + address.Split('?')[0]);
        }

        await AiConfigurationSmokeCheck.Run(main, output);
        checks.Add("AI 配置既有检查及默认模型、有效选择、手动模型、空列表保护通过");
        var addressRoot = Path.Combine(output, "地址补全");
        var settingsStore = new AiSettingsStore(addressRoot);
        var addressSecrets = new Secrets();
        var settingsWindow = new AiSettingsWindow(AiSettings.Default with { Endpoint = "https://synthetic.invalid/v1/chat/completions" },
            settingsStore, addressSecrets) { Owner = main };
        settingsWindow.Show();
        try
        {
            settingsWindow.ApiKeyBox.Password = "synthetic-entered-key";
            settingsWindow.ModelBox.Text = "manual-model";
            settingsWindow.EndpointBox.Text = "https://synthetic.invalid/";
            settingsWindow.EndpointBox.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount,
                settingsWindow.EndpointBox, settingsWindow.ModelBox) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Check(settingsWindow.EndpointBox.Text == "https://synthetic.invalid/"
                && settingsWindow.RequestAddressText.Text == "实际请求地址：https://synthetic.invalid/v1/chat/completions"
                && settingsWindow.ApiKeyBox.Password == "synthetic-entered-key" && settingsWindow.ModelBox.Text == "manual-model",
                "删后缀及失焦不写回地址，等效修改保留输入密钥和模型");
            await settingsWindow.PersistSettingsAsync();
            var reopened = new AiSettingsWindow(settingsStore.Load(), settingsStore, addressSecrets) { Owner = main };
            try
            {
                Check(reopened.EndpointBox.Text == "https://synthetic.invalid/", "保存重开不恢复已删后缀");
                reopened.CompleteBaseAddressBox.IsChecked = false;
                Check(reopened.RequestAddressText.Text == "实际请求地址：https://synthetic.invalid/", "关闭补全后实际请求不附加路径");
                reopened.ApiKeyBox.Password = "synthetic-exact-key";
                reopened.ModelBox.Text = "exact-model";
                await reopened.PersistSettingsAsync();
                Check(settingsStore.Load() is { CompleteBaseAddress: false, Endpoint: "https://synthetic.invalid/" }, "保存精确地址选项");
            }
            finally { reopened.Close(); }
        }
        finally { settingsWindow.Close(); }

        main.LoadSample(); main.ViewModel.MarkExported();
        await CheckModelFailure(main, output, Check);
        var original = main.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        new AutomaticSortingPreferencesStore(UserStorage.DirectoryPath).AcknowledgeNotice();
        main.AiSortingPageTab.IsChecked = true;
        var controller = main.OpenAiSorting();
        var view = (FrameworkElement)main.AiSortingHost.Content;
        controller.ResponseText.Text = "合成任务进展保留";
        Check(Window.GetWindow(view) == main && !controller.IsVisible
            && !main.OwnedWindows.OfType<AiSortingWindow>().Any(), "自动排序实际显示于主窗口而非独立窗口");
        Check(main.AiSortingPageTab.TranslatePoint(new Point(), main).X < main.LogsPageTab.TranslatePoint(new Point(), main).X,
            "自动排序页签位于游戏日志之前");
        main.LogsPageTab.IsChecked = true;
        main.ModsPageTab.IsChecked = true;
        main.AiSortingPageTab.IsChecked = true;
        Check(ReferenceEquals(controller, main.OpenAiSorting()) && ReferenceEquals(view, main.AiSortingHost.Content)
            && controller.ResponseText.Text == "合成任务进展保留", "切换页签保留同一任务与结果");
        Check((string)controller.SaveLastLogsButton.Content == "导出本次排障日志", "排障日志入口使用新名称");
        controller.ResponseText.Text = "";
        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(controller.ResponseEmptyText.Visibility == Visibility.Visible
            && controller.OrderEmptyText.Visibility == Visibility.Visible
            && controller.WarningsEmptyText.Visibility == Visibility.Visible,
            "空回报、空排序和空警告均有中文引导，不冒充已经完成测试");
        controller.ResponseText.Text = "合成任务进展保留";
        controller.AcceptedList.ItemsSource = new[] { "合成模组" };
        controller.RejectedList.ItemsSource = new[] { "合成警告" };
        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(controller.ResponseEmptyText.Visibility == Visibility.Collapsed
            && controller.OrderEmptyText.Visibility == Visibility.Collapsed
            && controller.WarningsEmptyText.Visibility == Visibility.Collapsed,
            "出现真实内容后空状态自动隐藏，不遮挡操作或复制");
        controller.AcceptedList.ItemsSource = null; controller.RejectedList.ItemsSource = null;

        // Exercise the real modal settings route without allowing a network or game call.
        var guided = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            var dialog = main.OwnedWindows.OfType<AiSettingsWindow>().FirstOrDefault();
            if (dialog is null) return;
            guided++; dialog.Close();
        };
        timer.Start();
        try { await controller.StartAutomaticAsync(); }
        finally { timer.Stop(); }
        Check(guided == 1 && !controller.IsWorking && original.SequenceEqual(main.ViewModel.Rows.Select(row => row.PackageId)),
            "未配置时开始排序直接打开设置，取消后不发请求、不改排序");

        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        foreach (var compact in new[] { false, true })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            main.Width = compact ? main.MinWidth : 1360;
            main.Height = compact ? main.MinHeight : 820;
            main.AiSortingPageTab.IsChecked = true;
            await Task.Delay(250);
            await main.Dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var bounds = controller.AutomaticButton.TransformToAncestor(main).TransformBounds(new Rect(controller.AutomaticButton.RenderSize));
            PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, $"{theme}-{compact}-自动排序.png"));
            Check(bounds.Top >= 0 && bounds.Bottom <= main.ActualHeight && controller.SettingsScroll.ViewportHeight >= 60,
                $"{theme}/{compact}：自动排序操作可见、设置可滚动（按钮 {bounds.Bottom:F0} / 窗口 {main.ActualHeight:F0}，设置 {controller.SettingsScroll.ViewportHeight:F0}）");
            main.ModsPageTab.IsChecked = true;
            await Task.Delay(250);
            await main.Dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Check(Equals(main.ImportOrderButton.Background, main.SaveOrderButton.Background)
                && Equals(main.ImportOrderButton.Foreground, main.SaveOrderButton.Foreground), $"{theme}/{compact}：导入与保存按钮颜色一致");
            Check(Equals(controller.AutomaticButton.Background, main.SaveOrderButton.Background)
                && Equals(controller.AutomaticButton.Foreground, main.SaveOrderButton.Foreground),
                $"{theme}/{compact}：开始排序复用主操作强调色，设置与清理保持次级");
            var actions = main.OrderActions.Children.OfType<Button>().ToArray();
            Check(Array.IndexOf(actions, main.ImportOrderButton) + 1 == Array.IndexOf(actions, main.SaveOrderButton),
                $"{theme}/{compact}：导入紧邻保存左侧");
            foreach (var button in actions)
            {
                var rect = button.TransformToAncestor(main).TransformBounds(new Rect(button.RenderSize));
                Check(rect.Left >= 0 && rect.Right <= main.ActualWidth && rect.Bottom <= main.ActualHeight,
                    $"{theme}/{compact}：底部按钮不超出窗口：{button.Content}");
            }
            PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, $"{theme}-{compact}-导入入口.png"));
        }
        Check(OrderImportDialog.Filters.Length == OrderImportDialog.Kinds.Length
            && OrderImportDialog.FallbackFilter.Split('|').Length == OrderImportDialog.Kinds.Length * 2,
            "原生与回退导入对话框共用六类筛选");
        OrderImportDialogSmokeCheck.Run(main, output);
        checks.Add("真实原生导入筛选切换、路径导航、取消验证通过");
        await CheckEmbeddedLifecycle(main, output, Check);
        await CheckUsageResponsiveness(main, output, Check);
        for (var i = 0; i < 8; i++)
        {
            main.LogsPageTab.IsChecked = true;
            main.AiSortingPageTab.IsChecked = true;
            main.ModsPageTab.IsChecked = true;
        }
        main.AiSortingPageTab.IsChecked = true;
        await Task.Delay(500);
        Check(main.AiSortingHost.Visibility == Visibility.Visible && main.GameLogHost.Visibility == Visibility.Collapsed
            && main.AiSortingHost.Opacity == 1
            && main.AiSortingHost.RenderTransform is TranslateTransform { X: 0, HasAnimatedProperties: false },
            "快速往返页签后自动排序动画归位，与游戏日志共用滑动动画且不弹出排序窗口");
        main.ViewModel.MarkExported();
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }));
    }

    private static async Task CheckNavigationFocus(MainWindow main, string output, Action<bool, string> check)
    {
        var style = (Style)main.FindResource("NavigationTab");
        foreach (var tab in new[] { main.ModsPageTab, main.AiSortingPageTab, main.LogsPageTab, main.MarketPageTab })
            check(ReferenceEquals(tab.Style, style) && tab.FocusVisualStyle is null && tab.Focusable && tab.IsTabStop,
                $"{tab.Content}：移除系统虚线焦点框，保留键盘焦点与导航");

        var tabs = new[] { "模组排序", "自动排序", "游戏日志", "排序市场（测试）" }
            .Select(label => new RadioButton { Content = label, Style = style, GroupName = "SyntheticFocus" }).ToArray();
        tabs[0].IsChecked = true;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var tab in tabs) panel.Children.Add(tab);
        var strip = new NavigationTabStrip();
        strip.Children.Add(panel);
        // Isolated component fixture: no actual page handlers, market request or game operation.
        var fixture = new Window { Owner = main, Content = strip, Width = 640, Height = 120,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            fixture.Show();
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                fixture.UpdateLayout();
                var sizes = tabs.Select(tab => tab.RenderSize).ToArray();
                for (var i = 0; i < tabs.Length; i++)
                {
                    Keyboard.Focus(tabs[i]);
                    fixture.UpdateLayout();
                    var chrome = (Border)tabs[i].Template.FindName("Tab", tabs[i]);
                    check(tabs[i].IsKeyboardFocused && tabs[i].FocusVisualStyle is null
                        && Equals(chrome.Background, tabs[i].FindResource("SelectionBrush")),
                        $"{theme}/{tabs[i].Content}：无虚线，键盘焦点使用主题背景");
                    tabs[i].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(fixture),
                        Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
                    tabs[i].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(fixture),
                        Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
                    check(tabs[i].IsChecked == true && tabs.Count(tab => tab.IsChecked == true) == 1,
                        $"{theme}/{tabs[i].Content}：空格键可选择且保持单选");
                    check(tabs.Select(tab => tab.RenderSize).SequenceEqual(sizes),
                        $"{theme}/{tabs[i].Content}：焦点与选择不改变页签尺寸");
                    await fixture.Dispatcher.InvokeAsync(fixture.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    PrototypeSmokeCheck.SavePreview(fixture, Path.Combine(output, $"{theme}-页签焦点-{i}.png"));
                }
                check(tabs[3].MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous))
                    && tabs.Take(3).Any(tab => tab.IsKeyboardFocused), $"{theme}：保留反向键盘导航");
            }
        }
        finally
        {
            fixture.Close();
            ((App)Application.Current).Themes.Select(ThemePreference.Light, save: false);
        }
    }

    private static async Task CheckModelFailure(MainWindow main, string output, Action<bool, string> check)
    {
        var root = Path.Combine(output, "模型校验", "profile");
        new AiSettingsStore(root).Save(AiSettings.Default with { Model = "synthetic-model" });
        var vm = new MainViewModel();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "<ModsConfigData><version>1.7</version><activeMods><li>fixture.mod</li></activeMods></ModsConfigData>"));
        vm.Load(new ModsConfigXmlFormat().Read(input), Path.Combine(root, "ModsConfig.xml"));
        vm.ApplyScan(new(new(null, "", ""), new GameVersion(1, 7), new ModCatalogue([]), []), observeActivity: false);
        var client = new Client();
        var pathsReads = 0;
        var original = vm.Rows.Select(row => row.PackageId).ToArray();
        var panel = new AiSortingWindow(vm, root, client: client, secrets: new Secrets(),
            gamePaths: () => { pathsReads++; throw new InvalidOperationException("未通过校验却访问游戏路径"); }) { Owner = main };
        panel.Show();
        var shown = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            if (panel.OwnedWindows.OfType<MessageDialogWindow>().FirstOrDefault() is not { } notice) return;
            shown |= notice.Title == "AI 校验失败，未开始排序" && notice.MessageText.Text.Contains("HTTP 403");
            notice.Close();
        };
        timer.Start();
        try
        {
            await panel.StartAutomaticAsync();
            check(shown && pathsReads == 0 && client.Probes == 1 && client.Calls == 0 && !panel.IsWorking
                && original.SequenceEqual(vm.Rows.Select(row => row.PackageId)) && !File.Exists(Path.Combine(root, "ModsConfig.xml")),
                "模型 403 立即中文提示，不访问游戏路径或修改排序");
            var logs = new AutomaticSortingLogStore(root);
            check(logs.LoadLastRun() is { EndedUtc: not null, Sessions.Length: 0, TaskIds.Length: 1 }, "失败仍保留本次零轮日志与用量记录");
            await panel.SaveLastLogsAsync(openFolder: false);
            using var zip = ZipFile.OpenRead(Directory.GetFiles(logs.DefaultDirectory, "RimWise-sorting-*.zip").Single());
            check(zip.GetEntry("manifest.json") is not null, "零轮失败仍可导出诊断包");
        }
        finally { timer.Stop(); panel.Close(); }
    }

    private static async Task CheckEmbeddedLifecycle(MainWindow main, string output, Action<bool, string> check)
    {
        var root = Path.Combine(output, "嵌入生命周期");
        var vm = new MainViewModel();
        ModInstallation Mod(string id) => new(Path.Combine(root, id), id, ModSource.Local, "",
            new(id, "合成作者", id, ["1.7"], "", ModRequirements.Empty, false), []);
        var catalogue = new ModCatalogue([Mod("first.mod"), Mod("second.mod")]);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "<ModsConfigData><version>1.7</version><activeMods><li>first.mod</li><li>second.mod</li></activeMods></ModsConfigData>"));
        vm.Load(new ModsConfigXmlFormat().Read(input), Path.Combine(root, "ModsConfig.xml"));
        vm.ApplyScan(new(new(null, "", ""), GameVersion.Parse("1.7"), catalogue, []), observeActivity: false);
        var store = new AiSettingsStore(root);
        store.Save(AiSettings.Default with { Endpoint = "https://synthetic.invalid/v1/", Model = "synthetic-model" });
        var client = new Client();
        var secrets = new Secrets();
        var host = new Window { Owner = main, Width = 1000, Height = 760 };
        var panel = new AiSortingWindow(vm, root, client: client, secrets: secrets);
        var view = panel.Embed(host, () => { });
        host.Content = view; host.Show();
        try
        {
            await panel.RefreshUsageAsync();
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                panel.ReadUsageForCheck = () =>
                {
                    started.Set(); release.Wait(TimeSpan.FromSeconds(10)); return "合成用量";
                };
                var usage = panel.RefreshUsageAsync();
                check(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))), "发起任务前已有慢账本读取");
                var request = panel.StartSortingAsync();
                await Task.Delay(150);
                check(panel.IsWorking && client.Calls == 0 && !request.IsCompleted,
                    "开始排序异步等待自己的账本扫描，不与恢复锁冲突或提前发送请求");
                panel.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                release.Set(); await usage; await request;
                check(client.Calls == 0 && !panel.IsWorking, "等待账本时可取消，不发送 AI 请求");
                panel.ReadUsageForCheck = null;
                await panel.RefreshUsageAsync();
            }
            secrets.Value = null;
            var savedDialogs = 0;
            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            settingsTimer.Tick += (_, _) =>
            {
                var dialog = host.OwnedWindows.OfType<AiSettingsWindow>().FirstOrDefault();
                if (dialog is null || savedDialogs != 0) return;
                savedDialogs++;
                dialog.ApiKeyBox.Password = "synthetic-restored-key";
                dialog.ModelBox.Text = "saved-model";
                dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            settingsTimer.Start();
            try { check(!await panel.EnsureAiConfiguredAsync(), "缺少密钥直接进入设置，保存后等待用户再次开始"); }
            finally { settingsTimer.Stop(); }
            check(savedDialogs == 1 && store.Load().Model == "saved-model" && client.Calls == 0 && !panel.IsWorking,
                "补齐配置保存成功，但不会擅自发起 AI 生成或启动游戏");
            secrets.Value = null;
            store.Save(store.Load() with { Endpoint = "http://127.0.0.1:1234/" });
            check(await panel.EnsureAiConfiguredAsync(), "本机兼容服务允许无密钥，不强制进入设置");
            secrets.Value = "synthetic-key";
            store.Save(store.Load() with { Endpoint = "https://synthetic.invalid/v1/" });
            check(await panel.EnsureAiConfiguredAsync(), "有效远程配置不弹设置；此检查不调用 AI");
            var task = panel.StartSortingAsync();
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(panel.SortingBusyLabel.Visibility == Visibility.Visible
                && panel.SortingProgress.IsIndeterminate == InterfaceMotion.Enabled,
                "运行时持续显示处理状态；进度动画遵循系统减少动画设置，不伪造百分比");
            main.AiSortingPageTab.IsChecked = true;
            await main.Dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.ApplicationIdle);
            host.Width = main.AiSortingHost.ActualWidth + 16;
            host.Height = main.AiSortingHost.ActualHeight + 38;
            await host.Dispatcher.InvokeAsync(host.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var actionBounds = panel.AutomaticButton.TransformToAncestor(host).TransformBounds(new Rect(panel.AutomaticButton.RenderSize));
            PrototypeSmokeCheck.SavePreview(host, Path.Combine(output, "紧凑运行状态.png"));
            check(actionBounds.Bottom <= host.ActualHeight && panel.SettingsScroll.ViewportHeight >= 60,
                $"最小主面板尺寸下运行状态和底部取消操作保持可见（按钮 {actionBounds.Bottom:F0} / 宿主 {host.ActualHeight:F0}，设置 {panel.SettingsScroll.ViewportHeight:F0}）");
            view.Visibility = Visibility.Collapsed;
            check(panel.IsWorking && client.Calls == 1, "隐藏嵌入面板不取消任务或重复请求");
            view.Visibility = Visibility.Visible;
            check(!panel.PrepareHostClose(), "任务进行时关闭主窗口先取消并阻止提前退出");
            await task.WaitAsync(TimeSpan.FromSeconds(10));
            check(panel.PrepareHostClose() && !panel.IsWorking && vm.Rows.Select(row => row.PackageId).SequenceEqual(["first.mod", "second.mod"]),
                "取消完成后允许关闭，排序保持不变");
            check(panel.SortingBusyLabel.Visibility == Visibility.Collapsed && !panel.SortingProgress.IsIndeterminate,
                "取消完成后移除忙碌反馈并停止动画");
            panel.DisposeEmbedded();
            var status = panel.StatusText.Text;
            vm.Load(new ModsConfigXmlFormat().Read(new MemoryStream(Encoding.UTF8.GetBytes(
                "<ModsConfigData><version>1.7</version><activeMods><li>second.mod</li></activeMods></ModsConfigData>"))), "synthetic.xml");
            check(panel.StatusText.Text == status, "销毁嵌入面板后取消编辑器订阅，无迟到刷新");
        }
        finally { panel.DisposeEmbedded(); host.Close(); }
    }

    private static async Task CheckUsageResponsiveness(MainWindow main, string output, Action<bool, string> check)
    {
        var root = Path.Combine(output, "慢速账本");
        var panel = new AiSortingWindow(new MainViewModel(), root);
        var host = new Window { Owner = main, Width = 1000, Height = 760 };
        host.Content = panel.Embed(host, () => { });
        host.Show();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            await panel.RefreshUsageAsync();
            panel.TokenText.Text = "本轮统计";
            var calls = 0;
            var uiThread = Environment.CurrentManagedThreadId;
            var backgroundOnly = true;
            panel.ReadUsageForCheck = () =>
            {
                if (Environment.CurrentManagedThreadId == uiThread) backgroundOnly = false;
                Interlocked.Increment(ref calls);
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("合成慢读取超时");
                return "本机累计已回报 123 token";
            };
            var read = panel.RefreshUsageAsync();
            check(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))), "合成慢账本已开始后台读取");
            for (var i = 0; i < 30; i++)
                check(ReferenceEquals(read, panel.RefreshUsageAsync()), "密集刷新合并为同一读取任务 " + i);
            var ticks = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (_, _) => ticks++;
            timer.Start();
            try { await Task.Delay(200); }
            finally { timer.Stop(); }
            check(ticks >= 2 && calls == 1 && !read.IsCompleted && backgroundOnly,
                "账本阻塞时 UI 心跳正常，且无并行账本扫描");
            release.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(10));
            check(calls == 2 && panel.TokenText.Text == "本轮统计\n本机累计已回报 123 token",
                "合并后补读最新账本，累计只显示一次，不改本轮统计");
            panel.ReadUsageForCheck = () => throw new OverflowException("合成异常");
            await panel.RefreshUsageAsync();
            check(panel.TokenText.Text == "本轮统计\n累计用量读取失败，记录已保留。",
                "账本读取异常有中文反馈且不终止进程");
            panel.ReadUsageForCheck = () => "本机累计已回报 456 token";
            await panel.RefreshUsageAsync();
            check(!panel.TokenText.Text.Contains("失败") && panel.TokenText.Text.Contains("456 token"),
                "读取失败后可以再次刷新恢复，不叠加旧提示");
            check(!panel.ResponseText.IsUndoEnabled && !panel.StartupFailureText.IsUndoEnabled,
                "只读流式文本不积累无用撤销历史");
            started.Reset(); release.Reset();
            panel.ReadUsageForCheck = () =>
            {
                started.Set(); release.Wait(TimeSpan.FromSeconds(10)); return "迟到累计";
            };
            read = panel.RefreshUsageAsync();
            check(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))), "迟到读取开始");
            panel.TokenText.Text = "新的运行进度";
            release.Set(); await read;
            check(panel.TokenText.Text == "新的运行进度", "旧账本结果不覆盖新任务统计");
            started.Reset(); release.Reset();
            read = panel.RefreshUsageAsync();
            check(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))), "关闭前慢读取开始");
            panel.DisposeEmbedded();
            var original = panel.TokenText.Text;
            check(!read.IsCompleted, "销毁面板无需等待磁盘读取结束");
            release.Set(); await read;
            check(panel.TokenText.Text == original, "销毁后不更新界面或触发迟到回调");
        }
        finally { release.Set(); await panel.RefreshUsageAsync(); panel.DisposeEmbedded(); host.Close(); }
    }

    private sealed class Secrets : ISecretStore
    {
        internal string? Value = "synthetic-key";
        public string? Read(string name) => Value;
        public void Write(string name, string secret) => Value = secret;
        public void Delete(string name) => Value = null;
    }
    private sealed class Client : IAiClient
    {
        internal int Calls, Probes;
        public Task<AiClientResult> ProbeModelAsync(AiRequest request, CancellationToken token)
        { Probes++; return Task.FromResult(new AiClientResult(false, "AI 服务拒绝请求（HTTP 403）。", null, null)); }
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token)
            => Task.FromResult(new AiConnectionResult(true, "合成连接通过"));
        public async Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            Calls++; Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
#endif
