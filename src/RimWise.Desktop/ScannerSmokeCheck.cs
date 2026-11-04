#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;
using RimWise.Desktop.Presentation;
using RimWise.Formats;

namespace RimWise.Desktop;

internal static class ScannerSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output)
    {
        var root = Path.Combine(output, "扫描 界面样例");
        var paths = new GamePaths
        {
            GameDirectory = Path.Combine(root, "游戏"), LocalModsDirectory = Path.Combine(root, "本地模组"),
            WorkshopDirectory = Path.Combine(root, "创意工坊")
        };
        Directory.CreateDirectory(paths.GameDirectory);
        File.WriteAllText(Path.Combine(paths.GameDirectory, "Version.txt"), "1.7.4985 rev1269");
        WriteMod(Path.Combine(paths.GameDirectory, "Data", "Core"), "<name>Core</name><packageId>ludeon.rimworld</packageId>");
        WriteMod(Path.Combine(paths.LocalModsDirectory, "建筑"), "<name>中文建筑</name><packageId>example.buildings</packageId><author>示例作者</author><description>为殖民地添加建筑与家具。\n选择建筑后，可在游戏中查看材料需求及使用方式。</description><supportedVersions><li>1.7</li></supportedVersions><modDependencies><li><packageId>example.framework</packageId><displayName>示例框架</displayName></li></modDependencies>");
        WriteMod(Path.Combine(paths.WorkshopDirectory, "100"), "<name>示例框架</name><packageId>example.framework</packageId><supportedVersions><li>1.6</li></supportedVersions>");
        WriteMod(Path.Combine(paths.LocalModsDirectory, "武器"), "<name>示例武器</name><packageId>example.weapons</packageId>");
        WriteMod(Path.Combine(paths.WorkshopDirectory, "200"), "<name>示例武器</name><packageId>example.weapons</packageId>");
        WriteMod(Path.Combine(paths.LocalModsDirectory, "闲置建筑"), "<name>闲置建筑</name><packageId>inactive.buildings</packageId><supportedVersions><li>1.6</li></supportedVersions><modDependencies><li><packageId>inactive.missing</packageId></li></modDependencies>");
        WriteMod(Path.Combine(paths.LocalModsDirectory, "闲置武器"), "<name>闲置武器</name><packageId>inactive.weapons</packageId><supportedVersions><li>1.6</li></supportedVersions>");
        DetailsSmokeCheck.CreateFixtures(paths);
        var scan = new ModScanner().Scan(paths);
        var empty = new MainViewModel();
        empty.ApplyScan(scan);
        if (empty.AvailableMods.Count != 7 || empty.Diagnostics.Count != 0 || empty.HasOrderStatus)
            throw new InvalidOperationException("Scanning without an order must not show compatibility warnings.");

        var vm = window.ViewModel;
        vm.SelectedRow = vm.Rows[1];
        vm.MoveDownCommand.Execute(null);
        var before = vm.Rows.Select(row => row.PackageId).ToArray();
        await window.ScanForCheckAsync(paths);
        // The workshop variant remains available when the local copy is active.
        if (vm.AvailableMods.Count != 3 || !vm.HasChanges || !before.SequenceEqual(vm.Rows.Select(row => row.PackageId)))
            throw new InvalidOperationException($"Partition or scan lost pending edits. Available={string.Join(",", vm.AvailableMods.Select(row => row.PackageId))}; Changed={vm.HasChanges}; Before={string.Join(",", before)}; After={string.Join(",", vm.Rows.Select(row => row.PackageId))}");
        if (!string.Equals(window.RefreshButton.Content as string, "刷新", StringComparison.Ordinal))
            throw new InvalidOperationException("Refresh button did not use the player-facing label.");
        AssertLiveStatus(vm);
        if (!vm.Rows.Single(row => row.PackageId == "example.framework").HasWarning
            || !vm.Rows.Single(row => row.PackageId == "example.stories").HasError
            || vm.Rows.Single(row => row.PackageId == "example.weapons").HasError
            || !vm.AvailableMods.Any(row => row.PackageId == "example.weapons" && row.Installation.Source == ModSource.Workshop))
            throw new InvalidOperationException("Automatic severity did not match missing, ambiguous or version declarations.");

        var issueCount = vm.Diagnostics.Count;
        vm.SearchText = "闲置";
        if (vm.VisibleAvailableMods.Cast<object>().Count() != 2 || vm.VisibleRows.Cast<object>().Any() || issueCount != vm.Diagnostics.Count)
            throw new InvalidOperationException("Search hid diagnostics or failed to filter both lists.");
        vm.SelectedAvailable = vm.AvailableMods[0];
        if (vm.MoveUpCommand.CanExecute(null)) throw new InvalidOperationException("Inactive selection can move a stale active selection.");
        vm.SearchText = string.Empty;

        vm.SelectedRow = vm.Rows.Single(row => row.PackageId == "example.buildings");
        vm.MoveUpCommand.Execute(null);
        if (!vm.SelectedRow!.HasError || !vm.SelectedRow.DiagnosticTooltip.Contains("必须在它之前"))
            throw new InvalidOperationException("Moving before a prerequisite did not immediately turn the row red.");
        vm.MoveDownCommand.Execute(null);
        if (vm.SelectedRow!.HasError || vm.SelectedRow.HasWarning)
            throw new InvalidOperationException($"Fixing dependency order retained a stale row warning. Selected={vm.SelectedRow.PackageId}, Order={string.Join(",", vm.Rows.Select(row => row.PackageId))}, Detail={vm.SelectedRow.DiagnosticTooltip}");

        vm.SelectedAvailable = vm.AvailableMods.Single(row => row.PackageId == "inactive.buildings");
        vm.AddToOrderCommand.Execute(null);
        if (vm.Rows.Count != 6 || vm.AvailableMods.Count != 2 || !vm.HasChanges || !vm.SelectedRow!.HasError
            || !vm.SelectedRow.DiagnosticTooltip.Contains("未声明支持") || !vm.SelectedRow.DiagnosticTooltip.Contains("前置"))
            throw new InvalidOperationException("Adding failed to immediately aggregate warnings and errors.");
        vm.RemoveFromOrderCommand.Execute(null);
        if (vm.AvailableMods.Count != 3 || !before.SequenceEqual(vm.Rows.Select(row => row.PackageId)))
            throw new InvalidOperationException("Removing failed to restore the partition.");
        AssertLiveStatus(vm);

        var diagnosticsBefore = vm.Diagnostics.Select(issue => issue.Diagnostic).ToArray();
        var cancellation = window.ScanForCheckAsync(paths);
        window.CancelScanForCheck();
        await cancellation;
        if (vm.IsScanning || !vm.HasChanges || !before.SequenceEqual(vm.Rows.Select(row => row.PackageId)))
            throw new InvalidOperationException("Cancelled scan lost the last snapshot or pending edits.");
        if (!diagnosticsBefore.SequenceEqual(vm.Diagnostics.Select(issue => issue.Diagnostic)))
            throw new InvalidOperationException("Cancelled scan invalidated current row status.");

        var original = vm.CreateExportDocument();
        vm.Load(original.WithOrder(new LoadOrder(before.Append("example.framework"))), "重复排序.xml");
        if (vm.Rows.Where(row => row.PackageId == "example.framework").Any(row => !row.HasError))
            throw new InvalidOperationException("Duplicate entries were not both marked as errors.");
        vm.Load(original.WithOrder(new LoadOrder([])), "空排序.xml");
        if (!vm.HasOrderStatus || vm.Diagnostics.Count != 0 || vm.AvailableMods.Count != 7 || vm.ErrorCount != 0 || vm.WarningCount != 0)
            throw new InvalidOperationException("Empty order contains inactive-mod diagnostics.");
        vm.SearchText = "闲置";
        vm.Load(original, "ModsConfig.xml");
        if (vm.SearchText.Length != 0 || vm.AvailableMods.Count != 3) throw new InvalidOperationException("Import did not rebuild both lists.");
        AssertLiveStatus(vm);
        await DetailsSmokeCheck.RunAsync(window, paths, output);
        vm.MarkExported();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        if (window.ModDetails.TranslatePoint(new Point(), window).X >= window.AvailableGrid.TranslatePoint(new Point(), window).X
            || window.AvailableGrid.TranslatePoint(new Point(), window).X >= window.OrderGrid.TranslatePoint(new Point(), window).X)
            throw new InvalidOperationException("Three-pane layout is incorrect.");
        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "实时状态与模组详情.png"));
    }

    private static void AssertLiveStatus(MainViewModel vm)
    {
        if (!vm.HasOrderStatus || vm.Diagnostics.Count == 0 || vm.ErrorCount == 0 || vm.WarningCount == 0
            || vm.Diagnostics.Any(issue => issue.Identifier.StartsWith("inactive.") || issue.Message.Contains("闲置")))
            throw new InvalidOperationException("Automatic status leaked inactive-mod warnings or was not updated.");
    }
    internal static async Task CheckInstalledGameAsync(GamePaths paths, string output)
    {
        var timer = Stopwatch.StartNew();
        // Actual-library smoke covers the initial metadata path. Content inspection
        // is exercised by bounded fixtures, not by reading every installed asset.
        var result = await Task.Run(() => new ModScanner().Scan(paths, inspectContent: false));
        timer.Stop();
        var vm = new MainViewModel();
        vm.ApplyScan(result);
        if (vm.HasOrderStatus || vm.Diagnostics.Count != 0 || vm.AvailableMods.Count != result.Catalogue.Installations.Count)
            throw new InvalidOperationException("Real-library scan showed order warnings with no order loaded.");
        int? activeCount = null;
        int? inactiveCount = null;
        var file = CurrentOrderLocation.Resolve(paths);
        if (File.Exists(file))
        {
            var originalHash = SHA256.HashData(File.ReadAllBytes(file));
            var loaded = new PrototypeFileService().Read(file);
            var current = new MainViewModel();
            current.Load(loaded, file);
            current.ApplyScan(result);
            if (!loaded.Order.PackageIds.SequenceEqual(current.Rows.Select(row => row.PackageId))
                || current.HasChanges || !current.HasOrderStatus
                || !originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))))
                throw new InvalidOperationException("Real current order was changed while loading or scanning.");
            activeCount = current.Rows.Count;
            inactiveCount = current.AvailableMods.Count;
        }
        File.WriteAllText(Path.Combine(output, "installed-scan.json"), JsonSerializer.Serialize(new
        {
            DetectedVersion = result.DetectedVersion.Version?.ToString(), Count = result.Catalogue.Installations.Count,
            DurationMilliseconds = timer.ElapsedMilliseconds, VisibleOrderWarnings = vm.Diagnostics.Count,
            CurrentOrderEntries = activeCount, InactiveInstallations = inactiveCount,
            Sources = result.Catalogue.Installations.GroupBy(mod => mod.Source).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            ReadErrors = result.Diagnostics.Where(issue => issue.Code is "MetadataUnreadable" or "ScanRootUnreadable").ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void WriteMod(string directory, string fields)
    {
        Directory.CreateDirectory(Path.Combine(directory, "About"));
        File.WriteAllText(Path.Combine(directory, "About", "About.xml"), "<ModMetaData>" + fields + "</ModMetaData>");
    }
}
#endif
