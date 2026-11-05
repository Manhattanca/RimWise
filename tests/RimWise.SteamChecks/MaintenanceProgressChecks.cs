using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;

internal static class MaintenanceProgressChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "maintenance-progress", Guid.NewGuid().ToString("N"));
    private static readonly string[] Order = ["ludeon.rimworld", "test.a", "test.b", "test.translation"];
    private const string Blocker = "System.PlatformNotSupportedException: unsupported operation [Ref ABC123]\n at Example.Step.Run() in Example.dll:token 0x123+0x4\n at RimWise.TestHelper.NewGameJourney.Step()";
    private static void Assert(bool value, string message = "Maintenance progress regression")
    { if (!value) throw new Exception(message); }
    private static ModInstallation Mod(string id, params string[] dependencies) => new(Path.Combine(Root, id), id,
        id == Order[0] ? ModSource.Game : ModSource.Local, "", new(id, "test", id, ["1.7"], "",
            new(dependencies.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
    private static ModCatalogue Catalogue(bool overlap = false) => new([Mod(Order[0]), Mod(Order[1]), Mod(Order[2]),
        overlap ? Mod(Order[3], Order[1], Order[2]) : Mod(Order[3], Order[1])]);
    private static AiRequest Request() => new(AiSettings.Default with { Enabled = true, Model = "test",
        Endpoint = "http://127.0.0.1/v1/" }, null, "1.7", Order, Order, [], []);
    private static MaintenanceGameReport Failure(bool blocker = false) => new(false,
        ["Could not load type 'Example.A'", "Could not load type 'Example.B'"])
    {
        CanDiagnose = true,
        DiagnosticFacts = [new(blocker ? "GameTestBlockingFailure" : "GameTypeLoadFailure", Order[1]),
            new("GameTypeLoadFailure", Order[2])],
        LogContext = new([], [new("Example.A", Order[1], "assembly-type"), new("Example.B", Order[2], "assembly-type")], false)
            { BlockingError = blocker ? Blocker : null }
    };
    private static readonly MaintenanceLimits Limits = new() { TokenLimitEnabled = false, AttemptLimitEnabled = false };

    internal static void Run(Action<string, Action> check)
    {
        check("Failure signature ignores changing reference IDs, addresses and unrelated startup errors", () =>
        {
            var first = Failure(true);
            var next = first with { Errors = ["different incidental error"],
                LogContext = first.LogContext! with { BlockingError = Blocker.Replace("ABC123", "999ABC").Replace("0x123", "0xFFFF")
                    + "\n at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method()" } };
            Assert(MaintenanceFailureSignature.Create(first) == MaintenanceFailureSignature.Create(next));
            Assert(MaintenanceFailureSignature.Create(first) != MaintenanceFailureSignature.Create(next with
                { LogContext = next.LogContext! with { BlockingError = Blocker.Replace("Step.Run", "Step.Other") } }));
            var restored = JsonSerializer.Deserialize<MaintenanceFeedback>(JsonSerializer.Serialize(
                MaintenanceFeedback.Capture(1, Order, first, new string('A', 64))))!.Restore();
            Assert(MaintenanceFailureSignature.Create(first) == MaintenanceFailureSignature.Create(restored));
        });
        check("Unresolved blocker refuses unrelated exclusions and stops after one diagnosis", () =>
        {
            var report = Failure(true) with { DiagnosticFacts = [new("GameTypeLoadFailure", Order[2])] };
            Assert(TrialExclusionPlanning.Candidates(report, Catalogue(), Order, [], new(1, 7), 1).Count == 0);
            var client = new Client(); var session = new Session(_ => report);
            var result = Run("unresolved", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Attempts == 1 && client.Calls == 1
                && result.TrialExclusions.Count == 0, result.Message);
        });
        check("Identical blocker after an exclusion stops without a second paid request or unrelated removal", () =>
        {
            var client = new Client(); var session = new Session(_ => Failure(true));
            var result = Run("unchanged", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Attempts == 2 && client.Calls == 1
                && result.TrialExclusions.Count == 1 && result.OriginalRestored, result.Message);
            Assert(session.Orders[1].SequenceEqual([Order[0], Order[2]]));
        });
        check("Changed order cannot evade unchanged-error detection; one evidenced local trial remains possible", () =>
        {
            var client = new Client(reorder: true); var session = new Session(_ => Failure(true));
            var result = Run("reorder", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Attempts == 3 && client.Calls == 1
                && result.TrialExclusions.Count == 1, result.Message);
        });
        check("Independent error groups use one combined retest and retain final removal consent", () =>
        {
            var client = new Client(); var session = new Session(n => n == 1 ? Failure() : new(true, []));
            var result = Run("batch", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 2 && client.Calls == 1
                && result.TrialExclusions.Count == 2 && result.OriginalRestored && !result.TrialRemovalsApproved, result.Message);
            Assert(result.LastAttemptedOrder.SequenceEqual([Order[0]]) && result.TrialExclusions.All(t => t.Attempt == 1));
            var saved = new AiTaskRecoveryStore(Path.Combine(Root, "batch")).Inspect().Single().Checkpoint!;
            var copy = JsonSerializer.Deserialize<AiTaskCheckpoint>(JsonSerializer.Serialize(saved))!;
            copy.Validate();
            Assert(TrialExclusionPlanning.Replay(Catalogue(), Order, [], new(1, 7), copy.TrialExclusions).SequenceEqual([Order[0]]));
        });
        check("Shared error owners, overlapping dependencies and protected dependencies never batch", () =>
        {
            var report = Failure();
            TrialExclusion[] Batch(MaintenanceGameReport r, ModCatalogue c, string[] retained)
            {
                var first = TrialExclusionPlanning.Select(r, c, Order, retained, new(1, 7), 1)!;
                return TrialExclusionPlanning.Batch(r, c, Order, retained, new(1, 7), first);
            }
            Assert(Batch(report, Catalogue(), [Order[3]]).Single().SelectedId == Order[2]);
            Assert(Batch(report, Catalogue(true), []).Length == 1);
            Assert(Batch(report with { Errors = ["Could not load type 'Example.A' because type 'Example.B' failed"] }, Catalogue(), []).Length == 1);
            Assert(Batch(report with { LogContext = report.LogContext! with
                { Owners = [new("Example.A", Order[1], "assembly-type"), new("Example.A", Order[2], "assembly-type")] } }, Catalogue(), []).Length == 1);
            Assert(Batch(Failure(true), Catalogue(), []).Length == 1);
        });
        check("Different failure after a successful exclusion can still receive a new diagnosis", () =>
        {
            var client = new Client(); var session = new Session(n => n switch
            {
                1 => Failure(true),
                2 => Failure(true) with { DiagnosticFacts = [new("GameTestBlockingFailure", Order[2])],
                    LogContext = Failure(true).LogContext! with { BlockingError = Blocker.Replace("Step.Run", "Other.Run") } },
                _ => new(true, [])
            });
            var result = Run("progress", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 3 && client.Calls == 2, result.Message);
        });
        check("Saved failed exclusion survives resume without a repeat AI call or losing protection", () =>
        {
            var root = Path.Combine(Root, "resume");
            var client = new Client();
            var session = new Session(n => n == 1 ? Failure(true) : throw new MaintenancePauseException("simulated interruption"));
            var first = new LevelOneMaintenance(client, new AiTokenEstimator(root), session)
                .RunAsync(Request(), Catalogue(), [], Limits, null, CancellationToken.None).GetAwaiter().GetResult();
            var secondSession = new Session(_ => Failure(true));
            var result = new LevelOneMaintenance(client, new AiTokenEstimator(root), secondSession, first.TaskId)
                .RunAsync(Request(), Catalogue(), [], Limits, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Stopped && client.Calls == 1 && result.TrialExclusions.Count == 1
                && result.OriginalRestored && secondSession.Orders.Count == 1, result.Message);
        });
        check("Combined exclusion recovery replays every group without another paid request", () =>
        {
            var root = Path.Combine(Root, "batch-resume"); var client = new Client();
            var firstSession = new Session(n => n == 1 ? Failure() : throw new MaintenancePauseException("interruption"));
            var first = new LevelOneMaintenance(client, new AiTokenEstimator(root), firstSession)
                .RunAsync(Request(), Catalogue(), [], Limits, null, CancellationToken.None).GetAwaiter().GetResult();
            var secondSession = new Session(_ => new(true, []));
            var result = new LevelOneMaintenance(client, new AiTokenEstimator(root), secondSession, first.TaskId)
                .RunAsync(Request(), Catalogue(), [], Limits, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.TrialExclusions.Count == 2 && client.Calls == 1
                && result.OriginalRestored && !result.TrialRemovalsApproved && secondSession.Orders.Single().SequenceEqual([Order[0]]), result.Message);
        });
        check("Recovery rejects removal of remembered failures and tampered batch history", () =>
        {
            var store = new AiTaskRecoveryStore(Path.Combine(Root, "unchanged"));
            using var lease = store.Open(store.Inspect().Single().Id);
            bool rejected = false;
            try { lease.Save(lease.Current with { DiagnosedFailures = [] }); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected);
            var report = Failure();
            var first = TrialExclusionPlanning.Select(report, Catalogue(), Order, [], new(1, 7), 1)!;
            var batch = TrialExclusionPlanning.Batch(report, Catalogue(), Order, [], new(1, 7), first);
            batch[1] = batch[1] with { TestedOrder = Order };
            rejected = false;
            try { TrialExclusionPlanning.Replay(Catalogue(), Order, [], new(1, 7), batch); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected);
        });
    }

    internal static void ReplayHistory(string directory)
    {
        var hashes = new HashSet<string>(); var count = 0; var unresolved = 0;
        foreach (var folder in Directory.EnumerateDirectories(directory, "attempt-*"))
        {
            var helper = Path.Combine(folder, "workbench-helper-failure.txt");
            var contextPath = Path.Combine(folder, "diagnostic-context.json");
            if (!File.Exists(helper) || !File.Exists(contextPath)) continue;
            var raw = File.ReadAllText(helper);
            using var contextJson = JsonDocument.Parse(File.ReadAllText(contextPath));
            var context = contextJson.RootElement.GetProperty("Logs").Deserialize<AiLogContext>()!;
            context = context with { BlockingError = DiagnosticLogContext.Sanitize(raw) };
            var report = new MaintenanceGameReport(false, ["captured helper failure"]) { CanDiagnose = true, LogContext = context };
            hashes.Add(MaintenanceFailureSignature.Create(report)!);
            var symbols = DiagnosticLogContext.ExceptionSymbols(raw);
            if (!context.Owners.Any(o => symbols.Contains(o.Symbol))) unresolved++;
            count++;
        }
        Assert(count > 1 && hashes.Count == 1 && unresolved == count,
            $"Archived replay: {count} attempts, {hashes.Count} signatures, {unresolved} unresolved");
        Console.WriteLine(JsonSerializer.Serialize(new { ArchivedAttempts = count, DistinctBlockers = hashes.Count,
            UnresolvedBlockerOwners = unresolved, RealGameRetest = false }));
    }

    private static MaintenanceResult Run(string name, Client client, Session session) =>
        new LevelOneMaintenance(client, new AiTokenEstimator(Path.Combine(Root, name)), session)
            .RunAsync(Request(), Catalogue(), [], Limits, null, CancellationToken.None).GetAwaiter().GetResult();

    private sealed class Client(bool reorder = false) : IAiClient
    {
        internal int Calls;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            Calls++;
            var response = reorder ? new StructuredAiResponse(1, "test", [new("move", Order[2], 2, false, null, null)], []) : null;
            return Task.FromResult(new AiClientResult(response is not null, "simulated", response, null)
                { Usage = new(10, 10, 20), Failure = response is null ? AiReplyFailure.ServiceFailure : AiReplyFailure.None });
        }
    }
    private sealed class Session(Func<int, MaintenanceGameReport> report) : ILevelOneGameSession, IMaintenanceTrialSession
    {
        internal readonly List<string[]> Orders = [];
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        { Orders.Add(order.ToArray()); Assert(Orders.Count < 8, "Unexpected runaway"); return Task.FromResult(report(Orders.Count)); }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token)
        { Assert(plan.CanApply); return Task.CompletedTask; }
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token) => Task.CompletedTask;
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials) { foreach (var trial in trials) trial.Validate(); }
    }
}
