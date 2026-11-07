using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;

internal static class MaintenanceEfficiencyChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "maintenance-efficiency", Guid.NewGuid().ToString("N"));
    private static readonly string[] Order = ["ludeon.rimworld", "test.a", "test.b", "test.translation"];
    private static readonly string[] Signatures = [new('A', 64), new('B', 64)];
    private static ModInstallation Mod(string id, params string[] deps) => new(Path.Combine(Root, id), id,
        id == Order[0] ? ModSource.Game : ModSource.Local, "", new(id, "test", id, ["1.7"], "",
            new(deps.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
    private static ModCatalogue Catalogue() => new([Mod(Order[0]), Mod(Order[1]), Mod(Order[2]), Mod(Order[3], Order[1])]);
    private static AiRequest Request() => new(AiSettings.Default with { Enabled = true, Model = "test",
        Endpoint = "http://127.0.0.1/v1/" }, null, "1.7", Order, Order, [], []);
    private static MaintenanceGameReport Failure(bool reduced = false, string? second = null) => new(true,
        reduced ? ["second error"] : ["first error", "second error"])
    {
        CanDiagnose = true, ErrorSignatures = reduced ? [second ?? Signatures[1]] : Signatures,
        DiagnosticFacts = reduced ? [new("GameLogError", Order[2])]
            : [new("GamePatchFailure", Order[1]), new("GameLogError", Order[2])]
    };
    private static MaintenanceGameReport PatchReport(bool same = false) => new(true,
        [$"[Verse] Patch operation Verse.PatchOperationRemove(/Defs/ThingDef[defName=\"TargetA\"]/comps) failed [{Order[1]}]",
         $"[Verse] Patch operation Verse.PatchOperationReplace(/Defs/ThingDef[defName=\"{(same ? "TargetA" : "TargetB")}\"]/label) failed [{Order[2]}]"])
    {
        CanDiagnose = true,
        DiagnosticFacts = [new("GamePatchFailure", Order[1]), new("GamePatchFailure", Order[2])],
        LogContext = new([], [], false) { Files = [Xml(Order[1], "TargetA", "comps"), Xml(Order[2], same ? "TargetA" : "TargetB", "label")] }
    };
    private static AiModFileEvidence Xml(string id, string target, string field) => new("active-patch", id, 2, "ThingDef", target,
        $"<Operation Class=\"PatchOperationRemove\"><xpath>/Defs/ThingDef[defName=\"{target}\"]/{field}</xpath></Operation>");
    private static void Assert(bool value, string message = "Maintenance efficiency regression")
    { if (!value) throw new Exception(message); }

    internal static void Run(Action<string, Action> check)
    {
        check("Independent XML patch owners batch into one retest with dependency closure and removal consent", () =>
        {
            var client = new Client(); var session = new Session(n => n == 1 ? PatchReport() : new(true, []));
            var result = Run("xml-batch", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 2 && client.Calls == 1
                && result.TrialExclusions.Count == 2 && result.OriginalRestored && !result.TrialRemovalsApproved, result.Message);
            Assert(session.Orders[1].SequenceEqual([Order[0]]));
        });
        check("Shared patch targets, unmatched XPath, references and malformed XML cannot justify batching", () =>
        {
            TrialExclusion[] Batch(MaintenanceGameReport report, string[]? retained = null)
            {
                var first = TrialExclusionPlanning.Select(report, Catalogue(), Order, retained ?? [], new(1, 7), 1)!;
                return TrialExclusionPlanning.Batch(report, Catalogue(), Order, retained ?? [], new(1, 7), first);
            }
            Assert(Batch(PatchReport(true)).Length == 1);
            var report = PatchReport();
            foreach (var xml in new[] { Xml(Order[2], "OtherTarget", "label"),
                Xml(Order[2], "TargetB", "label") with { Kind = "active-reference" },
                Xml(Order[2], "TargetB", "label") with { Text = "<Operation>" },
                Xml(Order[2], "TargetB", "label") with { Text = "<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///never-read'>]><a>&x;</a>" } })
                Assert(Batch(report with { LogContext = report.LogContext! with { Files = [report.LogContext!.Files[0], xml] } }).Length == 1);
            Assert(Batch(report with { Errors = [report.Errors[0], report.Errors[1].Replace("[test.b]", "[unknown.b]")] }).Length == 1);
            Assert(Batch(report, [Order[3]]).Single().SelectedId == Order[2]);
        });
        check("Reduced already diagnosed error set reuses analysis without another paid request", () =>
        {
            var client = new Client(); var session = new Session(n => n < 3 ? Failure(n == 2) : new(true, []));
            var result = Run("reduced", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 3 && client.Calls == 1
                && result.TrialExclusions.Count == 2 && result.OriginalRestored && !result.TrialRemovalsApproved, result.Message);
            var saved = new AiTaskRecoveryStore(Path.Combine(Root, "reduced")).Inspect().Single().Checkpoint!;
            Assert(saved.DiagnosedErrors.SequenceEqual(Signatures) && saved.ReportedTokens == 20);
        });
        check("Unchanged errors stop unrelated exclusion chains without claiming a pass", () =>
        {
            var client = new Client(); var session = new Session(_ => Failure());
            var result = Run("unchanged", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Attempts == 2 && client.Calls == 1
                && result.TrialExclusions.Count == 1 && result.OriginalRestored, result.Message);
        });
        check("A new error signature still receives a fresh diagnosis", () =>
        {
            var client = new Client(); var session = new Session(n => n < 3 ? Failure(n == 2, new('C', 64)) : new(true, []));
            var result = Run("changed", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 2 && result.TrialExclusions.Count == 2, result.Message);
        });
        check("A failed reorder may still try a local exclusion using its saved diagnosis", () =>
        {
            var client = new Client { Reorder = true };
            var session = new Session(n => n < 4 ? Failure(n == 3) : new(true, []));
            var result = Run("reorder", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 4 && client.Calls == 1
                && result.TrialExclusions.Count == 2, result.Message);
        });
        check("Rejected AI replies are not remembered as valid diagnosis", () =>
        {
            var client = new Client { Reject = true }; var session = new Session(n => n < 3 ? Failure(n == 2) : new(true, []));
            var result = Run("rejected", client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 2, result.Message);
            Assert(new AiTaskRecoveryStore(Path.Combine(Root, "rejected")).Inspect().Single().Checkpoint!.DiagnosedErrors.Length == 0);
        });
        check("Reuse survives interruption and checkpoint serialization without another request", () =>
        {
            var client = new Client(); var firstSession = new Session(n => n == 1 ? Failure() : throw new MaintenancePauseException("interruption"));
            var first = Run("resume", client, firstSession);
            var next = new Session(n => n == 1 ? Failure(true) : new(true, []));
            var result = Run("resume", client, next, first.TaskId);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.TrialExclusions.Count == 2 && client.Calls == 1, result.Message);
            var store = new AiTaskRecoveryStore(Path.Combine(Root, "resume"));
            using var lease = store.Open(store.Inspect().Single().Id);
            bool rejected = false;
            try { lease.Save(lease.Current with { DiagnosedErrors = [] }); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected);
            var clone = JsonSerializer.Deserialize<AiTaskCheckpoint>(JsonSerializer.Serialize(lease.Current))!;
            clone.Validate(); Assert(clone.DiagnosedErrors.SequenceEqual(Signatures));
        });
        check("Full error fingerprints ignore timestamps and refs while preserving different stacks and definitions", () =>
        {
            OrdinaryGameReport Report(string text)
            {
                Directory.CreateDirectory(Root);
                var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".log");
                File.WriteAllText(path, text);
                return new(1, 0, GameTestStopReason.ProbeCompleted, true, true, [PlayerLogReader.Read(path)])
                    { ProbeStage = "map-ready", ProbeTicks = 120, ProbeColonists = 1, MenuPassed = true, FactionsPassed = true, PawnsTested = 1 };
            }
            var text = "2026-11-06 12:00:00.000 ERROR missing definition Item12 [Ref ABC123]\n at Example.Step.Run() [0x12]";
            var report = Report(text);
            Assert(report.MapSuccessVerified);
            var original = MaintenanceErrorMemory.Capture(report);
            Assert(original.Length == 1 && original.SequenceEqual(MaintenanceErrorMemory.Capture(Report(text.Replace("12:00:00", "13:10:05").Replace("ABC123", "DEF456").Replace("0x12", "0x99")))));
            Assert(!original.SequenceEqual(MaintenanceErrorMemory.Capture(Report(text.Replace("Step.Run", "Other.Run")))));
            Assert(!original.SequenceEqual(MaintenanceErrorMemory.Capture(Report(text.Replace("Item12", "Item13")))));
            Assert(MaintenanceErrorMemory.Capture(Report(text + "\n" + text.Replace("Step.Run", "Other.Run"))).Length == 2);
            Assert(MaintenanceErrorMemory.Capture(report with { Logs = [report.Logs[0] with { Path = Path.Combine(Root, "missing.log") }] }).Length == 0);
            Assert(MaintenanceErrorMemory.Capture(report with { ProbeErrors = 2 }).Length == 0);
            Assert(MaintenanceErrorMemory.Capture(report with { StopReason = GameTestStopReason.Exited }).Length == 0);
            Assert(!MaintenanceErrorMemory.Covered(Failure() with { StartupVerified = false }, Signatures));
        });
    }

    internal static void Replay(string directory)
    {
        var messages = Directory.EnumerateFiles(directory, "workbench-messages.log", SearchOption.AllDirectories).ToArray();
        var hashes = new HashSet<string>(); var counts = new List<int>();
        foreach (var path in messages)
        {
            var log = PlayerLogReader.Read(path) with { Kind = "game-errors" };
            var report = new OrdinaryGameReport(1, 0, GameTestStopReason.ProbeCompleted, true, true, [log])
                { ProbeStage = "map-ready", ProbeTicks = 120, ProbeColonists = 1, MenuPassed = true, FactionsPassed = true, PawnsTested = 1 };
            var signatures = MaintenanceErrorMemory.Capture(report);
            Assert(signatures.Length > 0);
            counts.Add(signatures.Count(hashes.Contains));
            hashes.UnionWith(signatures);
        }
        Assert(messages.Length >= 8);
        Console.WriteLine(JsonSerializer.Serialize(new { CapturedRuntimeLogs = messages.Length,
            PreviouslyObservedErrors = counts.Sum(), RealGameRetest = false, FullStartupLogsAvailable = false }));
    }
    private static MaintenanceResult Run(string name, Client client, Session session, string? resume = null) =>
        new LevelOneMaintenance(client, new AiTokenEstimator(Path.Combine(Root, name)), session, resume)
            .RunAsync(Request(), Catalogue(), [], new() { TokenLimitEnabled = false, AttemptLimitEnabled = false }, null, CancellationToken.None)
            .GetAwaiter().GetResult();
    private sealed class Client : IAiClient
    {
        internal int Calls;
        internal bool Reject;
        internal bool Reorder;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new AiClientResult(!Reject, "test", Reject ? null : new(1, "No supported reorder",
                Reorder ? [new("move", Order[2], 2, false, null, null)] : [], []), null)
                { Usage = new(10, 10, 20), Failure = Reject ? AiReplyFailure.ServiceFailure : AiReplyFailure.None });
        }
    }
    private sealed class Session(Func<int, MaintenanceGameReport> report) : ILevelOneGameSession, IMaintenanceTrialSession
    {
        internal readonly List<string[]> Orders = [];
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        { Orders.Add(order.ToArray()); Assert(Orders.Count < 6); return Task.FromResult(report(Orders.Count)); }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) { Assert(plan.CanApply); return Task.CompletedTask; }
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token) => Task.CompletedTask;
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials) { foreach (var trial in trials) trial.Validate(); }
    }
}
