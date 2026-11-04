#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Game;
using RimWise.Integration.Scanning;
using RimWise.Integration.Safety;

namespace RimWise.Desktop;

/// <summary>Only synthetic editor data and fixture logs; no real AI, game, or mod execution.</summary>
internal static class AiExecutionSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var root = Path.Combine(output, "AI 执行 中文 & [fixture]");
        Directory.CreateDirectory(root);
        ModInstallation Mod(string id) => new(Path.Combine(root, id), id, ModSource.Local, "",
            new(id, "测试作者", id, ["1.7"], "", ModRequirements.Empty, false), []);
        var catalogue = new ModCatalogue([Mod("first.mod"), Mod("second.mod")]);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "<ModsConfigData><version>1.7</version><activeMods><li>first.mod</li><li>second.mod</li></activeMods></ModsConfigData>"));
        var document = new ModsConfigXmlFormat().Read(input);
        var scan = new ModScanResult(new(null, "", ""), GameVersion.Parse("1.7"), catalogue, []);
        void Reset() { vm.Load(document, Path.Combine(root, "ModsConfig.xml")); vm.ApplyScan(scan, observeActivity: false); vm.SetCustomRules([]); }
        AiOrderPlan Plan() => AiOrderPlanner.Plan([new("move", "second.mod", 1, false, null, "测试")],
            AiPermissionLevel.Level1OrderAndRequiredDependencies, vm.Rows.Select(r => r.PackageId).ToArray(),
            catalogue, vm.RetainedPackageIds, GameVersion.Parse("1.7"), vm.CustomRules);
        void Reject(AiOrderPlan plan, long revision)
        {
            try { vm.ApplyAiPlan(plan, revision); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Stale or unavailable AI plan was applied.");
        }
        Reset();
        var plan = Plan(); var revision = vm.ContentRevision;
        var preview = new OrderPreviewWindow(new OrderComparison(new LoadOrder(plan.Before), new LoadOrder(plan.After)),
            "应用 AI 排序", "确认后调整当前编辑清单；保存排序时才写入游戏配置。", "应用排序") { Owner = window };
        preview.Show(); await Layout(preview);
        Assert(vm.Rows[0].PackageId == "first.mod", "Preview applied changes before confirmation.");
        PrototypeSmokeCheck.SavePreview(preview, Path.Combine(output, "AI-排序差异预览.png")); preview.Close();
        var journal = new ExecutionJournal(root);
        var entry = journal.Prepare(plan, AiPermissionLevel.Level1OrderAndRequiredDependencies);
        vm.ApplyAiPlan(plan, revision); journal.Complete(entry);
        Assert(vm.Rows[0].PackageId == "second.mod" && vm.HasChanges, "AI apply failed.");
        vm.UndoCommand.Execute(null);
        Assert(vm.Rows.Select(r => r.PackageId).SequenceEqual(plan.Before), "AI undo failed.");
        vm.RedoCommand.Execute(null);
        Assert(vm.Rows.Select(r => r.PackageId).SequenceEqual(plan.After), "AI cancel-undo failed.");
        Reject(plan, revision);
        Reset(); plan = Plan(); revision = vm.ContentRevision; vm.SetCustomRules([new("first.mod", "second.mod")]); Reject(plan, revision);
        Reset(); plan = Plan(); revision = vm.ContentRevision;
        vm.ApplyScan(scan with { Catalogue = new(catalogue.Installations.Select(mod => mod with { Source = ModSource.Workshop }).ToArray()) }, observeActivity: false);
        Reject(plan, revision);
        Reset(); plan = Plan(); revision = vm.ContentRevision; vm.IsScanning = true; Reject(plan, revision); vm.IsScanning = false;
        vm.IsShowingCache = true; Reject(plan, revision); vm.IsShowingCache = false;
        vm.ApplyScan(scan with { TargetVersion = GameVersion.Parse("1.6") }, observeActivity: false); Reject(plan, revision);
        Reset();
        var logPath = Path.Combine(root, "Player.log");
        File.WriteAllText(logPath, "Error loading first.mod\n at Test.Sample()\n\nError loading first.mod\n at Test.Sample()\n");
        foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            var rules = new OrderRulesWindow([new("first.mod", "second.mod")], catalogue.Installations.Select(m => (m.PackageId, m.Name))) { Owner = window };
            rules.Show(); await Layout(rules);
            PrototypeSmokeCheck.SavePreview(rules, Path.Combine(output, $"{theme}-排序规则.png")); rules.Close();
            var log = new GameLogWindow(logPath, catalogue) { Owner = window };
            log.Show(); await Layout(log);
            await log.RefreshForCheckAsync(); await Layout(log);
            Assert(PlayerLogReader.Read(logPath, catalogue).Issues.Single().Count == 2, "Fixture log grouping failed.");
            PrototypeSmokeCheck.SavePreview(log, Path.Combine(output, $"{theme}-游戏日志.png")); log.Close();
        }
        window.LoadSample();
        File.WriteAllText(Path.Combine(output, "ai-execution.json"), "{\"Passed\":true,\"RealGameRun\":false,\"RealAiRequest\":false}");
    }
    private static async Task Layout(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
