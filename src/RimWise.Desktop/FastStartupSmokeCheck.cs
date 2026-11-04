#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RimWise.Core;
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
                Assert(sawCache == warm, "Warm startup did not show its previous cache.");
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                var vm = window.ViewModel;
                Assert(vm.IsScanning && vm.Rows.Count == 2 && vm.AvailableMods.Count == 1
                    && vm.AvailableMods[0].CodeKind == ModCodeKind.Unknown && vm.Status.Contains("列表已载入"), "First list waited for deep content scan.");
                Assert(vm.IsEnrichingContent && new AiSortingSession(vm, UserStorage.DirectoryPath).ReadinessProblem is null,
                    "Verified metadata still blocked automatic sorting during optional content inspection.");
                var available = vm.AvailableMods[0];
                vm.SelectedRow = vm.Rows[1];
                vm.MoveUpCommand.Execute(null);
                vm.SelectedAvailable = available;
                vm.SearchText = "quick";
                var edited = vm.Rows.Select(row => row.PackageId).ToArray();
                var action = vm.LastAction;
                var preparing = cancel ? window.PrepareAutomaticCatalogueAsync() : Task.CompletedTask;
                if (cancel) Assert(!preparing.IsCompleted, "Automatic sorting did not wait for the cancelled inspector to drain.");
                waiting.SetResult(complete);
                await pending;
                await preparing;
                Assert(!vm.IsScanning && !vm.IsEnrichingContent, "Content inspection state was not reset.");
                Assert(vm.Rows.Select(row => row.PackageId).SequenceEqual(edited) && vm.HasChanges && vm.LastAction == action
                    && vm.UndoCommand.CanExecute(null) && vm.SearchText == "quick", "Content scan erased intervening edits.");
                Assert(ReferenceEquals(vm.AvailableMods[0], available) && ReferenceEquals(vm.SelectedAvailable, available), "Content scan replaced selection or row identity.");
                Assert(available.CodeKind == (cancel ? ModCodeKind.Unknown : ModCodeKind.Xml), "Cancellation or detail completion failed.");
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
        Assert(original.SequenceEqual(File.ReadAllBytes(file)), "Fast startup wrote game configuration.");
        File.WriteAllText(Path.Combine(output, "fast-startup.json"), JsonSerializer.Serialize(new { Passed = true,
            Checks = new[] { "cold and warm list before content I/O", "unverified cache blocks sorting; fresh metadata permits preparation", "automatic preparation cancels and drains enrichment", "edits/search/selection/undo survive enrichment", "cancel preserves list", "cache excludes unsaved edits", "game files unchanged" } }));
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
