#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Formats;
using RimWise.Integration.Cache;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;
using RimWise.Desktop.Services;

namespace RimWise.Desktop;

internal static class FastStartupSmokeCheck
{
    internal static async Task RunAsync(MainWindow owner, string output)
    {
        var root = Path.Combine(output, "快速启动 中文 [样例] &");
        var mods = Path.Combine(root, "Mods");
        var config = Path.Combine(root, "Config");
        Directory.CreateDirectory(config);
        foreach (var id in new[] { "quick.a", "quick.b", "quick.c" })
        {
            var about = Path.Combine(mods, id, "About");
            Directory.CreateDirectory(about);
            File.WriteAllText(Path.Combine(about, "About.xml"), $"<ModMetaData><name>{id}</name><packageId>{id}</packageId><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
            Directory.CreateDirectory(Path.Combine(mods, id, "Defs"));
            File.WriteAllText(Path.Combine(mods, id, "Defs", "Test.xml"), "<Defs/>");
        }
        var file = Path.Combine(config, "ModsConfig.xml");
        File.WriteAllText(file, "<ModsConfigData><version>1.7</version><activeMods><li>quick.a</li><li>quick.b</li></activeMods></ModsConfigData>");
        var original = File.ReadAllBytes(file);
        var paths = new GamePaths { ConfigDirectory = config, LocalModsDirectory = mods, TargetVersionOverride = "1.7" };
        var scanner = new ModScanner();
        var metadata = scanner.Scan(paths, inspectContent: false);
        var complete = scanner.InspectContent(metadata);
        foreach (var (warm, cancel) in new[] { (false, false), (true, false), (true, true) })
        {
            var window = new MainWindow(initializePaths: false) { Owner = owner };
            try
            {
                window.Show();
                var entered = new TaskCompletionSource();
                var waiting = new TaskCompletionSource<ModScanResult>();
                var sawCache = false;
                var pending = window.FastStartupForCheckAsync(paths, (_, _, _) =>
                {
                    sawCache = window.ViewModel.IsShowingCache;
                    if (warm) Assert(new AiSortingSession(window.ViewModel, UserStorage.DirectoryPath).ReadinessProblem is not null,
                        "Unverified cached metadata allowed automatic sorting.");
                    return Task.FromResult(metadata);
                }, (_, _) => { entered.SetResult(); return waiting.Task; }, loadCache: warm);
                await entered.Task;
                await pending.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(sawCache == warm, "Warm startup did not show its previous cache.");
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                var vm = window.ViewModel;
                Assert(!vm.IsScanning && vm.Rows.Count == 2 && vm.AvailableMods.Count == 1
                    && vm.AvailableMods[0].CodeKind == (warm ? ModCodeKind.Xml : ModCodeKind.Unknown), "First list waited for content or discarded cached type.");
                Assert(!window.HasUpdateBlockingWork && vm.AutoSortCommand.CanExecute(null)
                    && window.RefreshButton.Content as string == "刷新", "Optional inspection blocked updates or editor commands.");
                Assert(vm.IsEnrichingContent && new AiSortingSession(vm, UserStorage.DirectoryPath).ReadinessProblem is null,
                    "Verified metadata still blocked automatic sorting during optional content inspection.");
                var available = vm.AvailableMods[0];
                vm.SelectedRow = vm.Rows[1];
                vm.MoveUpCommand.Execute(null);
                vm.SelectedAvailable = available;
                vm.SearchText = "quick";
                var edited = vm.Rows.Select(row => row.PackageId).ToArray();
                var action = vm.LastAction;
                vm.Status = "用户正在编辑排序";
                var details = window.ContentEnrichmentForCheck;
                var preparing = cancel ? window.PrepareAutomaticCatalogueAsync() : Task.CompletedTask;
                await preparing.WaitAsync(TimeSpan.FromSeconds(3));
                if (cancel) Assert(!vm.IsEnrichingContent, "Automatic preparation waited on optional inspection.");
                waiting.SetResult(complete);
                await details.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(!vm.IsScanning && !vm.IsEnrichingContent, "Content inspection state was not reset.");
                Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(edited) && vm.HasChanges && vm.LastAction == action
                    && vm.UndoCommand.CanExecute(null) && vm.SearchText == "quick", "Content scan erased intervening edits.");
                Assert(ReferenceEquals(vm.AvailableMods[0], available) && ReferenceEquals(vm.SelectedAvailable, available), "Content scan replaced selection or row identity.");
                Assert(available.CodeKind == ModCodeKind.Xml && vm.Status == "用户正在编辑排序", "Cached type or user status changed after optional completion/cancellation.");
                if (!cancel)
                {
                    var cache = new StartupCacheStore(UserStorage.DirectoryPath).TryLoad(paths)!;
                    Assert(cache.Configuration.Order.PackageIds.SequenceEqual(new[] { "quick.a", "quick.b" }), "Cache saved unsaved editor order.");
                    Assert(available.CodeKindIcon == "XML", "XML badge not explicit.");
                    PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "快速加载后保留编辑.png"));
                }
            }
            finally { window.ViewModel.MarkExported(); window.Close(); }
        }
        await CheckBackgroundIsolationAsync(owner, output, paths, metadata, complete);
        Assert(original.SequenceEqual(File.ReadAllBytes(file)), "Fast startup wrote game configuration.");
        File.WriteAllText(Path.Combine(output, "fast-startup.json"), JsonSerializer.Serialize(new { Passed = true,
            Checks = new[] { "cold and warm list before content I/O", "unverified cache blocks sorting; fresh metadata permits preparation", "automatic preparation invalidates optional inspection without waiting", "edits/search/selection/undo/status survive enrichment", "warm content type retained", "updates and commands not blocked by enrichment", "cache excludes unsaved edits", "new refresh/identity change/close discard stale results", "real content type change refreshed and cached", "partial inspection retains last known type", "game files unchanged" } }));
    }

    private static async Task CheckBackgroundIsolationAsync(MainWindow owner, string output, GamePaths paths,
        ModScanResult metadata, ModScanResult complete)
    {
        var store = new StartupCacheStore(UserStorage.DirectoryPath);
        var config = new PrototypeFileService().Read(CurrentOrderLocation.Resolve(paths));
        Assert(store.TrySave(paths, config, complete), "Could not seed background snapshot.");
        var window = new MainWindow(initializePaths: false) { Owner = owner };
        try
        {
            window.Show();
            var entered = new TaskCompletionSource();
            var stale = new TaskCompletionSource<ModScanResult>();
            await window.FastStartupForCheckAsync(paths, (_, _, _) => Task.FromResult(metadata),
                (_, _) => { entered.TrySetResult(); return stale.Task; }, loadCache: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var oldDetails = window.ContentEnrichmentForCheck;
            var vm = window.ViewModel;
            Assert(!window.HasUpdateBlockingWork, "Background startup remained an update blocker.");
            var nextEntered = new TaskCompletionSource();
            var nextDetails = new TaskCompletionSource<ModScanResult>();
            // Fresh metadata must win even while we reuse ONLY the old content badges.
            var fresh = metadata with { Catalogue = new(metadata.Catalogue.Installations.Select(mod => mod with
                { Metadata = mod.Metadata! with { Name = "新名称", ReleaseVersion = "9.0",
                    Requirements = new([], ["fresh.rule"], [], []) } }).ToArray()) };
            await window.FastStartupForCheckAsync(paths, (_, _, _) => Task.FromResult(fresh),
                (_, _) => { nextEntered.TrySetResult(); return nextDetails.Task; }).WaitAsync(TimeSpan.FromSeconds(3));
            await nextEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var newDetailsTask = window.ContentEnrichmentForCheck;
            Assert(vm.CurrentCatalogue!.Installations.All(mod => mod.Name == "新名称" && mod.Metadata!.ReleaseVersion == "9.0"
                && mod.Metadata.Requirements.LoadBefore.SequenceEqual(["fresh.rule"]) && mod.CodeKind == ModCodeKind.Xml),
                "Cached content replaced fresh metadata, or lost its cached badge.");
            var active = vm.CurrentCatalogue;
            stale.SetResult(complete);
            await oldDetails.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(ReferenceEquals(active, vm.CurrentCatalogue) && vm.IsEnrichingContent, "Old completion replaced the newer scan/state.");
            vm.Status = "另一个任务的提示";
            var changed = fresh with { Catalogue = new(fresh.Catalogue.Installations.Select(mod => mod with
                { CodeKind = ModCodeKind.CSharp, ContentKinds = ModContentKinds.ManagedCode,
                    LastUpdatedUtc = DateTimeOffset.UnixEpoch.AddDays(20) }).ToArray()) };
            nextDetails.SetResult(changed);
            await newDetailsTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(vm.CurrentCatalogue!.Installations.All(mod => mod.CodeKind == ModCodeKind.CSharp)
                && vm.Rows.All(row => row.Match!.UniqueInstallation!.CodeKind == ModCodeKind.CSharp)
                && vm.Status == "另一个任务的提示", "Real type change was not applied silently to both lists.");
            Assert(store.TryLoad(paths)!.Scan.Catalogue.Installations.All(mod => mod.CodeKind == ModCodeKind.CSharp),
                "Updated type was not persisted.");

            // A partial/unreadable scan must not turn a known DLL badge into Unknown.
            await window.FastStartupForCheckAsync(paths, (_, _, _) => Task.FromResult(metadata),
                (_, _) => Task.FromResult(metadata));
            await window.ContentEnrichmentForCheck.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(vm.CurrentCatalogue!.Installations.All(mod => mod.CodeKind == ModCodeKind.CSharp), "Partial inspection erased cached types.");

            // A catalogue changed by a workshop/import operation cannot be overwritten by the old scan.
            entered = new TaskCompletionSource();
            stale = new TaskCompletionSource<ModScanResult>();
            await window.FastStartupForCheckAsync(paths, (_, _, _) => Task.FromResult(metadata),
                (_, _) => { entered.TrySetResult(); return stale.Task; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            oldDetails = window.ContentEnrichmentForCheck;
            vm.ApplyScan(changed);
            vm.Status = "已导入排序";
            var beforeCache = File.ReadAllBytes(store.CachePath);
            stale.SetResult(complete);
            await oldDetails.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(ReferenceEquals(vm.CurrentCatalogue, changed.Catalogue) && vm.Status == "已导入排序"
                && beforeCache.SequenceEqual(File.ReadAllBytes(store.CachePath)), "Stale scan overwrote imported catalogue or cache.");

            // Reused folder with a different package is a different mod, not a cache hit.
            var replacement = metadata with { Catalogue = new(metadata.Catalogue.Installations.Select(mod => mod with
                { Metadata = mod.Metadata! with { PackageId = "replacement." + mod.PackageId } }).ToArray()) };
            entered = new TaskCompletionSource();
            stale = new TaskCompletionSource<ModScanResult>();
            await window.FastStartupForCheckAsync(paths, (_, _, _) => Task.FromResult(replacement),
                (_, _) => { entered.TrySetResult(); return stale.Task; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            oldDetails = window.ContentEnrichmentForCheck;
            Assert(vm.CurrentCatalogue!.Installations.All(mod => mod.CodeKind == ModCodeKind.Unknown), "Replaced package inherited another mod's cached type.");
            beforeCache = File.ReadAllBytes(store.CachePath);
            window.Close();
            stale.SetException(new IOException("关闭后才返回的受控读取错误"));
            await oldDetails.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(!vm.IsEnrichingContent && beforeCache.SequenceEqual(File.ReadAllBytes(store.CachePath)), "Closed window published a late result.");
        }
        finally { window.ViewModel.MarkExported(); if (window.IsVisible) window.Close(); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
