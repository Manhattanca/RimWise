using RimWise.Core;
using RimWise.Integration.AI;

internal static class DuplicateInstallationChecks
{
    internal static void Run(Action<string, Action> check)
    {
        var version = new GameVersion(1, 7);
        ModInstallation Mod(string folder, ModSource source, string id = "shared.mod", params ModDependency[] dependencies) =>
            new(Path.Combine(AppContext.BaseDirectory, "重复安装 合成", folder), folder, source,
                source == ModSource.Workshop ? "123" : "",
                new("样例 " + folder, "test", id, ["1.7"], "", new(dependencies, [], [], []), false), []);
        var local = Mod("本地", ModSource.Local);
        var steam = Mod("工坊", ModSource.Workshop);
        void Assert(bool value) { if (!value) throw new Exception("Duplicate installation regression"); }

        check("Local and Workshop copies resolve by game identifier in either scan order", () =>
        {
            foreach (var entries in new[] { new[] { local, steam }, new[] { steam, local } })
            {
                var catalogue = new ModCatalogue(entries);
                Assert(catalogue.Resolve("SHARED.MOD", version).UniqueInstallation == local);
                Assert(catalogue.Resolve("shared.mod_STEAM", version).UniqueInstallation == steam);
                Assert(catalogue.IdentifierFor(steam, version) == "shared.mod_steam");
                Assert(catalogue.IdentifierFor(local, version) == "shared.mod");
                Assert(DuplicateInstallations.FindRequired(catalogue, ["shared.mod_steam"], version).Count == 0);
            }
        });
        check("Workshop-only base ID and existing Steam alias remain readable", () =>
        {
            var catalogue = new ModCatalogue([steam]);
            Assert(catalogue.Resolve("shared.mod", version).UniqueInstallation == steam);
            Assert(catalogue.Resolve("shared.mod_steam", version).UniqueInstallation == steam);
            Assert(catalogue.IdentifierFor(steam, version) == "shared.mod");
        });
        check("Multiple copies from the same source stay ambiguous and expose paths", () =>
        {
            foreach (var source in new[] { ModSource.Local, ModSource.Workshop })
            {
                var mods = new[] { Mod("副本一", source), Mod("副本二", source) };
                var catalogue = new ModCatalogue(mods);
                var matches = DuplicateInstallations.FindRequired(catalogue, ["shared.mod"], version);
                Assert(matches.Count == 1 && matches[0].UniqueInstallation is null);
                var description = DuplicateInstallations.Describe(matches[0]);
                Assert(mods.All(mod => description.Contains(mod.Name) && description.Contains(mod.Directory)));
                try { MissingModResolution.Find(catalogue, ["shared.mod"], [], version); }
                catch (InvalidDataException ex) when (ex.Message.Contains(mods[0].Directory)) { continue; }
                throw new Exception("Ambiguous installation treated as missing or silently selected");
            }
        });
        check("Multiple Workshop copies remain ambiguous under Steam alias", () =>
        {
            var catalogue = new ModCatalogue([local, steam, Mod("另一个工坊", ModSource.Workshop)]);
            Assert(catalogue.Resolve("shared.mod", version).UniqueInstallation == local);
            Assert(catalogue.Resolve("shared.mod_steam", version).Candidates.Count == 2);
        });
        check("An exact package ID ending in Steam suffix takes precedence over an alias", () =>
        {
            var exact = Mod("确切包名", ModSource.Local, "shared.mod_steam");
            var catalogue = new ModCatalogue([steam, exact]);
            Assert(catalogue.Resolve("shared.mod_steam", version).UniqueInstallation == exact);
        });
        check("Legacy folder identifiers remain unchanged", () =>
        {
            var catalogue = new ModCatalogue([local, steam]);
            var legacy = new GameVersion(1, 0);
            Assert(catalogue.Resolve(local.FolderName, legacy).UniqueInstallation == local);
            Assert(catalogue.Resolve(steam.FolderName, legacy).UniqueInstallation == steam);
            Assert(catalogue.IdentifierFor(steam, legacy) == steam.FolderName);
            Assert(catalogue.Resolve("shared.mod", legacy).Candidates.Count == 0);
        });
        var owner = Mod("依赖方", ModSource.Local, "owner.mod", new ModDependency("shared.mod", "共享前置"));
        check("Required dependency uses local base unless Workshop copy is explicitly active", () =>
        {
            var catalogue = new ModCatalogue([local, steam, owner]);
            var expanded = AiOrderPlanner.ExpandRequiredOrder(["owner.mod"], catalogue, version);
            Assert(expanded.SequenceEqual(["owner.mod", "shared.mod"]));
            var chosen = AiOrderPlanner.ExpandRequiredOrder(["owner.mod", "shared.mod_steam"], catalogue, version);
            Assert(chosen.SequenceEqual(["owner.mod", "shared.mod_steam"]));
            var sorted = new LoadOrderSorter().Sort(catalogue, chosen, version, version);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(["shared.mod_steam", "owner.mod"]));
            var plan = AiOrderPlanner.PreparedOrder(chosen, sorted.Order, catalogue, [], version, [], []);
            Assert(plan.Rejected.Count == 0 && plan.CanApply);
        });
        check("Preflight finds recursive ambiguous prerequisites before starting a task", () =>
        {
            var head = Mod("入口", ModSource.Local, "head.mod", new ModDependency("owner.mod", "中间前置"));
            var catalogue = new ModCatalogue([head, owner, local, Mod("第二本地", ModSource.Local)]);
            var duplicates = DuplicateInstallations.FindRequired(catalogue, ["head.mod"], version);
            Assert(duplicates.Count == 1 && duplicates[0].Identifier == "shared.mod");
            Assert(DuplicateInstallations.FindRequired(catalogue, ["missing.mod"], version).Count == 0);
        });
        check("Selected Workshop dependency is retained despite inactive local duplicates", () =>
        {
            var catalogue = new ModCatalogue([owner, local, steam, Mod("第二本地", ModSource.Local)]);
            Assert(DuplicateInstallations.FindRequired(catalogue, ["owner.mod", "shared.mod_steam"], version).Count == 0);
            Assert(AiOrderPlanner.ExpandRequiredOrder(["owner.mod", "shared.mod_steam"], catalogue, version).Length == 2);
        });
        check("Normal local and Workshop coexistence no longer emits duplicate warning", () =>
        {
            Assert(!CatalogueAnalysis.Inspect(new([local, steam]), version).Any(d => d.Code == "DuplicateInstallation"));
            Assert(CatalogueAnalysis.Inspect(new([local, Mod("第二本地", ModSource.Local)]), version)
                .Any(d => d.Code == "DuplicateInstallation"));
        });
        check("Prerequisite cycles do not loop during duplicate preflight", () =>
        {
            var a = Mod("a", ModSource.Local, "a", new ModDependency("b", "B"));
            var b = Mod("b", ModSource.Local, "b", new ModDependency("a", "A"));
            Assert(DuplicateInstallations.FindRequired(new([a, b]), ["a"], version).Count == 0);
        });
    }
}
