#if DEBUG
using System.IO;
using RimWise.Formats;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class StartupSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output)
    {
        var root = Path.Combine(output, "启动 自动读取 [中文] &");
        var config = Path.Combine(root, "存档", "Config");
        var mods = Path.Combine(root, "Mods");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(Path.Combine(mods, "启用", "About"));
        Directory.CreateDirectory(Path.Combine(mods, "未启用", "About"));
        File.WriteAllText(Path.Combine(mods, "启用", "About", "About.xml"),
            "<ModMetaData><name>启动测试模组</name><packageId>test.enabled</packageId><author>测试作者</author><description>中文简介\n第二行说明</description></ModMetaData>");
        File.WriteAllText(Path.Combine(mods, "未启用", "About", "About.xml"),
            "<ModMetaData><name>未启用测试</name><packageId>test.disabled</packageId></ModMetaData>");
        var file = Path.Combine(config, "ModsConfig.xml");
        File.WriteAllText(file, "<ModsConfigData><version>1.7.4985</version><activeMods><li>test.enabled</li><li>test.missing</li></activeMods><extra>保留字段</extra></ModsConfigData>");
        var original = File.ReadAllBytes(file);
        var paths = new GamePaths { ConfigDirectory = config, LocalModsDirectory = mods, TargetVersionOverride = "1.7" };
        await window.StartupForCheckAsync(paths);
        var vm = window.ViewModel;
        if (vm.Rows.Count != 2 || vm.Rows[0].Name != "启动测试模组" || vm.AvailableMods.Count != 1
            || vm.SelectedDescription != "中文简介\n第二行说明" || vm.HasChanges || !vm.HasOrderStatus || !vm.Rows[1].HasError
            || !original.SequenceEqual(File.ReadAllBytes(file)))
            throw new InvalidOperationException("Automatic startup read, partition, description or read-only guarantee failed.");

        var refreshBackup = File.ReadAllBytes(file);
        File.WriteAllText(file, "<ModsConfigData><version>1.7.4985</version><activeMods><li>test.enabled</li></activeMods></ModsConfigData>");
        await window.RefreshForCheckAsync(paths);
        if (vm.Rows.Count != 1 || vm.Rows[0].PackageId != "test.enabled" || vm.AvailableMods.Count != 1
            || vm.IsScanning || !string.Equals(vm.ScanActionLabel, "刷新", StringComparison.Ordinal))
            throw new InvalidOperationException("Refresh did not reload the current order and catalogue.");
        File.WriteAllBytes(file, refreshBackup);
        await window.RefreshForCheckAsync(paths);

        window.LoadSample();
        vm.SelectedRow = vm.Rows[1];
        vm.MoveDownCommand.Execute(null);
        var order = vm.Rows.Select(row => row.PackageId).ToArray();
        await window.StartupForCheckAsync(paths);
        if (!vm.HasChanges || !order.SequenceEqual(vm.Rows.Select(row => row.PackageId)))
            throw new InvalidOperationException("Automatic scan replaced a pending order.");

        var read = new TaskCompletionSource<ModsConfigDocument>();
        var pending = window.LoadCurrentOrderAsync(false, _ => read.Task);
        window.LoadSample();
        var revision = vm.ContentRevision;
        read.SetResult(new PrototypeFileService().Read(file));
        await pending;
        if (revision != vm.ContentRevision || vm.Rows.Count != 5)
            throw new InvalidOperationException("A stale config read replaced a manual import.");

        read = new TaskCompletionSource<ModsConfigDocument>();
        pending = window.LoadCurrentOrderAsync(false, _ => read.Task);
        vm.SelectedRow = vm.Rows[1];
        vm.MoveDownCommand.Execute(null);
        vm.MarkExported();
        revision = vm.ContentRevision;
        read.SetResult(new PrototypeFileService().Read(file));
        await pending;
        if (revision != vm.ContentRevision || vm.Rows.Count != 5)
            throw new InvalidOperationException("A stale config read replaced an intervening edit/export.");

        var notice = await window.LoadCurrentOrderAsync(false, _ => Task.FromException<ModsConfigDocument>(new FileNotFoundException()));
        if (notice is null || vm.Rows.Count != 5) throw new InvalidOperationException("Missing configuration erased the document.");
        notice = await window.LoadCurrentOrderAsync(false, _ => Task.FromException<ModsConfigDocument>(new System.Xml.XmlException()));
        if (notice is null || vm.Rows.Count != 5) throw new InvalidOperationException("Malformed configuration erased the document.");
        var empty = new MainWindow(initializePaths: false);
        try
        {
            await empty.StartupForCheckAsync(paths with { ConfigDirectory = Path.Combine(root, "不存在") });
            if (empty.ViewModel.HasDocument || !empty.ViewModel.Status.Contains("未找到当前排序") || empty.ViewModel.AvailableMods.Count != 2)
                throw new InvalidOperationException("Missing startup config was hidden by successful catalogue scanning.");
        }
        finally { empty.Close(); }
        if (!original.SequenceEqual(File.ReadAllBytes(file))) throw new InvalidOperationException("Startup altered game config.");
        window.LoadSample();
    }
}
#endif
