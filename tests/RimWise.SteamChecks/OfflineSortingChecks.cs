using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Databases;
using RimWise.Integration.Game;

internal static class OfflineSortingChecks
{
    internal static void Run(Action<string, Action> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "离线排序 中文", Guid.NewGuid().ToString("N"));
        var version = new GameVersion(1, 7);
        ModInstallation Mod(string id, string[]? deps = null, string[]? conflicts = null) => new(
            Path.Combine(root, id), id, ModSource.Local, "", new(id, "test", id, ["1.7"], "",
            new((deps ?? []).Select(d => new ModDependency(d, d)).ToArray(), [], [], conflicts ?? []), false), []);
        OfflineSortingResult Sort(ModCatalogue catalogue, string[] order, CustomOrderRule[]? rules = null) =>
            OfflineSorting.Sort(catalogue, order, order, version, rules ?? [], null, root, default);
        static void Assert(bool value) { if (!value) throw new Exception("Offline sorting regression"); }
        check("Offline adds installed transitive prerequisites and preserves protected mods", () =>
        {
            var result = Sort(new([Mod("child", ["parent"]), Mod("parent", ["base"]), Mod("base")]), ["child"]);
            Assert(result.Complete && result.Plan!.After.SequenceEqual(["base", "parent", "child"]));
            Assert(result.Plan!.Notices.Count > 0);
        });
        check("Offline reports every missing prerequisite without removal", () =>
        {
            var result = Sort(new([Mod("child", ["missing.one", "missing.two"])]), ["child"]);
            Assert(!result.Complete && result.Plan is null && result.Messages.Any(m => m.Contains("missing.one"))
                && result.Messages.Any(m => m.Contains("missing.two")));
        });
        check("Offline uses cached community rules while explicit user pairs take precedence", () =>
        {
            var database = new DatabaseSnapshot(new(1, new('a',40), new('b',64), new('c',40), new('d',64),
                [new("a", [], ["b"], [], false, false)], []), "synthetic");
            var catalogue = new ModCatalogue([Mod("a"), Mod("b")]);
            var result = OfflineSorting.Sort(catalogue, ["a", "b"], [], version, [], database, root, default);
            Assert(result.Complete && result.Plan!.After.SequenceEqual(["b", "a"]));
            result = OfflineSorting.Sort(catalogue, ["a", "b"], [], version, [new("a", "b")], database, root, default);
            Assert(result.Complete && result.Plan!.After.SequenceEqual(["a", "b"]));
        });
        check("Offline keeps declared conflicts and required cycles unresolved", () =>
        {
            Assert(!Sort(new([Mod("a", conflicts: ["b"]), Mod("b")]), ["a", "b"]).Complete);
            Assert(!Sort(new([Mod("a", ["b"]), Mod("b", ["a"])]), ["a", "b"]).Complete);
        });
        check("Offline respects user rules and defers advisory cycles without dropping mods", () =>
        {
            var catalogue = new ModCatalogue([Mod("a"), Mod("b")]);
            Assert(Sort(catalogue, ["a", "b"], [new("b", "a")]).Plan!.After.SequenceEqual(["b", "a"]));
            var result = Sort(catalogue, ["a", "b"], [new("a", "b"), new("b", "a")]);
            Assert(result.Complete && result.Plan!.After.Count == 2 && result.Messages.Count > 0);
        });
        check("Offline assembly preferences are soft, ambiguity and partial graphs retain order", () =>
        {
            IndexedAssembly Assembly(string id, string name, params string[] refs) => new(id, id, "", "", new(name, refs, []));
            var graph = new AssemblyIndexResult([Assembly("a", "Consumer", "Provider"), Assembly("b", "Provider")], false, 0);
            string[] order = ["a", "b"];
            var preference = LocalCodeTopology.PreferProviders(graph, order, default);
            Assert(preference.SequenceEqual(["b", "a"]));
            Assert(new LoadOrderSorter().Sort(new([Mod("a"), Mod("b")]), order, version, version,
                [new("a", "b")], true, preference).Order.SequenceEqual(order));
            Assert(LocalCodeTopology.PreferProviders(graph with { Limited = true }, order, default).SequenceEqual(order));
            Assert(LocalCodeTopology.PreferProviders(graph with { Assemblies = [.. graph.Assemblies, Assembly("c", "Provider")] },
                ["a", "b", "c"], default).SequenceEqual(["a", "b", "c"]));
        });
        check("Offline cancellation aborts before analysis", () =>
        {
            try { OfflineSorting.Sort(new([Mod("a")]), ["a"], [], version, [], null, root, new(true)); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Cancellation ignored");
        });
        check("Offline preference persists and retired AI opt-in migrates without losing configuration", () =>
        {
            var preferences = new AutomaticSortingPreferencesStore(root);
            preferences.SaveMode("offline", true);
            Assert(preferences.Load().Mode == "offline");
            var settings = new AiSettingsStore(root);
            Assert(settings.Load().Enabled && settings.Load().Endpoint == "");
            settings.Save(AiSettings.Default with { Model = "preserved-model" });
            File.WriteAllText(settings.SettingsPath, JsonSerializer.Serialize(new { SchemaVersion = 1,
                Settings = AiSettings.Default with { Enabled = false, Model = "preserved-model" } }));
            Assert(settings.Load().Enabled && settings.Load().Model == "preserved-model" && AiSettings.Default.Enabled
                && settings.Load().Endpoint == AiSettings.Default.Endpoint);
        });
    }
}
