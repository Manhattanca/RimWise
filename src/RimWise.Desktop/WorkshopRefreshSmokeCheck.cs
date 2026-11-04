#if DEBUG
using System.IO;
using RimWise.Formats;
using RimWise.Integration.Cache;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class WorkshopRefreshSmokeCheck
{
    internal static async Task RunAsync(MainWindow owner, string output, List<string> checks)
    {
        var root = Path.Combine(output, "工坊刷新 fixtures");
        var workshop = Path.Combine(root, "Workshop");
        var local = Path.Combine(root, "Local");
        var config = Path.Combine(root, "Config");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(Path.Combine(local, "旧格式目录"));
        string About(string id) => Path.Combine(workshop, id, "About", "About.xml");
        void Write(string id, string body)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(About(id))!);
            File.WriteAllText(About(id), body);
        }
        string Metadata(string id) => $"<ModMetaData><name>模组 {id}</name><packageId>fixture.mod{id}</packageId></ModMetaData>";
        foreach (var id in new[] { "100", "200", "300", "400", "500", "600", "700" }) Write(id, Metadata(id));
        var paths = new GamePaths { WorkshopDirectory = workshop, LocalModsDirectory = local,
            ConfigDirectory = config, TargetVersionOverride = "1.7" };
        var file = Path.Combine(config, "ModsConfig.xml");
        File.WriteAllText(file, "<ModsConfigData><version>1.7</version><activeMods><li>fixture.mod100</li><li>fixture.mod400</li></activeMods></ModsConfigData>");
        var original = File.ReadAllBytes(file);
        var document = new PrototypeFileService().Read(file);
        var scanner = new ModScanner();
        var cached = scanner.Scan(paths, inspectContent: false);
        var cache = new StartupCacheStore(Path.Combine(root, "缓存"));
        Require(cache.TrySave(paths, document, cached), "Cache seed failed");

        // Reproduce removed metadata, empty About, missing item directory and retained assets.
        foreach (var id in new[] { "100", "200", "300", "600" }) File.Delete(About(id));
        Directory.Delete(Path.GetDirectoryName(About("200"))!);
        Directory.Delete(Path.GetDirectoryName(About("600"))!);
        Directory.Delete(Path.Combine(workshop, "600"));
        File.WriteAllText(Path.Combine(workshop, "300", "leftover.txt"), "residual content");
        Write("500", "<broken");
        File.Move(About("700"), About("700") + ".disabled");
        foreach (var inspect in new[] { false, true })
        {
            var fresh = scanner.Scan(paths, inspectContent: inspect);
            var ids = fresh.Catalogue.Installations.Select(mod => mod.WorkshopId).ToArray();
            Require(!ids.Intersect(new[] { "100", "200", "300", "600" }).Any(), "Removed item remains installed");
            Require(ids.Contains("400") && ids.Contains("500") && ids.Contains("700"), "Valid or diagnosed metadata was hidden");
            Require(fresh.Catalogue.Installations.Any(mod => mod.Directory == Path.Combine(local, "旧格式目录")), "Local legacy handling changed");
            Require(fresh.Diagnostics.Count(issue => issue.Code == "MetadataUnreadable") == 3, "Disabled/malformed/local diagnostics lost");
        }
        checks.Add("Full and metadata-only scans omit absent Workshop metadata; valid, malformed, disabled and local legacy entries retain their handling");
        try
        {
            scanner.ReadWorkshopItems(workshop, ["100"], cached.TargetVersion);
            throw new InvalidOperationException("Incremental scan published an incomplete download");
        }
        catch (InvalidDataException ex) { Require(ex.Message.Contains("100"), "Missing item not identified"); }
        checks.Add("Incremental downloaded-item scan rejects incomplete or removed Workshop items");

        var window = new MainWindow(initializePaths: false) { Owner = owner };
        try
        {
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource<ModScanResult>();
            var pending = window.CachedStartupForCheckAsync(paths, cache, scanner: (_, _, _) => { entered.SetResult(); return release.Task; });
            await entered.Task;
            Require(window.ViewModel.IsShowingCache && window.ViewModel.AvailableMods.Any(row => row.Installation.WorkshopId == "200"), "Stale cache not seeded");
            release.SetResult(scanner.Scan(paths));
            await pending;
            var vm = window.ViewModel;
            Require(!vm.IsShowingCache && vm.AvailableMods.All(row => !new[] { "100", "200", "300", "600" }.Contains(row.Installation.WorkshopId)), "Refresh retained ghosts");
            Require(vm.Rows.Select(row => row.PackageId).SequenceEqual(document.Order.PackageIds)
                && vm.Rows[0].Match?.Candidates.Count == 0 && vm.Rows[0].HasError, "Active missing item was deleted or still installed");
            Require(cache.TryLoad(paths)!.Scan.Catalogue.Installations.All(mod => mod.WorkshopId != "200"), "Ghost persisted to cache");
            Require(original.SequenceEqual(File.ReadAllBytes(file)) && !vm.HasChanges, "Refresh wrote personal order");
            checks.Add("Startup cache refresh removes ghosts, preserves missing active rows and game file, and replaces the stored catalogue");

            Write("100", Metadata("100"));
            Write("200", Metadata("200"));
            vm.ApplyScan(scanner.Scan(paths));
            Require(vm.Rows[0].Match?.Candidates.Count == 1 && vm.AvailableMods.Count(row => row.Installation.WorkshopId == "200") == 1,
                "Downloaded-again item failed to reappear or duplicated");
            checks.Add("Downloaded-again Workshop entries restore matching and appear once");
        }
        finally { window.ViewModel.MarkExported(); window.Close(); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
#endif
