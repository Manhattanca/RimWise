#if DEBUG
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

/// <summary>Isolated editor/desktop checks. No Steam, real game, AI, or real installation moves.</summary>
internal static class SortingInstallationSmokeCheck
{
    internal static void Attach(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try { await Run(main, output); main.ViewModel.MarkExported(); Application.Current.Shutdown(0); }
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
        void Check(bool valid, string name)
        { if (!valid) throw new InvalidOperationException(name); checks.Add(name); }
        var vm = main.ViewModel;
        var root = Path.Combine(output, "fixture"); var mods = Path.Combine(root, "Mods");
        foreach (var id in new[] { "sample.a", "sample.b" })
        {
            var about = Path.Combine(mods, id, "About"); Directory.CreateDirectory(about);
            File.WriteAllText(Path.Combine(about, "About.xml"), $"<ModMetaData><name>样例 {id}</name><packageId>{id}</packageId>"
                + "<supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
        }
        var paths = new GamePaths { LocalModsDirectory = mods, TargetVersionOverride = "1.7" };
        var scan = new ModScanner().Scan(paths, inspectContent: false);
        void Load(params string[] ids)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<ModsConfigData><version>1.7</version><activeMods>"
                + string.Join("", ids.Select(id => "<li>" + id + "</li>")) + "</activeMods></ModsConfigData>"));
            vm.Load(new ModsConfigXmlFormat().Read(stream), Path.Combine(root, "ModsConfig.xml"));
            vm.ApplyScan(scan, observeActivity: false);
        }
        string[] Order() => vm.Rows.Select(r => r.PackageId).ToArray();
        Load("sample.b", "missing.mod", "sample.a");
        Check(Order().Contains("missing.mod"), "导入依旧保留缺失项，不触发自动清理");
        var saves = 0;
        var panel = new AiSortingWindow(vm, root, gamePaths: () => paths, prepareSave: () => () => saves++) { Owner = main };
        try
        {
            await panel.PrepareSortingInstallationsAsync(default, reset: true);
            Check(Order().SequenceEqual(["sample.b", "sample.a"]), "排序准备自动剔除缺失项，不预先重排其他模组");
            Check(panel.ResponseText.Text.Contains("missing.mod") && panel.ResponseText.Text.Contains("未安装"), "清理决定已显示并记录");
            vm.UndoCommand.Execute(null);
            Check(Order().SequenceEqual(["sample.b", "missing.mod", "sample.a"]), "自动清理可撤销并恢复完整原排序");
            Load("sample.a", "sample.b");
            // Simulate an installation vanishing after the editor's old scan.
            Directory.Move(Path.Combine(mods, "sample.b"), Path.Combine(root, "outside-scan"));
            await panel.PrepareSortingInstallationsAsync(default, reset: true);
            Check(Order().SequenceEqual(["sample.a"]), "开始时重新扫描，旧缓存内已消失的模组也能剔除");
            Directory.Move(Path.Combine(root, "outside-scan"), Path.Combine(mods, "sample.b"));
            vm.UndoCommand.Execute(null);
            Check(Order().SequenceEqual(["sample.a", "sample.b"]), "刷新后的清理仍可撤销");
            Load("sample.a", "missing.mod", "sample.b");
            await panel.PrepareSortingInstallationsAsync(default, reset: true);
            await panel.RollbackUncommittedInstallationsAsync();
            Check(Order().SequenceEqual(["sample.a", "missing.mod", "sample.b"]), "取消且尚未保存时恢复开始前清单，避免原工坊标识失效");
            await panel.PrepareSortingInstallationsAsync(default, reset: true);
            panel.PrepareInstallationAwareSave()();
            await panel.RollbackUncommittedInstallationsAsync();
            Check(saves == 1 && Order().SequenceEqual(["sample.a", "sample.b"]), "配置保存即为提交点，后续账本错误不可撤回已保存的副本选择");
        }
        finally { panel.Close(); }

        // A schema error still has a confirmed identity; an unreadable whole document
        // must retain unresolved original tokens rather than guessing a directory.
        var brokenAbout = Path.Combine(mods, "sample.b", "About", "About.xml");
        File.WriteAllText(brokenAbout, "<ModMetaData><name>读取异常样例</name><packageId>sample.b</packageId>"
            + "<modDependencies><li><displayName>缺少标识</displayName></li></modDependencies></ModMetaData>");
        Load("sample.a", "sample.b");
        panel = new AiSortingWindow(vm, root, gamePaths: () => paths) { Owner = main };
        try
        {
            var notices = 0;
            panel.MetadataReadNotice = messages => notices += messages.Count;
            panel.Show();
            var ownedWindowsBeforeWarning = main.OwnedWindows.Count;
            await panel.PrepareSortingInstallationsAsync(default, reset: true);
            Check(Order().SequenceEqual(["sample.a", "sample.b"]) && notices == 1, "单项读取异常独立提示且仍保留原排序参加测试");
            Check(vm.CurrentCatalogue!.Resolve("sample.b", new(1, 7)).UniqueInstallation is { Metadata: null, PackageId: "sample.b" },
                "读取失败仅保留核对过的身份，不复用或伪造依赖");
            Check(panel.MetadataReadWarning.Visibility == Visibility.Visible && panel.ResponseText.Text.Contains("读取记录")
                && panel.ResponseText.Text.Contains(brokenAbout.Replace("\\About\\About.xml", "")), "独立警告提供异常位置及查看读取记录指引");
            var readIssuesButton = panel.MetadataReadWarning.Children.OfType<Button>().Single();
            panel.UpdateLayout();
            readIssuesButton.BringIntoView();
            await panel.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            panel.UpdateLayout();
            var buttonBounds = readIssuesButton.TransformToAncestor(panel.StatusScroll).TransformBounds(
                new Rect(new Point(), readIssuesButton.RenderSize));
            Check(readIssuesButton.IsVisible && readIssuesButton.IsEnabled && readIssuesButton.ActualWidth > 0
                && buttonBounds.Top >= -1 && buttonBounds.Bottom <= panel.StatusScroll.ActualHeight + 1,
                "读取异常按钮实际可见且在滚动视口内可操作");
            var readIssuesOpened = 0;
            panel.OpenReadIssues = () => readIssuesOpened++;
            readIssuesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(readIssuesOpened == 1 && main.IsEnabled && panel.IsEnabled
                && main.OwnedWindows.Count == ownedWindowsBeforeWarning,
                "读取异常仅非模态提醒，查看按钮可调用且不新增确认窗口");
            var warningPreview = Path.Combine(output, "读取异常提示.png");
            PrototypeSmokeCheck.SavePreview(panel, warningPreview);
            Check(new FileInfo(warningPreview).Length > 0, "读取异常可见提示已保存桌面预览");
            File.WriteAllText(brokenAbout, "<ModMetaData>");
            await panel.PrepareSortingInstallationsAsync(default, reset: true);
            Check(Order().Contains("sample.b") && vm.CurrentCatalogue.Resolve("sample.b", new(1, 7)).UniqueInstallation?.Metadata is null,
                "完整XML无法读取时仅沿用同安装身份，不误当未安装");
            // Drop history deliberately: a fresh scan cannot bind the corrupt directory.
            vm.ApplyScan(new ModScanner().Scan(paths, inspectContent: false), observeActivity: false);
            await panel.PrepareSortingInstallationsAsync(default, reset: true);
            Check(Order().Contains("sample.b") && vm.CurrentCatalogue!.IsUnverifiedOrderIdentifier("sample.b")
                && vm.CurrentCatalogue.Resolve("sample.b", new(1, 7)).Candidates.Count == 0, "全新未知归属项只透传原标识，不伪造目录或提前剔除");
        }
        finally { panel.Close(); }
        // Offline sorting has no runtime evidence and must never turn a read warning into removal authority.
        new AutomaticSortingPreferencesStore(root).SaveMode("offline", true);
        Load("sample.a", "sample.b");
        var offlineOrder = Order();
        var offlineConfig = Path.Combine(root, "ModsConfig.xml");
        var offlineBytes = Encoding.UTF8.GetBytes("synthetic configuration must remain untouched");
        File.WriteAllBytes(offlineConfig, offlineBytes);
        panel = new AiSortingWindow(vm, root, gamePaths: () => paths) { Owner = main };
        string? readFailureOfflineNotice = null;
        var offlineSucceeded = false;
        panel.OfflineResultNotice = (message, succeeded) => { readFailureOfflineNotice = message; offlineSucceeded = succeeded; };
        try
        {
            await panel.StartOfflineAsync();
            Check(offlineSucceeded && vm.ScanIssues.Any(issue => issue.Diagnostic.Code == "MetadataUnreadable"),
                "离线排序容忍单项元数据读取失败并正常完成");
            Check(Order().SequenceEqual(offlineOrder) && Order().Contains("sample.b"),
                "离线读取异常不阻断且不删除异常项");
            Check(readFailureOfflineNotice?.Contains("未进行游戏测试，不代表运行时无红字") == true
                && !readFailureOfflineNotice.Contains("已从排序剔除") && !readFailureOfflineNotice.Contains("本轮测试无红字"),
                "离线结算明确未游戏验证，不误报读取异常已剔除或运行无错");
            Check(File.ReadAllBytes(offlineConfig).SequenceEqual(offlineBytes) && File.Exists(brokenAbout),
                "离线读取异常保留安装文件及原配置，不自动保存");
        }
        finally { panel.Close(); }
        File.WriteAllText(brokenAbout, "<ModMetaData><name>样例b</name><packageId>sample.b</packageId></ModMetaData>");

        Load("sample.copy_steam");
        var metadata = new ModMetadata("别名样例", "test", "sample.copy", ["1.7"], "", ModRequirements.Empty, false);
        var local = new ModInstallation(Path.Combine(root, "local"), "local", ModSource.Local, "", metadata, []);
        var workshop = local with { Directory = Path.Combine(root, "workshop"), FolderName = "123", WorkshopId = "123", Source = ModSource.Workshop };
        var before = new ModCatalogue([local, workshop]); var after = new ModCatalogue([workshop]);
        vm.ApplyScan(scan with { Catalogue = before }, observeActivity: false);
        vm.ToggleRetained(vm.Rows.Single());
        vm.ApplyColor("#F59E0B", vm.Rows, []);
        var prepared = SortingInstallationPolicy.Normalize(before, after, Order(), new(1, 7), new HashSet<string>());
        vm.ApplyScan(scan with { Catalogue = after }, observeActivity: false);
        vm.ApplyInstallationPreparation(prepared, vm.ContentRevision);
        Check(Order().Single() == "sample.copy" && vm.IsRetained("sample.copy") && vm.Rows.Single().IsRetained,
            "工坊别名合并时保留必须保留标记，不修改全局解析规则");
        vm.UndoCommand.Execute(null);
        Check(Order().Single() == "sample.copy_steam" && vm.IsRetained("sample.copy_steam") && !vm.IsRetained("sample.copy"),
            "撤销同步恢复别名与保护标记");
        var rejected = false;
        try { vm.ApplyInstallationPreparation(prepared, vm.ContentRevision - 1); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && Order().Single() == "sample.copy_steam", "内容版本不匹配时禁止迟到清理覆盖编辑");

        Load("sample.a", "missing.mod", "sample.b");
        new AutomaticSortingPreferencesStore(root).SaveMode("offline", true);
        panel = new AiSortingWindow(vm, root, gamePaths: () => paths) { Owner = main };
        string? notice = null;
        panel.OfflineResultNotice = (message, _) => notice = message;
        try
        {
            await panel.StartOfflineAsync();
            Check(Order().SequenceEqual(["sample.a", "sample.b"]) && notice?.Contains("missing.mod") == true,
                "离线排序复用清理并在结算汇总缺失项，不调用游戏或AI");
            Check(panel.SummaryText.Text.Contains("模组处理汇总"), "取消与完成路径均保留处理汇总界面");
        }
        finally { panel.Close(); }
        await MetadataReadFailurePublicationSmokeCheck.RunAsync(main, output, Check);
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
#endif
