using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;
using RimWise.Integration.Configuration;

internal static class InsertionChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "新增模式 回归", Guid.NewGuid().ToString("N"));
    private static void Assert(bool value) { if (!value) throw new Exception("Insertion regression"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid insertion state accepted");
    }
    private static ModInstallation Mod(string id, params string[] dependencies) => new(Path.Combine(Root, id), id,
        ModSource.Local, "", new(id, "test", id, ["1.7"], "",
            new(dependencies.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);

    internal static void Run(Action<string, Action> check)
    {
        Directory.CreateDirectory(Root);
        var scope = InsertionScope.Capture(["test.a", "test.b"]);
        check("Insertion keeps originals, allows verified reordering, rejects omission and duplication", () =>
        {
            InsertionScope.ValidateOrder(scope, ["test.new", "TEST.A", "test.other", "test.b"]);
            InsertionScope.ValidateOrder(scope, ["test.b", "test.a"]);
            Reject(() => InsertionScope.ValidateOrder(scope, ["test.a"]));
            Reject(() => InsertionScope.ValidateOrder(scope, ["test.a", "test.b", "test.a"]));
        });
        check("Explicit ordering requirement overrides baseline; new dependency precedes consumer", () =>
        {
            var catalogue = new ModCatalogue([Mod("test.a"), Mod("test.b"), Mod("test.new", "test.dep"), Mod("test.dep")]);
            var rules = InsertionScope.Rules([new("test.b", "test.a")], scope);
            var result = new LoadOrderSorter().Sort(catalogue, ["test.a", "test.b", "test.new", "test.dep"], new(1,7), new(1,7),
                rules, deferAdvisoryCycles: true, preferredOrder: ["test.b", "test.dep", "test.new", "test.a"]);
            Assert(result.Complete && Array.IndexOf(result.Order.ToArray(), "test.b") < Array.IndexOf(result.Order.ToArray(), "test.a"));
            InsertionScope.ValidateOrder(scope, result.Order);
            Assert(Array.IndexOf(result.Order.ToArray(), "test.dep") < Array.IndexOf(result.Order.ToArray(), "test.new"));
        });
        check("Hard dependency overrides original order without removing originals", () =>
        {
            var result = new LoadOrderSorter().Sort(new([Mod("test.a", "test.b"), Mod("test.b")]),
                ["test.a", "test.b"], new(1,7), new(1,7), InsertionScope.Rules([], scope), deferAdvisoryCycles: true);
            Assert(result.Complete && result.Order.SequenceEqual(["test.b", "test.a"])
                && result.Diagnostics.Any(d => d.Code == "InsertionOrderAdjusted"));
        });
        check("Automatic removal respects protection and chooses minimum deterministic impact", () =>
        {
            var conflict = new DeclaredConflict("test.a", "test.b", "fixture",
                [new("test.a", "A", ["test.a"], "protected"), new("test.b", "B", ["test.b"], null)]);
            Assert(AutomaticRemovalChoice.Select(conflict)!.SequenceEqual(["test.b"]));
            Assert(AutomaticRemovalChoice.Select(conflict with { Options = [conflict.Options[0]] }) is null);
            conflict = conflict with { Options = [new("test.b", "B", ["test.b"], null), new("test.a", "A", ["test.a"], null)] };
            Assert(AutomaticRemovalChoice.Select(conflict)!.SequenceEqual(["test.a"]));
            conflict = conflict with { MissingIds = ["test.a", "test.b"] };
            Assert(AutomaticRemovalChoice.Select(conflict)!.SequenceEqual(["test.a", "test.b"]));
        });
        check("Existing error comparison rejects new blocks, greater counts and incomplete runs", () =>
        {
            var baseline = new InsertionEvidence(true, new() { [new('A', 64)] = 2 }, []);
            Assert(InsertionEvidence.Matches(baseline, baseline with { Errors = new() { [new('A', 64)] = 1 } }));
            Assert(!InsertionEvidence.Matches(baseline, baseline with { Completed = false }));
            Assert(!InsertionEvidence.Matches(baseline, baseline with { Errors = new() { [new('A', 64)] = 3 } }));
            Assert(!InsertionEvidence.Matches(baseline, baseline with { Errors = new() { [new('B', 64)] = 1 } }));
            Assert(!InsertionEvidence.Matches(baseline, baseline with { Errors = [] }));
        });
        check("Exact error fingerprints retain numbers, stack frames and reject abbreviated evidence", () =>
        {
            var path = Path.Combine(Root, "完整日志.log");
            Dictionary<string,int>? Read(string text) { File.WriteAllText(path, text); return PlayerLogReader.ExactErrorCounts(path); }
            var original = Read("TypeLoadException: Missing Def 123\n  at Fixture.First()")!;
            Assert(original.Count == 1);
            Assert(!original.Keys.SequenceEqual(Read("TypeLoadException: Missing Def 124\n  at Fixture.First()")!.Keys));
            Assert(!original.Keys.SequenceEqual(Read("TypeLoadException: Missing Def 123\n  at Fixture.Second()")!.Keys));
            Assert(Read("TypeLoadException: " + new string('x', 33000)) is null);
        });
        check("Insertion preferences default first with confirmation; switches survive limits update", () =>
        {
            var store = new AutomaticSortingPreferencesStore(Path.Combine(Root, "配置"));
            Assert(store.Load().Mode == "insert" && store.Load().ConfirmModRemoval);
            store.SaveMode("full", false);
            store.SaveLimits(new(7) { AttemptLimitEnabled = false, TokenLimitEnabled = false });
            var saved = store.Load();
            Assert(saved.Mode == "full" && !saved.ConfirmModRemoval && !saved.AttemptLimitEnabled);
        });
        check("Recovery preserves baseline evidence and includes control in attempt budget", () =>
        {
            var evidence = new InsertionEvidence(true, new() { [new('A',64)] = 1 }, []);
            var checkpoint = new AiTaskCheckpoint(AiTaskKind.Maintenance, AiTaskPhase.TestSaved, new('A',64),
                ["test.a", "test.b", "test.new"], ["test.a", "test.new", "test.b"], DateTimeOffset.UtcNow)
            {
                Limits = new(2) { InsertionBaselineJson = scope }, Attempts = 1, InsertionControlAttempts = 1,
                DeadlineUtc = DateTimeOffset.UtcNow.AddHours(1),
                InsertionControl = evidence,
                Feedback = new(1, ["test.a", "test.new", "test.b"], true, true, true)
                { ContextHash = new('A',64), ExistingErrorsOnly = true, InsertionEvidence = evidence }
            };
            var restored = JsonSerializer.Deserialize<AiTaskCheckpoint>(JsonSerializer.Serialize(checkpoint))!;
            restored.Validate();
            Assert(restored.Feedback!.Passed);
            Reject(() => (restored with { InsertionControl = null }).Validate());
            Reject(() => (restored with { InsertionControlAttempts = 0 }).Validate());
            // A moved order is valid; feedback for the previous order must not be reused.
            (restored with { CurrentOrder = ["test.b", "test.a", "test.new"], Feedback = null }).Validate();
            Reject(() => (restored with { CurrentOrder = ["test.b", "test.a", "test.new"] }).Validate());
            Reject(() => (restored with { CurrentOrder = ["test.a", "test.new"] }).Validate());
            Reject(() => (restored with { Limits = restored.Limits! with { MaximumAttempts = 1 } }).Validate());
            var store = new AiTaskRecoveryStore(Path.Combine(Root, "恢复"));
            using var task = store.Create(restored);
            Reject(() => task.Save(task.Current with { InsertionControlAttempts = 0 }));
        });
        check("Insertion controller accepts verified old errors without AI and counts baseline test", () =>
        {
            var evidence = new InsertionEvidence(true, new() { [new('A', 64)] = 1 }, []);
            var result = RunController(new(true, ["old error"]) { ExistingErrorsOnly = true, InsertionEvidence = evidence }, evidence);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.ExistingErrorsOnly
                && result.Attempts == 1 && result.ControlAttempts == 1 && result.Requests == 0);
        });
        check("Insertion controller refuses old-error exemption without a completed control", () =>
        {
            var result = RunController(new(true, ["unproven error"]) { ExistingErrorsOnly = true,
                InsertionEvidence = new(true, new() { [new('A', 64)] = 1 }, []) }, null);
            Assert(result.Outcome != MaintenanceOutcome.Passed);
        });
        check("Warnings alone complete insertion without requesting AI", () =>
        {
            var result = RunController(new(true, []) { RuntimeContext = new([new("UnknownWarning", "test.a", 1)]) }, null);
            Assert(result.Outcome == MaintenanceOutcome.Passed && !result.ExistingErrorsOnly && result.Requests == 0);
        });

        MaintenanceResult RunController(MaintenanceGameReport report, InsertionEvidence? control)
        {
            string[] order = ["test.a", "test.new", "test.b"];
            var catalogue = new ModCatalogue([Mod("test.a"), Mod("test.b"), Mod("test.new")]);
            var rules = InsertionScope.Rules([], scope);
            var settings = AiSettings.Default with { Enabled = true, Model = "test", Endpoint = "http://127.0.0.1/v1/",
                PermissionLevel = AiPermissionLevel.Level1OrderAndRequiredDependencies };
            var request = new AiRequest(settings, null, "1.7", order, order, ["test.a", "test.b"], []) { OrderRules = rules };
            var session = new FixtureSession(report, control);
            var controller = new LevelOneMaintenance(new NoCallsClient(), new(Path.Combine(Root, Guid.NewGuid().ToString("N"))), session);
            var result = controller.RunAsync(request, catalogue, rules,
                new(2) { TokenLimitEnabled = false, InsertionBaselineJson = scope }, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(session.Finished);
            return result;
        }
    }

    private sealed class NoCallsClient : IAiClient
    {
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new Exception("Unexpected connection request");
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token) => throw new Exception("Unexpected AI request");
    }

    private sealed class FixtureSession(MaintenanceGameReport report, InsertionEvidence? control) : ILevelOneGameSession, IMaintenancePreparation
    {
        public bool Finished;
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token) => Task.FromResult(report);
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) => throw new Exception("Unexpected order change");
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token) { Finished = true; return Task.CompletedTask; }
        public string ContextHash(AiRequest request, ModCatalogue catalogue, IReadOnlyList<CustomOrderRule> rules) => new('B', 64);
        public Task<MaintenancePreparation> PrepareAsync(AiRequest request, ModCatalogue catalogue, AiTaskRecoveryStore.AiTaskLease task, CancellationToken token)
        {
            if (control is not null) task.Save(task.Current with { InsertionControlAttempts = 1, InsertionControl = control });
            return Task.FromResult(new MaintenancePreparation(request, catalogue, request.CurrentOrder) { Rules = request.OrderRules });
        }
    }
}
