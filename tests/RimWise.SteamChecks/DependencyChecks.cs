using System.Text.Json;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Databases;
using RimWise.Integration.Game;
using RimWise.Integration.Scanning;
using RimWise.Integration.Steam;

internal static class DependencyChecks
{
    private static readonly GameVersion Version = new(1, 7);
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "dependency-fixtures", Guid.NewGuid().ToString("N"));
    private static ModInstallation Mod(string id, string workshop = "", params ModDependency[] dependencies) =>
        new(Path.Combine(Root, id), id, workshop.Length > 0 ? ModSource.Workshop : ModSource.Local, workshop,
            new(id + " 中文", "test", id, ["1.7"], "", new(dependencies, [], [], []), false), []);
    private static DatabaseSnapshot Database(params WorkshopEntry[] entries) => DatabaseParser.Create(
        new(1, new('a', 40), new('b', 64), new('c', 40), new('d', 64), [], entries) { RequiredItemsParsed = true });
    private static WorkshopEntry Entry(string id, string package, params string[] relations) =>
        new(id, package, ["1.7"], false, false) { Dependencies = relations };
    private static void Assert(bool condition, string message = "Dependency check failed")
    { if (!condition) throw new Exception(message); }

    internal static void Run(Action<string, Action> check)
    {
        check("Recovery verifies active downloads without reviving historical optional relations", () =>
        {
            var catalogue = new ModCatalogue([Mod("owner", "1", new ModDependency("base", "Base")), Mod("base", "2"), Mod("optional", "3")]);
            RequiredModDownload[] recorded = [new("base", "2"), new("optional", "3"),
                new("rimwise.workshop-required.3", "3") { DiscoverPackageId = true }];
            var expanded = AiOrderPlanner.ExpandRequiredOrder(["owner"], catalogue, Version);
            var active = WorkshopDependencyDownloads.ActiveInstalledDownloads(catalogue, expanded, Version, recorded).ToArray();
            Assert(active.Length == 1 && active[0].PackageId == "base" && recorded.Length == 3);
        });
        check("Workshop-only translation family never downloads or enables members", () =>
        {
            var db = Database(Entry("1", "translation", "2", "3", "999"), Entry("2", "optional.one"), Entry("3", "optional.two"));
            var owner = Mod("translation", "1") with { Metadata = Mod("translation").Metadata! with
                { Requirements = new([], ["optional.two"], ["optional.one"], []) } };
            var raw = new ModCatalogue([owner, Mod("optional.one", "2")]);
            var result = Prepare(raw, ["translation"], db, (_, _, _) => throw new Exception("Unexpected subscription"));
            Assert(result.Failures.Count == 0);
            Assert(AiOrderPlanner.ExpandRequiredOrder(["translation"], result.Catalogue, Version).SequenceEqual(["translation"]));
            Assert(raw.Resolve("translation", Version).UniqueInstallation!.Metadata!.Requirements.Dependencies.Count == 0);
        });
        check("Legacy database without required-item fields does not block local metadata", () =>
        {
            var db = Database(Entry("2", "base"));
            db = DatabaseParser.Create(db.Document with { RequiredItemsParsed = null });
            var result = db.WithRequiredDependencies(new([Mod("owner", "", new ModDependency("base", "Base"))]), ["owner"], Version);
            Assert(result.Resolve("owner", Version).UniqueInstallation!.Metadata!.Requirements.Dependencies.Single().WorkshopId == "2");
        });
        check("Declared recursive prerequisites download in waves and sort before parents", () =>
        {
            var db = Database(Entry("2", "middle", "99"), Entry("3", "base"), Entry("99", "optional"));
            var seen = new List<string>();
            var result = Prepare(new([Mod("owner", "", new ModDependency("middle", "Middle"))]), ["owner"], db, (source, pending, _) =>
            {
                var item = pending.Single(); seen.Add(item.Identifier);
                Assert(!item.DiscoverPackageId && item.Reason.Contains("About.xml"));
                Assert(item.Reason.Contains(item.Identifier == "middle" ? "[owner]" : "[middle]"));
                var installed = item.Identifier == "middle" ? Mod("middle", "2", new ModDependency("base", "Base")) : Mod("base", "3");
                return Task.FromResult(new AutomaticDownloadResult(new(source.Installations.Append(installed).ToArray()), new Dictionary<string, string>()));
            });
            var expanded = AiOrderPlanner.ExpandRequiredOrder(["owner"], result.Catalogue, Version);
            var sorted = new LoadOrderSorter().Sort(result.Catalogue, expanded, Version, Version);
            Assert(seen.SequenceEqual(["middle", "base"]) && sorted.Complete && sorted.Order.SequenceEqual(["base", "middle", "owner"]));
        });
        check("Installed DLC added from metadata without Steam subscription", () =>
        {
            var dlc = Mod("ludeon.rimworld.royalty") with { Source = ModSource.Game };
            var source = new ModCatalogue([Mod("owner", "", new ModDependency(dlc.PackageId, "Royalty")), dlc]);
            var result = Prepare(source, ["owner"], null, (_, _, _) => throw new Exception("DLC subscription"));
            var expanded = AiOrderPlanner.ExpandRequiredOrder(["owner"], result.Catalogue, Version);
            var reasons = RequiredDependencyReasons.Build(result.Catalogue, ["owner"], expanded, Version);
            Assert(expanded.Contains(dlc.PackageId) && reasons[dlc.PackageId].Contains("[owner]"));
        });
        check("Shared prerequisite shows every requesting mod and ignores inactive origins", () =>
        {
            var raw = new ModCatalogue([Mod("one", "1", new ModDependency("base", "Base", "4")),
                Mod("two", "2", new ModDependency("base", "Base", "4")), Mod("idle", "3", new ModDependency("base", "Wrong", "99"))]);
            var calls = 0;
            Prepare(raw, ["one_steam", "two"], null, (source, pending, _) =>
            {
                calls++; var item = pending.Single();
                Assert(item.WorkshopId == "4" && item.Reason.Contains("[one]") && item.Reason.Contains("[two]") && !item.Reason.Contains("idle"));
                return Task.FromResult(new AutomaticDownloadResult(new(source.Installations.Append(Mod("base", "4")).ToArray()), new Dictionary<string, string>()));
            });
            Assert(calls == 1);
        });
        check("Conflicting declared IDs never subscribe", () =>
        {
            var result = Prepare(new([Mod("one", "", new ModDependency("base", "Base", "4")), Mod("two", "", new ModDependency("base", "Base", "5"))]),
                ["one", "two"], null, (_, _, _) => throw new Exception("Ambiguous download"));
            Assert(result.Failures.ContainsKey("base"));
        });
        check("Known wrong package URL never subscribes", () =>
        {
            var db = Database(Entry("4", "unrelated"));
            var result = Prepare(new([Mod("owner", "", new ModDependency("base", "Base", "4"))]), ["owner"], db,
                (_, _, _) => throw new Exception("Wrong package subscribed"));
            Assert(result.Failures.ContainsKey("base"));
        });
        check("Ambiguous or outdated database candidates never auto subscribe", () =>
        {
            foreach (var db in new[] { Database(Entry("4", "base"), Entry("5", "base")),
                Database(Entry("4", "base") with { Versions = ["1.6"] }) })
            {
                var result = Prepare(new([Mod("owner", "", new ModDependency("base", "Base"))]), ["owner"], db,
                    (_, _, _) => throw new Exception("Unconfirmed automatic source"));
                Assert(result.Failures.ContainsKey("base"));
            }
        });
        check("Manual import still permits a unique older item with compatibility notice", () =>
        {
            var db = Database(Entry("4", "base") with { Versions = ["1.6"] });
            var plan = ImportedOrderDownloads.Plan(["base"], [], new([]), Version, db);
            Assert(plan.Downloads.Single().Notice.Length > 0);
        });
        check("Metadata version overrides and empty overrides exclude base/optional requirements", () =>
        {
            var directory = Path.Combine(Root, "中文 模组"); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "About.xml");
            new XElement("ModMetaData", new XElement("name", "测试"), new XElement("packageId", "owner"),
                new XElement("modDependencies", new XElement("li", new XElement("packageId", "old"))),
                new XElement("modDependenciesByVersion", new XElement("v1.7", new XElement("li", new XElement("packageId", "current"))), new XElement("v1.6")),
                new XElement("loadAfter", new XElement("li", "optional"))).Save(file);
            var reader = new ModMetadataReader();
            Assert(reader.Read(file, Root, Version).Requirements.Dependencies.Single().PackageId == "current");
            Assert(reader.Read(file, Root, new(1, 6)).Requirements.Dependencies.Count == 0);
        });
        check("Missing selected mod is labelled as original selection instead of invented prerequisite", () =>
        {
            var reasons = RequiredDependencyReasons.Build(new([]), ["missing"], ["missing"], Version);
            Assert(reasons["missing"].Contains("原排序") && !reasons["missing"].Contains("About.xml"));
        });
        check("Download failure retries are bounded while provenance remains visible", () =>
        {
            var calls = 0; IReadOnlyList<string> shown = [];
            var result = RequiredDependencyPreparation.RunAsync(new([Mod("owner", "", new ModDependency("base", "Base", "4"))]),
                ["owner"], [], Version, null, [], (source, pending, _) =>
                {
                    calls++; Assert(pending.Single().Reason.Contains("[owner]"));
                    return Task.FromResult(new AutomaticDownloadResult(source, new Dictionary<string, string> { ["base"] = "offline" }));
                }, default, lines => shown = lines).GetAwaiter().GetResult();
            Assert(calls == 1 && result.Failures["base"] == "offline" && shown.Single().Contains("[owner]"));
        });
        check("Download provenance survives recovery serialization without changing legacy record shape", () =>
        {
            var old = JsonSerializer.Deserialize<RequiredModDownload>("{\"PackageId\":\"base\",\"WorkshopId\":\"4\"}")!;
            Assert(old.Reason is null && !JsonSerializer.Serialize(old).Contains("Reason"));
            var fresh = old with { Reason = "base required by owner; About.xml" };
            Assert(JsonSerializer.Deserialize<RequiredModDownload>(JsonSerializer.Serialize(fresh)) == fresh);
        });
    }

    internal static void CheckOptionalMetadata(IEnumerable<string> files, Action<string, Action> check)
    {
        var database = new DatabaseStore(Root).Load().Snapshot;
        foreach (var file in files)
            check("Read-only optional metadata: " + Path.GetFullPath(file), () =>
            {
                var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file));
                var directory = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(file))!)!.FullName;
                var metadata = new ModMetadataReader().Read(file, directory, Version);
                var workshop = Path.GetFileName(directory);
                var mod = new ModInstallation(directory, workshop, ModSource.Workshop, workshop, metadata, []);
                Assert(metadata.Requirements.Dependencies.Count == 0, "Expected metadata without hard prerequisites");
                Assert(database.Document.Workshop.Single(e => e.Id == workshop).Dependencies is { Length: > 0 }, "Expected database-only relations");
                var result = Prepare(new([mod]), [mod.PackageId], database, (_, _, _) => throw new Exception("Unexpected real subscription"));
                Assert(result.Failures.Count == 0 && AiOrderPlanner.ExpandRequiredOrder([mod.PackageId], result.Catalogue, Version).SequenceEqual([mod.PackageId]));
                Assert(before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))), "Source metadata changed");
            });
    }

    private static AutomaticDownloadResult Prepare(ModCatalogue catalogue, string[] order, DatabaseSnapshot? database,
        Func<ModCatalogue, IReadOnlyList<ImportedModDownload>, CancellationToken, Task<AutomaticDownloadResult>> download) =>
        RequiredDependencyPreparation.RunAsync(catalogue, order, [], Version, database, [], download, default).GetAwaiter().GetResult();
}
