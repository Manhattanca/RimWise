using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Databases;

internal static class InsertionOrderingChecks
{
    private static readonly GameVersion Version = new(1, 7);
    private static ModInstallation Mod(string id, params string[] dependencies) => new(
        Path.Combine(AppContext.BaseDirectory, "中文 模组", id), id, ModSource.Local, "",
        new(id, "test", id, ["1.7"], "", new(dependencies.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
    private static void Assert(bool value) { if (!value) throw new Exception("Insertion ordering regression"); }

    internal static void Run(Action<string, Action> check)
    {
        check("Insertion preference yields to a reversed required dependency and preserves unrelated rows", () =>
        {
            string[] baseline = ["old.a", "old.b", "unrelated.c", "unrelated.d"];
            var catalogue = new ModCatalogue([Mod("old.a", "old.b"), Mod("old.b"), Mod("unrelated.c"), Mod("unrelated.d"), Mod("new.mod")]);
            var rules = InsertionScope.Rules([], InsertionScope.Capture(baseline));
            var sorted = new LoadOrderSorter().Sort(catalogue, [..baseline, "new.mod"], Version, Version, rules, true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(["old.b", "old.a", "unrelated.c", "unrelated.d", "new.mod"]));
            Assert(sorted.Diagnostics.Any(d => d.Code == "InsertionOrderAdjusted"));
            var plan = AiOrderPlanner.PreparedOrder([..baseline, "new.mod"], sorted.Order, catalogue, baseline, Version, rules, []);
            Assert(plan.CanApply && plan.Notices.Any(n => n.Contains("old.a") && n.Contains("old.b")));
            var repeated = new LoadOrderSorter().Sort(catalogue, sorted.Order, Version, Version, rules, true);
            Assert(repeated.Complete && !repeated.Changed);
        });
        check("Insertion follows dependency paths through a new mod without disabling originals", () =>
        {
            string[] baseline = ["old.consumer", "old.provider", "unrelated.mod"];
            var catalogue = new ModCatalogue([Mod("old.consumer", "new.mod"), Mod("new.mod", "old.provider"), Mod("old.provider"), Mod("unrelated.mod")]);
            var rules = InsertionScope.Rules([], InsertionScope.Capture(baseline));
            var sorted = new LoadOrderSorter().Sort(catalogue, [..baseline, "new.mod"], Version, Version, rules, true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(["old.provider", "new.mod", "old.consumer", "unrelated.mod"]));
            InsertionScope.ValidateOrder(InsertionScope.Capture(baseline), sorted.Order);
        });
        check("Insertion obeys declared load-after while advisory contradictions remain deferrable", () =>
        {
            var a = Mod("old.a");
            a = a with { Metadata = a.Metadata! with { Requirements = new([], [], ["old.b"], []) } };
            var catalogue = new ModCatalogue([a, Mod("old.b")]);
            var rules = InsertionScope.Rules([], InsertionScope.Capture(["old.a", "old.b"]));
            var sorted = new LoadOrderSorter().Sort(catalogue, ["old.a", "old.b"], Version, Version, rules, true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(["old.b", "old.a"]));
            sorted = new LoadOrderSorter().Sort(catalogue, ["old.a", "old.b"], Version, Version,
                InsertionScope.Rules([new("old.a", "old.b")], InsertionScope.Capture(["old.a", "old.b"])), true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(["old.a", "old.b"])
                && sorted.Diagnostics.Any(d => d.Code == "OrderingCycleDeferred"));
        });
        check("Actual reciprocal required dependencies still fail instead of being ignored", () =>
        {
            var catalogue = new ModCatalogue([Mod("old.a", "old.b"), Mod("old.b", "old.a")]);
            var rules = InsertionScope.Rules([], InsertionScope.Capture(["old.a", "old.b"]));
            var sorted = new LoadOrderSorter().Sort(catalogue, ["old.a", "old.b"], Version, Version, rules, true);
            Assert(!sorted.Complete && sorted.Diagnostics.Any(d => d.Code == "OrderingCycle"));
        });
        check("Baseline preference does not hide a reverse community rule; explicit user override still works", () =>
        {
            var catalogue = new ModCatalogue([Mod("old.a"), Mod("old.b")]);
            var scope = InsertionScope.Capture(["old.a", "old.b"]);
            var db = DatabaseParser.Create(new(1, new('a', 40), new('b', 64), new('c', 40), new('d', 64),
                [new("old.b", ["old.a"], [], [], false, false)], []));
            var projected = db.Rules(catalogue, ["old.a", "old.b"], Version, InsertionScope.Rules([], scope));
            var sorted = new LoadOrderSorter().Sort(catalogue, ["old.a", "old.b"], Version, Version, projected, true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(["old.b", "old.a"]));
            Assert(projected.Any(r => r.Before == "old.b" && r.After == "old.a" && r.OriginalOrderIndex is null));
            projected = db.Rules(catalogue, ["old.a", "old.b"], Version, InsertionScope.Rules([new("old.a", "old.b")], scope));
            sorted = new LoadOrderSorter().Sort(catalogue, ["old.a", "old.b"], Version, Version, projected, true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(["old.a", "old.b"]));
        });
        check("No requirement means no original reorder, including code preferences and arbitrary model moves", () =>
        {
            string[] baseline = ["old.a", "old.b", "old.c"];
            var catalogue = new ModCatalogue(baseline.Select(id => Mod(id)).ToArray());
            var rules = InsertionScope.Rules([], InsertionScope.Capture(baseline));
            var sorted = new LoadOrderSorter().Sort(catalogue, baseline, Version, Version, rules, true, baseline.Reverse().ToArray());
            Assert(sorted.Complete && !sorted.Changed && !sorted.Diagnostics.Any(d => d.Code == "InsertionOrderAdjusted"));
            var plan = AiOrderPlanner.Plan([new("reorder", "old.c", 1, false, null, "unproven")],
                AiPermissionLevel.Level1OrderAndRequiredDependencies, baseline, catalogue, baseline, Version, rules);
            Assert(plan.Rejected.Count > 0 && !plan.CanApply);
        });
        check("Model may propose a requirement-backed move while original removal remains forbidden", () =>
        {
            string[] baseline = ["old.a", "old.b"];
            var catalogue = new ModCatalogue([Mod("old.a", "old.b"), Mod("old.b")]);
            var rules = InsertionScope.Rules([], InsertionScope.Capture(baseline));
            var plan = AiOrderPlanner.Plan([new("reorder", "old.b", 1, false, null, "required dependency")],
                AiPermissionLevel.Level1OrderAndRequiredDependencies, baseline, catalogue, baseline, Version, rules);
            Assert(plan.CanApply && plan.After.SequenceEqual(["old.b", "old.a"]));
            plan = AiOrderPlanner.Plan([new("remove", "old.a", null, false, null, "not allowed")],
                AiPermissionLevel.Level2ModManagement, baseline, catalogue, baseline, Version, rules);
            Assert(plan.Rejected.Count > 0);
        });
        check("Preference reprojection is idempotent, survives serialization and stays visible to the model", () =>
        {
            var scope = InsertionScope.Capture(["old.a", "old.b"]);
            var once = InsertionScope.Rules([], scope);
            var twice = InsertionScope.Rules(once, scope);
            Assert(twice.SequenceEqual(once) && once.Single().OriginalOrderIndex == 0 && !once.Single().Required);
            var saved = JsonSerializer.Deserialize<CustomOrderRule[]>(JsonSerializer.Serialize(once))!;
            Assert(saved.SequenceEqual(once));
            var request = new AiRequest(AiSettings.Default, null, "1.7", ["old.a", "old.b"], ["old.a", "old.b"], ["old.a", "old.b"], [])
                { OrderRules = saved };
            using var context = JsonDocument.Parse(AiPrompt.Messages(request)[1].Content);
            Assert(context.RootElement.GetProperty("OrderRules")[0].GetProperty("OriginalOrderIndex").GetInt32() == 0);
        });
        check("Workshop source aliases retain exact identifiers during a justified original reorder", () =>
        {
            var local = Mod("old.a");
            var workshop = Mod("old.a", "old.b") with { Source = ModSource.Workshop, Directory = Path.Combine(AppContext.BaseDirectory, "工坊 a"), WorkshopId = "123" };
            var catalogue = new ModCatalogue([local, workshop, Mod("old.b")]);
            string[] baseline = ["old.a_steam", "old.b"];
            var sorted = new LoadOrderSorter().Sort(catalogue, baseline, Version, Version, InsertionScope.Rules([], InsertionScope.Capture(baseline)), true);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(["old.b", "old.a_steam"]));
        });
        check("Five thousand originals retain stable order when one declared dependency requires a correction", () =>
        {
            var baseline = Enumerable.Range(0, 5000).Select(i => "old." + i).ToArray();
            var catalogue = new ModCatalogue(baseline.Select((id, i) => i == 2500 ? Mod(id, "old.2501") : Mod(id)).ToArray());
            var rules = InsertionScope.Rules([], InsertionScope.Capture(baseline));
            var sorted = new LoadOrderSorter().Sort(catalogue, baseline, Version, Version, rules, true);
            var expected = baseline.ToArray();
            (expected[2500], expected[2501]) = (expected[2501], expected[2500]);
            Assert(sorted.Complete && sorted.Order.SequenceEqual(expected));
        });
        check("Maintenance applies required original move, tests, persists success and publishes without AI", () =>
        {
            string[] baseline = ["old.a", "old.b"];
            string[] order = [..baseline, "new.mod"];
            var catalogue = new ModCatalogue([Mod("old.a", "old.b"), Mod("old.b"), Mod("new.mod")]);
            var scope = InsertionScope.Capture(baseline);
            var rules = InsertionScope.Rules([], scope);
            var settings = AiSettings.Default with { Enabled = true, Model = "test", Endpoint = "http://127.0.0.1/v1/",
                PermissionLevel = AiPermissionLevel.Level1OrderAndRequiredDependencies };
            var request = new AiRequest(settings, null, "1.7", order, order, baseline, []) { OrderRules = rules };
            var root = Path.Combine(AppContext.BaseDirectory, "新增 顺序维护", Guid.NewGuid().ToString("N"));
            var store = new AiTaskRecoveryStore(root);
            var adapter = new OrderingSession(catalogue, rules);
            var result = new LevelOneMaintenance(new NoCalls(), new(root), adapter)
                .RunAsync(request, catalogue, rules, new(2) { TokenLimitEnabled = false, InsertionBaselineJson = scope },
                    null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 1 && result.Requests == 0 && adapter.Applied && adapter.Finished);
            Assert(result.LastAttemptedOrder.SequenceEqual(["old.b", "old.a", "new.mod"]));
            using var task = store.Open(result.TaskId!);
            task.Current.Validate();
            Assert(task.Current.CandidatePendingApply && task.Current.Feedback!.Passed);
            var publication = AiOrderPlanner.PreparedOrder(order, result.LastAttemptedOrder, catalogue, baseline, Version, rules, []);
            Assert(publication.CanApply && publication.Rejected.Count == 0);
        });
    }

    private sealed class NoCalls : IAiClient
    {
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings s, string? k, CancellationToken t) => throw new Exception("Unexpected AI connection");
        public Task<AiClientResult> GetSuggestionAsync(AiRequest r, CancellationToken t) => throw new Exception("Unexpected AI request");
    }

    private sealed class OrderingSession(ModCatalogue catalogue, IReadOnlyList<CustomOrderRule> rules) : ILevelOneGameSession, IMaintenancePreparation
    {
        public bool Applied;
        public bool Finished;
        public Task<bool> VerifySafetyAsync(CancellationToken t) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken t) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken t)
        {
            Assert(order.SequenceEqual(["old.b", "old.a", "new.mod"]));
            return Task.FromResult(new MaintenanceGameReport(true, []));
        }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken t) { Assert(plan.CanApply); Applied = true; return Task.CompletedTask; }
        public Task FinishAsync(bool accept, CancellationToken t) { Finished = true; return Task.CompletedTask; }
        public string ContextHash(AiRequest r, ModCatalogue c, IReadOnlyList<CustomOrderRule> rs) => new('B', 64);
        public Task<MaintenancePreparation> PrepareAsync(AiRequest request, ModCatalogue c, AiTaskRecoveryStore.AiTaskLease task, CancellationToken t)
        {
            var sorted = new LoadOrderSorter().Sort(catalogue, request.CurrentOrder, Version, Version, rules, true);
            Assert(sorted.Complete);
            return Task.FromResult(new MaintenancePreparation(request, catalogue, sorted.Order) { Rules = rules });
        }
    }
}
