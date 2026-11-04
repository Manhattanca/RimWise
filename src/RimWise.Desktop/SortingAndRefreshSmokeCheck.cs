#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class SortingAndRefreshSmokeCheck
{
    internal static async Task RunAsync(MainWindow owner, string output)
    {
        var window = new MainWindow(initializePaths: false) { Owner = owner };
        var vm = window.ViewModel;
        var themes = ((App)Application.Current).Themes;
        var preference = themes.Preference;
        try
        {
            window.Show();
            var root = Path.Combine(output, "刷新与排序 [中文] & café '");
            var config = Path.Combine(root, "Config");
            var mods = Path.Combine(root, "Mods");
            Directory.CreateDirectory(config);
            WriteMod(mods, "核心", "ludeon.rimworld", "");
            WriteMod(mods, "框架", "test.base", "<loadAfter><li>ludeon.rimworld</li></loadAfter>");
            WriteMod(mods, "建筑", "test.feature", "<modDependencies><li><packageId>test.base</packageId></li></modDependencies>");
            WriteMod(mods, "闲置", "test.unused", "");
            var file = Path.Combine(config, "ModsConfig.xml");
            File.WriteAllText(file, "<ModsConfigData><version>1.7.4985</version><activeMods><li>test.feature</li><li>ludeon.rimworld</li><li>test.base</li></activeMods><future>中文字段</future></ModsConfigData>");
            var originalBytes = File.ReadAllBytes(file);
            var paths = new GamePaths { ConfigDirectory = config, LocalModsDirectory = mods, TargetVersionOverride = "1.7" };
            await window.RefreshForCheckAsync(paths);
            Assert(vm.Rows.Count == 3 && vm.AvailableMods.Count == 1 && window.RefreshButton.Content as string == "刷新", "Refresh did not read both lists.");
            Assert(vm.AutoSortCommand.CanExecute(null), "Existing sort command unavailable after successful refresh.");
            var initial = Order(vm);
            vm.SelectedRow = vm.Rows[0];
            vm.SearchText = "test.feature";
            vm.AutoSortCommand.Execute(null);
            var sorted = Order(vm);
            Assert(sorted.SequenceEqual(["ludeon.rimworld", "test.base", "test.feature"])
                && vm.Rows.Select(row => row.Index).SequenceEqual([1, 2, 3]) && vm.HasChanges
                && vm.SelectedRow?.PackageId == "test.feature" && vm.SearchText == "test.feature" && vm.ErrorCount == 0,
                "Automatic sort lost selection, filtering, status or complete order.");
            vm.UndoCommand.Execute(null);
            Assert(Order(vm).SequenceEqual(initial) && !vm.HasChanges, "Automatic sort undo did not restore clean original.");
            vm.RedoCommand.Execute(null);
            Assert(Order(vm).SequenceEqual(sorted) && vm.HasChanges, "Automatic sort cancel undo failed.");
            vm.AutoSortCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            Assert(Order(vm).SequenceEqual(initial) && !vm.UndoCommand.CanExecute(null), "No-op sort consumed a history step.");
            vm.RedoCommand.Execute(null);
            Assert(originalBytes.SequenceEqual(File.ReadAllBytes(file)), "Automatic sort wrote the game configuration.");

            await CheckRefreshCancellation(window, paths, file);
            var previousAction = vm.LastAction;
            await window.RefreshForCheckAsync(paths);
            Assert(Order(vm).SequenceEqual(initial) && !vm.HasChanges && vm.LastAction == previousAction
                && !vm.UndoCommand.CanExecute(null), "Completed refresh did not reload order or preserve last action.");
            vm.AutoSortCommand.Execute(null);

            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                themes.Select(theme, save: false);
                window.Width = 960;
                window.Height = 640;
                await Idle(window);
                var buttons = ((StackPanel)window.RefreshButton.Parent).Children.OfType<Button>().ToArray();
                var points = buttons.Select(button => button.TranslatePoint(new Point(), window)).ToArray();
                Assert(buttons.Length == 10 && points[^1].X + buttons[^1].ActualWidth <= window.ActualWidth, "Refresh actions clipped.");
                for (var index = 1; index < points.Length; index++)
                    Assert(Math.Abs(points[index].Y - points[0].Y) < 1 && points[index].X >= points[index - 1].X + buttons[index - 1].ActualWidth,
                        "Refresh actions wrap or overlap.");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "刷新与自动排序最小窗口.png"));
            }

            WriteMod(mods, "框架", "test.base", "<loadAfter><li>test.feature</li></loadAfter>");
            await window.RefreshForCheckAsync(paths);
            vm.AutoSortCommand.Execute(null);
            Assert(Order(vm).SequenceEqual(initial) && !vm.HasChanges && !vm.UndoCommand.CanExecute(null)
                && vm.Status.Contains("循环") && vm.Diagnostics.Count(issue => issue.Diagnostic.Code == "OrderingCycle") == 2
                && vm.Rows.Where(row => row.PackageId != "ludeon.rimworld").All(row => row.HasError && row.DiagnosticTooltip.Contains("循环")),
                "Cycle did not report participants without changing history.");
            var issues = window.OpenIssues(DiagnosticSeverity.Error);
            await Idle(issues);
            Assert(issues.IssuesGrid.Items.Cast<DiagnosticRow>().Count(row => row.Diagnostic.Code == "OrderingCycle") == 2,
                "Cycle reasons missing from bottom error summary.");
            PrototypeSmokeCheck.SavePreview(issues, Path.Combine(output, "循环排序原因.png"));
            issues.Close();
            vm.SelectedRow = vm.Rows.First(row => row.PackageId == "test.feature");
            vm.RemoveFromOrderCommand.Execute(null);
            Assert(!vm.Diagnostics.Any(issue => issue.Diagnostic.Code == "OrderingCycle"), "Cycle diagnosis survived a new edit.");
            Assert(originalBytes.SequenceEqual(File.ReadAllBytes(file)), "Read or sort changed configuration bytes.");
        }
        finally
        {
            themes.Select(preference, save: false);
            vm.MarkExported();
            window.Close();
        }
    }

    private static async Task CheckRefreshCancellation(MainWindow window, GamePaths paths, string file)
    {
        var vm = window.ViewModel;
        var before = Order(vm);
        var action = vm.LastAction;
        var issues = vm.Diagnostics.Select(issue => issue.Diagnostic).ToArray();
        var read = new TaskCompletionSource<ModsConfigDocument>();
        var pending = window.RefreshForCheckAsync(paths, (_, _) => read.Task);
        Assert(vm.IsScanning && window.RefreshButton.Content as string == "取消刷新" && !vm.AutoSortCommand.CanExecute(null), "Refresh not cancellable during config reading.");
        window.CancelScanForCheck();
        read.SetResult(new PrototypeFileService().Read(file));
        await pending;
        Assert(!vm.IsScanning && Order(vm).SequenceEqual(before) && vm.LastAction == action && vm.HasChanges
            && issues.SequenceEqual(vm.Diagnostics.Select(issue => issue.Diagnostic)) && vm.UndoCommand.CanExecute(null),
            "Cancellation changed order, history or diagnosis.");

        var entered = new TaskCompletionSource();
        var scan = new TaskCompletionSource<ModScanResult>();
        pending = window.RefreshForCheckAsync(paths, scanner: (_, _, _) => { entered.SetResult(); return scan.Task; });
        await entered.Task;
        Assert(Order(vm).SequenceEqual(before) && vm.HasChanges, "Refresh committed order before scan finished.");
        window.CancelScanForCheck();
        scan.SetResult(new ModScanner().Scan(paths));
        await pending;
        Assert(Order(vm).SequenceEqual(before) && vm.LastAction == action && vm.HasChanges, "Scan cancellation committed half a refresh.");

        read = new TaskCompletionSource<ModsConfigDocument>();
        pending = window.RefreshForCheckAsync(paths, (_, _) => read.Task);
        vm.SelectedRow = vm.Rows[1];
        vm.MoveDownCommand.Execute(null);
        var edited = Order(vm);
        read.SetResult(new PrototypeFileService().Read(file));
        await pending;
        Assert(Order(vm).SequenceEqual(edited) && vm.HasChanges && vm.Status.Contains("已保留当前编辑"), "Stale refresh overwrote a new edit.");

        var failedOrder = Order(vm);
        pending = window.RefreshForCheckAsync(paths, scanner: (_, _, _) => Task.FromException<ModScanResult>(new IOException("受控失败")));
        await pending;
        Assert(Order(vm).SequenceEqual(failedOrder) && vm.HasChanges && vm.Status.Contains("刷新未完成"), "Failed refresh erased editor.");
    }

    private static void WriteMod(string mods, string folder, string id, string rules)
    {
        var about = Path.Combine(mods, folder, "About");
        Directory.CreateDirectory(about);
        File.WriteAllText(Path.Combine(about, "About.xml"), $"<ModMetaData><name>{folder}</name><packageId>{id}</packageId><supportedVersions><li>1.7</li></supportedVersions>{rules}</ModMetaData>");
    }
    private static string[] Order(MainViewModel vm) => vm.Rows.Select(row => row.PackageId).ToArray();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
#endif
