using RimWise.Core;

internal static class SorterChecks
{
    internal static void Run(Action<string, Action> check)
    {
        check("automatic sorting defers advisory cycles while sorting unrelated rules and preserving identifiers", () =>
        {
            var catalogue = Catalogue(
                Mod("A", "a.mod", requirements: new([], [], ["b.mod"], [])), Mod("B", "b.mod"),
                Mod("C", "c.mod", requirements: new([], [], ["b.mod"], [])), Mod("D", "d.mod"));
            CustomOrderRule[] rules = [new("a.mod", "b.mod"), new("d.mod", "c.mod")];
            string[] input = ["c.mod", "A.MOD", "b.mod", "d.mod"];
            var result = new LoadOrderSorter().Sort(catalogue, input, new(1,7), new(1,7), rules, deferAdvisoryCycles: true);
            Assert(result.Complete && result.Order.SequenceEqual(["A.MOD", "b.mod", "d.mod", "c.mod"]));
            Assert(result.Diagnostics.Where(d => d.Code == "OrderingCycleDeferred").Select(d => d.Identifier)
                .ToHashSet().SetEquals(["A.MOD", "b.mod"]));
            var repeated = new LoadOrderSorter().Sort(catalogue, result.Order, new(1,7), new(1,7), rules, deferAdvisoryCycles: true);
            Assert(repeated.Complete && !repeated.Changed);
            Assert(!new LoadOrderSorter().Sort(catalogue, input, new(1,7), new(1,7), rules).Complete);
        });
        check("required dependency overrides reverse advice even when the same edge is also advisory", () =>
        {
            var catalogue = Catalogue(Mod("Base", "base.mod"), Mod("Child", "child.mod",
                requirements: new([new("base.mod", "Base")], ["base.mod"], ["base.mod"], [])));
            var result = new LoadOrderSorter().Sort(catalogue, ["child.mod", "base.mod"], new(1,7), new(1,7), deferAdvisoryCycles: true);
            Assert(result.Complete && result.Order.SequenceEqual(["base.mod", "child.mod"]));
        });
        check("official content order survives cyclic external advice", () =>
        {
            var catalogue = Catalogue(Mod("Core", "ludeon.rimworld", ModSource.Game), Mod("A", "a.mod"));
            var result = new LoadOrderSorter().Sort(catalogue, ["a.mod", "ludeon.rimworld"], new(1,7), new(1,7),
                [new("a.mod", "ludeon.rimworld")], deferAdvisoryCycles: true);
            Assert(result.Complete && result.Order.SequenceEqual(["ludeon.rimworld", "a.mod"]));
        });
        check("mutual dependency cycles preserve members and order while missing prerequisites still block", () =>
        {
            var a = Mod("A", "a.mod", requirements: new([new("b.mod", "B")], [], [], []));
            var b = Mod("B", "b.mod", requirements: new([new("a.mod", "A")], [], [], []));
            var result = new LoadOrderSorter().Sort(Catalogue(a,b), ["a.mod", "b.mod"], new(1,7), new(1,7), deferAdvisoryCycles: true);
            Assert(result.Complete && !result.Changed && result.Order.SequenceEqual(["a.mod", "b.mod"])
                && result.DeferredCycles.Single().SequenceEqual(["a.mod", "b.mod"]));
            Assert(new LoadOrderSorter().Validate(Catalogue(a,b), ["b.mod", "a.mod"], new(1,7), new(1,7),
                deferAdvisoryCycles: true).Complete);
            Assert(!new LoadOrderSorter().Sort(Catalogue(a,b), ["a.mod", "b.mod"], new(1,7), new(1,7)).Complete);
            result = new LoadOrderSorter().Sort(Catalogue(a), ["a.mod"], new(1,7), new(1,7), deferAdvisoryCycles: true);
            Assert(!result.Complete && result.Diagnostics.Any(d => d.Code == "DependencyNotInstalled"));
        });
        check("cyclic mandatory rules remain blockers and code preferences cannot reverse a deferred group", () =>
        {
            var catalogue = Catalogue(Mod("甲", "a.mod"), Mod("乙", "b.mod"), Mod("丙", "c.mod"));
            CustomOrderRule[] cycle = [new("a.mod", "b.mod"), new("b.mod", "a.mod")];
            var sorted = new LoadOrderSorter().Sort(catalogue, ["a.mod", "b.mod", "c.mod"], new(1,7), new(1,7),
                cycle, deferAdvisoryCycles: true, preferredOrder: ["b.mod", "c.mod", "a.mod"]);
            Assert(sorted.Complete && Array.IndexOf(sorted.Order.ToArray(), "a.mod") < Array.IndexOf(sorted.Order.ToArray(), "b.mod"));
            Assert(sorted.Diagnostics.Where(d => d.Code == "OrderingCycleDeferred")
                .All(d => d.Message.Contains("甲") && !d.Message.Contains("a.mod")));
            Assert(!new LoadOrderSorter().Sort(catalogue, ["a.mod", "b.mod"], new(1,7), new(1,7),
                cycle.Select(r => r with { Required = true }).ToArray(), deferAdvisoryCycles: true).Complete);
        });
        check("independent advisory groups and self rules are deferred without labelling downstream rows", () =>
        {
            string[] ids = ["a.mod", "b.mod", "c.mod", "d.mod", "e.mod", "f.mod"];
            var catalogue = Catalogue(ids.Select(id => Mod(id,id,
                requirements: id == "e.mod" ? new([], [], [id], []) : ModRequirements.Empty)).ToArray());
            CustomOrderRule[] rules = [new("a.mod","b.mod"), new("b.mod","a.mod"), new("c.mod","d.mod"),
                new("d.mod","c.mod"), new("b.mod","f.mod")];
            var result = new LoadOrderSorter().Sort(catalogue, ids, new(1,7), new(1,7), rules, deferAdvisoryCycles: true);
            Assert(result.Complete && !result.Changed && result.Diagnostics.Count(d => d.Code == "OrderingCycleDeferred") == 5);
            Assert(result.Diagnostics.Where(d => d.Code == "OrderingCycleDeferred").All(d => d.Identifier != "f.mod"));
        });
        check("large advisory cycle is deferred without recursive traversal or loss of mods", () =>
        {
            const int count = 10000;
            var ids = Enumerable.Range(0,count).Select(i => "cycle."+i).ToArray();
            var catalogue = Catalogue(ids.Select((id,i) => Mod(id,id,requirements: new([],[],[ids[(i+1)%count]],[]))).ToArray());
            var result = new LoadOrderSorter().Sort(catalogue, ids, new(1,7), new(1,7), deferAdvisoryCycles: true);
            Assert(result.Complete && !result.Changed && result.Order.SequenceEqual(ids));
            Assert(result.Diagnostics.Count(d => d.Code == "OrderingCycleDeferred") == count);
            Assert(result.Diagnostics.Where(d => d.Code == "OrderingCycleDeferred").All(d => d.Message.Length < 1000));
        });
        check("mixed-case package references preserve exact identifiers and repeated sorting is stable", () =>
        {
            var catalogue=Catalogue(Mod("Core","Ludeon.RimWorld",source:ModSource.Game),
                Mod("Anomaly","Ludeon.RimWorld.Anomaly",source:ModSource.Game,
                    requirements:new([],[],["Ludeon.RimWorld"],[])),
                Mod("Odyssey","Ludeon.RimWorld.Odyssey",source:ModSource.Game,
                    requirements:new([],[],["Ludeon.RimWorld.Anomaly"],[])));
            string[] input=["Ludeon.RimWorld.Odyssey","ludeon.rimworld.anomaly","Ludeon.RimWorld"];
            var result=Sort(catalogue,input,new(1,7));
            Assert(result.Complete && result.Order.SequenceEqual(["Ludeon.RimWorld","ludeon.rimworld.anomaly","Ludeon.RimWorld.Odyssey"]));
            Assert(!Sort(catalogue,result.Order,new(1,7)).Changed);
        });
        check("game content precedes ordinary mods while early loader prerequisites remain early", () =>
        {
            var catalogue = Catalogue(
                Mod("Core", "ludeon.rimworld", source: ModSource.Game),
                Mod("Royalty", "ludeon.rimworld.royalty", source: ModSource.Game),
                Mod("Ideology", "ludeon.rimworld.ideology", source: ModSource.Game),
                Mod("Feature", "feature.mod"),
                Mod("Early", "early.mod", requirements: new([new("loader.base", "Base")], ["ludeon.rimworld"], [], [])),
                Mod("Base", "loader.base"));
            var input = new[] { "feature.mod", "ludeon.rimworld.ideology", "early.mod", "ludeon.rimworld.royalty", "loader.base", "ludeon.rimworld" };
            Assert(CatalogueAnalysis.InspectOrder(catalogue, input, new(1, 7)).Any(i => i.Code == "GameContentLoadsAfter"));
            var result = Sort(catalogue, input);
            Assert(result.Complete && result.Order.SequenceEqual(["loader.base", "early.mod", "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "feature.mod"]));
            Assert(!result.Diagnostics.Any(i => i.Code == "GameContentLoadsAfter"));
            Assert(!Sort(catalogue, result.Order).Changed);
            var conflicting = new LoadOrderSorter().Sort(catalogue, result.Order, new(1,7), new(1,7),
                [new("feature.mod", "ludeon.rimworld")]);
            Assert(!conflicting.Complete);
        });

        check("legacy Core anchor preserves folder identifiers and inactive content", () =>
        {
            var catalogue = Catalogue(Mod("Core", "", source: ModSource.Game, folder: "Core"),
                Mod("Feature", "", folder: "中文 模组"),
                Mod("Inactive", "", folder: "未启用"));
            var result = Sort(catalogue, ["中文 模组", "Core"], new(0, 18));
            Assert(result.Complete && result.Order.SequenceEqual(["Core", "中文 模组"]));
            Assert(!result.Diagnostics.Any(i => i.Code == "GameContentLoadsAfter"));
        });

        check("explicit DLC loader stays after Core and before its DLC", () =>
        {
            var catalogue = Catalogue(Mod("Core", "ludeon.rimworld", source: ModSource.Game),
                Mod("Royalty", "ludeon.rimworld.royalty", source: ModSource.Game),
                Mod("Loader", "dlc.loader", requirements: new([], ["ludeon.rimworld.royalty"], [], [])),
                Mod("Feature", "feature.mod"));
            var result = Sort(catalogue, ["feature.mod", "ludeon.rimworld.royalty", "dlc.loader", "ludeon.rimworld"]);
            Assert(result.Complete && result.Order.SequenceEqual(["ludeon.rimworld", "dlc.loader", "ludeon.rimworld.royalty", "feature.mod"]));
        });

        check("workshop package prefix cannot act as official content", () =>
        {
            var catalogue = Catalogue(Mod("Pretend DLC", "ludeon.rimworld.royalty", source: ModSource.Workshop),
                Mod("Feature", "feature.mod"));
            var input = new[] { "feature.mod", "ludeon.rimworld.royalty" };
            var result = Sort(catalogue, input);
            Assert(result.Complete && !result.Changed && result.Order.SequenceEqual(input));
        });

        check("stable dependency and declaration sort", () =>
        {
            var catalogue = Catalogue(
                Mod("a", "a.mod", requirements: new ModRequirements([], ["c.mod"], [], [])),
                Mod("b", "b.mod", requirements: new ModRequirements([], [], ["a.mod"], [])),
                Mod("c", "c.mod"));
            var result = Sort(catalogue, ["b.mod", "c.mod", "a.mod"]);
            Assert(result.Complete && result.Changed);
            Assert(result.Order.SequenceEqual(["a.mod", "b.mod", "c.mod"]));
            Assert(!result.Diagnostics.Any(issue => issue.Code is "LoadBeforeViolated" or "LoadAfterViolated" or "DependencyLoadsAfter"));
        });

        check("already valid order remains unchanged", () =>
        {
            var catalogue = Catalogue(Mod("a", "a.mod"), Mod("b", "b.mod"));
            var result = Sort(catalogue, ["a.mod", "b.mod"]);
            Assert(result.Complete && !result.Changed && result.Order.SequenceEqual(["a.mod", "b.mod"]));
        });

        check("unconstrained mods keep relative order", () =>
        {
            var catalogue = Catalogue(Mod("a", "a.mod"), Mod("b", "b.mod"), Mod("c", "c.mod"));
            var result = Sort(catalogue, ["c.mod", "a.mod", "b.mod"]);
            Assert(result.Complete && result.Order.SequenceEqual(["c.mod", "a.mod", "b.mod"]));
        });

        check("workshop steam alias resolves dependency", () =>
        {
            var catalogue = Catalogue(
                Mod("Framework", "framework.mod", source: ModSource.Workshop, folder: "100"),
                Mod("Feature", "feature.mod", requirements: new ModRequirements(
                    [new("framework.mod", "Framework")], [], [] , [])));
            var result = Sort(catalogue, ["feature.mod", "framework.mod_steam"]);
            Assert(result.Complete && result.Order.SequenceEqual(["framework.mod_steam", "feature.mod"]));
        });

        check("missing prerequisite blocks without adding a mod", () =>
        {
            var catalogue = Catalogue(Mod("Feature", "feature.mod", requirements: new ModRequirements(
                [new("missing.mod", "Missing")], [], [], [])));
            var order = new[] { "feature.mod" };
            var result = Sort(catalogue, order);
            Assert(!result.Complete && !result.Changed && result.Order.SequenceEqual(order));
            Assert(result.Diagnostics.Any(issue => issue.Code == "DependencyNotInstalled"));
        });

        check("ambiguous installation blocks without guessing", () =>
        {
            var catalogue = Catalogue(
                Mod("First", "same.mod", folder: "first"),
                Mod("Second", "same.mod", folder: "second"));
            var order = new[] { "same.mod" };
            var result = Sort(catalogue, order);
            Assert(!result.Complete && !result.Changed && result.Order.SequenceEqual(order));
            Assert(result.Diagnostics.Any(issue => issue.Code == "OrderModAmbiguous"));
        });

        check("duplicate entries block without rewriting", () =>
        {
            var catalogue = Catalogue(Mod("A", "a.mod"));
            var order = new[] { "a.mod", "a.mod" };
            var result = Sort(catalogue, order);
            Assert(!result.Complete && !result.Changed && result.Order.SequenceEqual(order));
        });

        check("cycle is reported and original order is retained", () =>
        {
            var catalogue = Catalogue(
                Mod("A", "a.mod", requirements: new ModRequirements([], [], ["b.mod"], [])),
                Mod("B", "b.mod", requirements: new ModRequirements([], [], ["a.mod"], [])));
            var order = new[] { "a.mod", "b.mod" };
            var result = Sort(catalogue, order);
            Assert(!result.Complete && !result.Changed && result.Order.SequenceEqual(order));
            Assert(result.Diagnostics.Any(issue => issue.Code == "OrderingCycle"));
        });

        check("legacy folder identifiers are sortable", () =>
        {
            var catalogue = Catalogue(
                Mod("Second", "", folder: "第二个", requirements: new ModRequirements([], [], ["第一个"], [])),
                Mod("First", "", folder: "第一个"));
            var result = Sort(catalogue, ["第二个", "第一个"], new GameVersion(0, 18));
            Assert(result.Complete && result.Order.SequenceEqual(["第一个", "第二个"]));
        });

        check("installed but inactive prerequisites do not get silently enabled", () =>
        {
            var catalogue = Catalogue(Mod("Base", "base.mod"), Mod("Feature", "feature.mod",
                requirements: new ModRequirements([new("base.mod", "Base")], [], [], [])));
            var result = Sort(catalogue, ["feature.mod"]);
            Assert(!result.Complete && !result.Changed && result.Order.SequenceEqual(["feature.mod"]));
            Assert(result.Diagnostics.Any(issue => issue.Code == "DependencyNotResolvedInOrder"));
        });

        check("cycle reasons exclude downstream mods and preserve original case", () =>
        {
            var catalogue = Catalogue(
                Mod("A", "a.mod", requirements: new ModRequirements([], [], ["b.mod"], [])),
                Mod("B", "b.mod", requirements: new ModRequirements([], [], ["a.mod"], [])),
                Mod("C", "c.mod", requirements: new ModRequirements([], [], ["b.mod"], [])));
            var result = Sort(catalogue, ["A.MOD", "b.mod", "c.mod"]);
            var cycles = result.Diagnostics.Where(issue => issue.Code == "OrderingCycle").ToArray();
            Assert(!result.Complete && result.Explanation.Contains("循环") && cycles.Length == 2);
            Assert(cycles.Any(issue => issue.Identifier == "A.MOD") && cycles.All(issue => issue.Identifier != "c.mod"));
        });

        check("self-referencing declarations stop instead of being ignored", () =>
        {
            var catalogue = Catalogue(Mod("Self", "self.mod", requirements: new ModRequirements([], [], ["self.mod"], [])));
            var result = Sort(catalogue, ["self.mod"]);
            Assert(!result.Complete && !result.Changed && result.Diagnostics.Any(issue => issue.Code == "OrderingCycle"));
        });

        check("unreadable enabled metadata blocks safe sorting", () =>
        {
            var broken = Mod("Broken", "broken.mod") with { Metadata = null,
                Diagnostics = [new("MetadataUnreadable", DiagnosticSeverity.Warning, "无法读取")], FolderName = "broken.mod" };
            var result = Sort(Catalogue(broken), ["broken.mod"]);
            Assert(!result.Complete && !result.Changed);
        });

        check("different game series blocks automatic interpretation", () =>
        {
            var result = new LoadOrderSorter().Sort(Catalogue(Mod("A", "a.mod")), ["a.mod"], new(1, 6), new(1, 7));
            Assert(!result.Complete && !result.Changed && result.Explanation.Contains("版本"));
        });

        check("unknown version with conditional requirements blocks sorting", () =>
        {
            var mod = Mod("Conditional", "conditional.mod");
            mod = mod with { Metadata = mod.Metadata! with { HasVersionedRequirements = true } };
            var result = Sort(Catalogue(mod), ["conditional.mod"]);
            Assert(!result.Complete && !result.Changed && result.Explanation.Contains("版本"));
        });

        check("large cycle detection does not depend on recursion depth", () =>
        {
            const int count = 10000;
            var mods = Enumerable.Range(0, count).Select(index => Mod($"mod{index}", $"test.{index}",
                requirements: new ModRequirements([], [], [$"test.{(index + 1) % count}"], []))).ToArray();
            var ids = Enumerable.Range(0, count).Select(index => $"test.{index}").ToArray();
            var result = Sort(Catalogue(mods), ids);
            Assert(!result.Complete && result.Order.SequenceEqual(ids)
                && result.Diagnostics.Count(issue => issue.Code == "OrderingCycle") == count);
            Assert(result.Explanation.Length < 1000);
        });
    }

    private static LoadOrderSortResult Sort(ModCatalogue catalogue, IReadOnlyList<string> order,
        GameVersion? version = null) => new LoadOrderSorter().Sort(catalogue, order, version, version);

    private static ModCatalogue Catalogue(params ModInstallation[] mods) => new(mods);

    private static ModInstallation Mod(string name, string packageId, ModSource source = ModSource.Local,
        string? folder = null, ModRequirements? requirements = null) => new(
        Path.Combine("synthetic-mods", folder ?? name), folder ?? name, source,
        source == ModSource.Workshop ? (folder ?? "") : "",
        new ModMetadata(name, "Test", packageId, ["1.7"], "", requirements ?? ModRequirements.Empty, false), []);

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }
}
