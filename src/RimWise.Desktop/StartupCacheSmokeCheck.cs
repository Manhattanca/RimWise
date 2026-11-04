#if DEBUG
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RimWise.Formats;
using RimWise.Integration.Cache;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class StartupCacheSmokeCheck
{
    internal static async Task RunAsync(MainWindow owner, string output)
    {
        var root = Path.Combine(output, "启动缓存 [中文] & café");
        var config = Path.Combine(root, "Config");
        var mods = Path.Combine(root, "Mods");
        var about = Path.Combine(mods, "模组", "About");
        Directory.CreateDirectory(config); Directory.CreateDirectory(about);
        File.WriteAllText(Path.Combine(about, "About.xml"), "<ModMetaData><name>缓存测试模组</name><packageId>test.cache</packageId><description>缓存简介</description><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
        var file = Path.Combine(config, "ModsConfig.xml");
        File.WriteAllText(file, "<ModsConfigData><version>1.7</version><activeMods><li>test.cache</li><li>test.old</li></activeMods></ModsConfigData>");
        var paths = new GamePaths { ConfigDirectory = config, LocalModsDirectory = mods, TargetVersionOverride = "1.7" };
        var store = new StartupCacheStore(Path.Combine(root, "应用"));
        var cachedConfig = new PrototypeFileService().Read(file);
        var scan = new ModScanner().Scan(paths);
        Assert(store.TrySave(paths, cachedConfig, scan), "Could not seed cache");
        File.WriteAllText(file, "<ModsConfigData><version>1.7</version><activeMods><li>test.cache</li></activeMods></ModsConfigData>");
        var freshBytes = File.ReadAllBytes(file);
        var watch = Stopwatch.StartNew();
        double firstDisplayMs;
        var window = new MainWindow(initializePaths: false) { Owner = owner };
        try
        {
            window.Show();
            var entered = new TaskCompletionSource();
            var waiting = new TaskCompletionSource<ModScanResult>();
            var pending = window.CachedStartupForCheckAsync(paths, store, scanner: (_, _, _) => { entered.SetResult(); return waiting.Task; });
            await entered.Task;
            var vm = window.ViewModel;
            Assert(window.ShouldReadStartupOrder, "Unchanged startup cache would skip game order refresh");
            Assert(vm.IsShowingCache && vm.IsScanning && vm.Rows.Count == 2 && vm.Rows[0].Name == "缓存测试模组"
                && vm.SelectedDescription == "缓存简介" && vm.GameVersionLabel.Contains("缓存") && !vm.HasChanges
                && !vm.AutoSortCommand.CanExecute(null), "Cache not shown before real refresh");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            firstDisplayMs = watch.Elapsed.TotalMilliseconds;
            window.UpdateLayout();
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "缓存显示与后台刷新.png"));
            waiting.SetResult(scan); await pending;
            Assert(!vm.IsShowingCache && !vm.IsScanning && vm.Rows.Count == 1 && vm.AutoSortCommand.CanExecute(null), "Fresh data did not replace cache");
            Assert(store.TryLoad(paths)!.Configuration.Order.PackageIds.Count == 1, "Fresh snapshot not persisted");
            Assert(freshBytes.SequenceEqual(File.ReadAllBytes(file)), "Cache refresh wrote game config");
        }
        finally { window.ViewModel.MarkExported(); window.Close(); }

        Assert(store.TrySave(paths, cachedConfig, scan), "Could not reset cache");
        window = new MainWindow(initializePaths: false) { Owner = owner };
        try
        {
            window.Show();
            var entered = new TaskCompletionSource();
            var waiting = new TaskCompletionSource<ModScanResult>();
            var pending = window.CachedStartupForCheckAsync(paths, store, scanner: (_, _, _) => { entered.SetResult(); return waiting.Task; });
            await entered.Task;
            var vm = window.ViewModel;
            vm.ReplaceFileUndoable(OrderFileDocument.FromConfiguration(cachedConfig), "拖入的排序.xml");
            Assert(!window.ShouldReadStartupOrder, "Imported document still marked for startup game order reload");
            vm.SelectedRow = vm.Rows[1]; vm.MoveUpCommand.Execute(null);
            var edited = vm.Rows.Select(row => row.PackageId).ToArray();
            var action = vm.LastAction;
            waiting.SetResult(scan); await pending;
            Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(edited) && vm.HasChanges && vm.LastAction == action
                && vm.UndoCommand.CanExecute(null) && vm.Status.Contains("已保留当前编辑") && !vm.IsShowingCache, "Background refresh erased startup edits");
            Assert(store.TryLoad(paths)!.Configuration.Order.PackageIds.Count == 1, "Cache persisted unsaved edits instead of actual game order");
            vm.UndoCommand.Execute(null);
            Assert(vm.Rows[0].PackageId == "test.cache", "Cache edit undo failed");
        }
        finally { window.ViewModel.MarkExported(); window.Close(); }

        foreach (var cancel in new[] { false, true })
        {
            Assert(store.TrySave(paths, cachedConfig, scan), "Could not reset cache");
            window = new MainWindow(initializePaths: false) { Owner = owner };
            try
            {
                var entered = new TaskCompletionSource();
                var waiting = new TaskCompletionSource<ModScanResult>();
                var pending = window.CachedStartupForCheckAsync(paths, store, scanner: (_, _, _) => { entered.SetResult(); return waiting.Task; });
                await entered.Task;
                if (cancel) { window.CancelScanForCheck(); waiting.SetResult(scan); }
                else waiting.SetException(new IOException("受控刷新失败"));
                await pending;
                Assert(window.ViewModel.IsShowingCache && window.ViewModel.Rows.Count == 2 && !window.ViewModel.IsScanning, "Failed or cancelled refresh erased cache");
            }
            finally { window.Close(); }
        }

        File.WriteAllText(store.CachePath, "broken");
        window = new MainWindow(initializePaths: false) { Owner = owner };
        try
        {
            await window.CachedStartupForCheckAsync(paths, store);
            Assert(!window.ViewModel.IsShowingCache && window.ViewModel.Rows.Count == 1 && store.TryLoad(paths) is not null, "Corrupt cache blocked startup or was not rebuilt");
        }
        finally { window.Close(); }
        Assert(freshBytes.SequenceEqual(File.ReadAllBytes(file)), "Cache checks changed real fixture config");
        File.WriteAllText(Path.Combine(output, "startup-cache-result.json"), JsonSerializer.Serialize(new
        { Passed = true, CachedDisplayMs = firstDisplayMs, Note = "Small controlled fixture; real refresh deliberately held pending; not a real library speed claim" }));
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
