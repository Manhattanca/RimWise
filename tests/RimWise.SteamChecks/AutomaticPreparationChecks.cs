using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;

internal static class AutomaticPreparationChecks
{
    private static readonly GameVersion Version = new(1, 7);
    private static ModInstallation Mod(string id, string[]? dependencies = null, string[]? incompatible = null) => new(
        Path.Combine(AppContext.BaseDirectory, "准备检查 中文", id), id, ModSource.Local, "",
        new(id, "test", id, ["1.7"], "", new((dependencies ?? []).Select(d => new ModDependency(d, d)).ToArray(),
            [], [], incompatible ?? []), false), []);
    private static void Assert(bool value) { if (!value) throw new Exception("Automatic preparation regression"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Unauthorized removal accepted");
    }
    internal static void Run(Action<string, Action> check)
    {
        check("Self dependency and self ordering do not produce impossible cycles", () =>
        {
            var self = Mod("test.self", ["TEST.SELF"]);
            self = self with { Metadata = self.Metadata! with { Requirements = self.Metadata.Requirements with
                { LoadBefore = ["test.self"], LoadAfter = ["TEST.SELF"] } } };
            var catalogue = new ModCatalogue([Mod("test.other"), self]);
            string[] order = ["test.other", "test.self"];
            var sorted = new LoadOrderSorter().Sort(catalogue, order, Version, Version, deferAdvisoryCycles: true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(order));
            Assert(sorted.Diagnostics.Any(d => d.Code == "SelfRequirementIgnored")
                && !sorted.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error));
            Assert(!new LoadOrderSorter().Sort(new([Mod("test.a", ["test.b"]), Mod("test.b", ["test.a"])]),
                ["test.a", "test.b"], Version, Version, deferAdvisoryCycles: true).Complete);
            var workshop = self with { Source = ModSource.Workshop, WorkshopId = "123", Directory = self.Directory + "-steam" };
            Assert(new LoadOrderSorter().Sort(new([self, workshop]), ["test.self_steam"], Version, Version, deferAdvisoryCycles: true).Complete);
        });
        check("Graph validation accepts a valid code preference without allowing arbitrary original moves", () =>
        {
            var catalogue = new ModCatalogue([Mod("old.a", ["old.b"]), Mod("old.b"), Mod("new.mod")]);
            var rules = InsertionScope.Rules([], InsertionScope.Capture(["old.a", "old.b"]));
            string[] target = ["old.b", "new.mod", "old.a"];
            Assert(new LoadOrderSorter().Sort(catalogue, target, Version, Version, rules, true).Changed);
            var plan = AiOrderPlanner.PreparedOrder(["old.a", "old.b", "new.mod"], target, catalogue,
                ["old.a", "old.b"], Version, rules, []);
            Assert(plan.CanApply && plan.Rejected.Count == 0 && plan.After.SequenceEqual(target));
            Assert(!new LoadOrderSorter().Validate(catalogue, ["old.a", "new.mod", "old.b"], Version, Version, rules, true).Complete);
            Assert(!new LoadOrderSorter().Validate(new([Mod("old.a"), Mod("old.b"), Mod("new.mod")]), target,
                Version, Version, rules, true).Complete);
        });
        check("Two hundred prepared graphs validate despite alternative code tie preferences", () =>
        {
            var random = new Random(241103);
            for (var round = 0; round < 200; round++)
            {
                string[] ids = ["old.a", "old.b", "old.c", "new.x", "new.y", "new.z"];
                var graphOrder = ids.OrderBy(_ => random.Next()).ToArray();
                var catalogue = new ModCatalogue(graphOrder.Select((id, i) => Mod(id,
                    graphOrder.Take(i).Where(_ => random.Next(4) == 0).ToArray())).ToArray());
                var rules = InsertionScope.Rules([], InsertionScope.Capture(ids.Take(3)));
                var sorted = new LoadOrderSorter().Sort(catalogue, ids, Version, Version, rules, true,
                    ids.OrderBy(_ => random.Next()).ToArray());
                Assert(sorted.Complete);
                Assert(new LoadOrderSorter().Validate(catalogue, sorted.Order, Version, Version, rules, true).Complete);
                Assert(AiOrderPlanner.PreparedOrder(ids, sorted.Order, catalogue, ids.Take(3).ToArray(), Version, rules, []).Rejected.Count == 0);
            }
        });
        check("Player retention override survives replay publication and insertion recovery", () =>
        {
            string[] order = ["test.a", "test.b", "test.child"];
            var catalogue = new ModCatalogue([Mod("test.a", incompatible: ["test.b"]), Mod("test.b"), Mod("test.child", ["test.a"])]);
            var conflict = DeclaredConflictResolution.Find(catalogue, order, order, Version)!;
            Assert(AutomaticRemovalChoice.Select(conflict) is null);
            Assert(!ConflictChoicePreview.Create(conflict, ["test.a"]).CanContinue);
            var preview = ConflictChoicePreview.Create(conflict, ["test.a"], true);
            Assert(preview.CanContinue && preview.RemovedIds.SequenceEqual(["test.a", "test.child"])
                && preview.RetentionOverrideIds.Length == 2);
            var approval = JsonSerializer.Deserialize<ConflictRemovalApproval>(JsonSerializer.Serialize(preview.Approve(conflict)))!;
            var remaining = DeclaredConflictResolution.Replay(catalogue, order, order, Version, [approval]);
            Assert(remaining.SequenceEqual(["test.b"]));
            Assert(AiOrderPlanner.PreparedOrder(order, remaining, catalogue, order, Version, [], [approval]).CanApply);
            Reject(() => DeclaredConflictResolution.Replay(catalogue, order, order, Version, [approval with { RetentionOverrideIds = [] }]));
            Reject(() => DeclaredConflictResolution.Replay(catalogue, order, order, Version, [approval with { RetentionOverrideIds = ["test.a"] }]));
            var scope = InsertionScope.Capture(order);
            InsertionScope.ValidateOrder(scope, remaining, approval.RemovedIds);
            Reject(() => InsertionScope.ValidateOrder(scope, remaining));
            new AiTaskCheckpoint(AiTaskKind.Maintenance, AiTaskPhase.Paused, new('A', 64), order, remaining, DateTimeOffset.UtcNow)
                { Limits = new() { InsertionBaselineJson = scope }, DeadlineUtc = DateTimeOffset.MaxValue,
                    ConflictApprovals = [approval] }.Validate();
        });
        check("Automatic retention conflicts ask the player; official content stays protected", () =>
        {
            string[] order = ["test.a", "test.b"];
            var catalogue = new ModCatalogue([Mod("test.a", incompatible: ["test.b"]), Mod("test.b")]);
            var conflict = DeclaredConflictResolution.Find(catalogue, order, order, Version)!;
            var root = Path.Combine(AppContext.BaseDirectory, "冲突确认 中文", Guid.NewGuid().ToString("N"));
            using var task = new AiTaskRecoveryStore(root).Create(new(AiTaskKind.Maintenance, AiTaskPhase.Prepared,
                new('A', 64), order, order, DateTimeOffset.UtcNow)
                { Limits = new() { ConfirmModRemoval = false }, DeadlineUtc = DateTimeOffset.MaxValue });
            var asked = 0;
            var approval = AutomaticGameMaintenance.ChooseAndRecordAsync(conflict, task, (_, _) =>
                { asked++; return Task.FromResult<string[]?>(["test.b"]); }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(asked == 1 && approval.RetentionOverrideIds.SequenceEqual(["test.b"]));
            var core = Mod("ludeon.rimworld") with { Source = ModSource.Game };
            conflict = DeclaredConflictResolution.Find(new([core, Mod("test.a", incompatible: ["ludeon.rimworld"])]),
                ["ludeon.rimworld", "test.a"], ["ludeon.rimworld"], Version)!;
            Assert(!conflict.Options.Single(o => o.Identifier == "ludeon.rimworld").CanOverrideRetention
                && !ConflictChoicePreview.Create(conflict, ["ludeon.rimworld"], true).CanContinue);
        });
    }
}
