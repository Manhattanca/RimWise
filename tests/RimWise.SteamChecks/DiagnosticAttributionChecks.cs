using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;

internal static class DiagnosticAttributionChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "blocking-diagnostics", Guid.NewGuid().ToString("N"));
    private static readonly string[] Order = ["ludeon.rimworld", "test.noise", "test.blocker", "test.translation"];
    private const string Symbol = "Example.WorldStep.Prefix";
    private const string HelperText = "ERROR System.Reflection.TargetInvocationException\n ---> System.PlatformNotSupportedException: unsupported operation\n   at System.Threading.Thread.Abort()\n   at Example.WorldStep.Prefix()\n   at RimWorld.Page_CreateWorldParams.CanDoNext()\n - PREFIX Innocent: Void Unrelated.Patch:Prefix()\n   at RimWise.TestHelper.NewGameJourney.Step()";
    private static ModInstallation Mod(string id, string? directory = null, params string[] dependencies) =>
        new(directory ?? Path.Combine(Root, id), id, id == Order[0] ? ModSource.Game : ModSource.Local, "",
            new(id, "test", id, ["1.7"], "", new(dependencies.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
    private static ModCatalogue Catalogue() => new([Mod(Order[0]), Mod(Order[1]), Mod(Order[2]), Mod(Order[3], null, Order[2])]);
    private static GameLogSnapshot Log(string text = HelperText) => new(Path.Combine(Root, "workbench-helper-failure.txt"),
        DateTimeOffset.UtcNow, text, [], false);
    private static AiLogContext Context() => new([new("startup", HelperText)], [new(Symbol, Order[2], "assembly-type")], false);
    private static OrdinaryGameReport Report(params GameLogSnapshot[] logs) =>
        new(123, 0, GameTestStopReason.ProbeCompleted, true, true, logs.Length == 0 ? [Log()] : logs)
        { ProbeStage = "helper-failed", FailedPhase = "world", MenuPassed = true, StoppedByTester = true };
    private static void Assert(bool condition, string message = "Blocking attribution regression")
    { if (!condition) throw new Exception(message); }

    internal static void Run(Action<string, Action> check)
    {
        check("Only real third-party exception frames create blocking symbols", () =>
            Assert(DiagnosticLogContext.BlockingSymbols([Log()]).SequenceEqual([Symbol])));
        check("Framework copies never implicate their containing mods", () =>
        {
            var text = "TypeLoadException at HarmonyLib.MethodCopier.GetInstructions() at System.Reflection.MethodBase.Invoke()";
            var log = new GameLogSnapshot(Path.Combine(Root, "Player.log"), DateTimeOffset.UtcNow, text,
                [new(text, 1, [], text)], false);
            var facts = GameTestDiagnostics.Project([log], [new("HarmonyLib.MethodCopier.GetInstructions", Order[1], "assembly-type"),
                new("System.Reflection.MethodBase.Invoke", Order[1], "assembly-type")]);
            Assert(facts.Count > 0 && facts.All(f => f.PackageId.Length == 0));
        });
        check("Blocking exception remains diagnosable without a second copy in Player.log", () =>
        {
            var feedback = OrdinaryGameTestSession.CreateFeedback(Report(), Context(), Root);
            Assert(feedback.CanDiagnose && !feedback.StartupVerified && feedback.Errors.Count == 1);
            var candidates = TrialExclusionPlanning.Candidates(feedback, Catalogue(), Order, [], new(1, 7), 1);
            Assert(candidates.Count == 1 && candidates[0].SelectedId == Order[2]
                && candidates[0].RemovedIds.ToHashSet().SetEquals([Order[2], Order[3]]));
            candidates[0].Validate();
            Assert(TrialExclusionPlanning.Replay(Catalogue(), Order, [], new(1, 7), candidates).SequenceEqual(Order[..2]));
            var recovered = JsonSerializer.Deserialize<MaintenanceFeedback>(JsonSerializer.Serialize(
                MaintenanceFeedback.Capture(1, Order, feedback, new string('A', 64))))!.Restore();
            Assert(recovered.DiagnosticFacts.Any(f => f.Code == "GameTestBlockingFailure") && recovered.CanDiagnose);
        });
        check("Startup noise cannot evict blocker or bypass retained dependency protection", () =>
        {
            var noise = Enumerable.Range(0, 130).Select(i => new GameLogIssue("Patch operation failed " + i, 1,
                [Order[1]], "Patch operation failed " + i)).ToArray();
            var report = Report(new(Path.Combine(Root, "Player.log"), DateTimeOffset.UtcNow, "errors", noise, false), Log());
            var feedback = OrdinaryGameTestSession.CreateFeedback(report, Context(), Root);
            Assert(feedback.DiagnosticFacts[0].Code == "GameTestBlockingFailure");
            Assert(TrialExclusionPlanning.Candidates(feedback, Catalogue(), Order, [], new(1, 7), 1).Single().SelectedId == Order[2]);
            Assert(TrialExclusionPlanning.Candidates(feedback, Catalogue(), Order, [Order[3]], new(1, 7), 1).Count == 0);
        });
        check("Helper infrastructure failure and symbol-prefix collisions do not blame mods", () =>
        {
            var helper = Log("ERROR System.InvalidOperationException\n at RimWise.TestHelper.NewGameJourney.Step()");
            Assert(GameTestDiagnostics.Project([helper], Context().Owners).Count == 0);
            var text = "Exception at Example.WorldStepOther.Run()";
            var log = new GameLogSnapshot(Path.Combine(Root, "Player.log"), DateTimeOffset.UtcNow, text, [new(text, 1, [], text)], false);
            Assert(GameTestDiagnostics.Project([log], [new("Example.WorldStep", Order[2], "assembly-type")]).All(f => f.PackageId.Length == 0));
        });
        check("Empty and duplicate relation records are omitted while complete order and dependencies survive", () =>
        {
            var relation = new AiModRequirementFact(Order[3], [Order[2], "test.missing"], [], [], []);
            var request = Request() with { ModRequirements = [new(Order[0], [], [], [], []), relation, relation] };
            var outbound = AiOutboundContext.Create(request);
            Assert(outbound.CurrentOrder.SequenceEqual(Order) && outbound.ModRequirements.Count == 1
                && outbound.ModRequirements[0].Dependencies.Contains("test.missing"));
        });
        check("Metadata lookup reads an existing assembly without creating or executing a fixture DLL", () =>
        {
            var assembly = typeof(GameTestDiagnostics).Assembly.Location;
            var symbol = typeof(GameTestDiagnostics).FullName! + ".Project";
            var catalogue = new ModCatalogue([Mod(Order[2], Path.GetDirectoryName(assembly))]);
            var owners = DiagnosticSymbolOwners.Find(catalogue, [Order[2]], [symbol, "System.Reflection.MethodBase"],
                CancellationToken.None, out _, [assembly]);
            Assert(owners.Any(o => o.Symbol == symbol && o.PackageId == Order[2])
                && owners.All(o => !o.Symbol.StartsWith("System.")));
        });
        check("Later blocking symbol survives ownership quota consumed by earlier installations", () =>
        {
            var noise = Enumerable.Range(0, 64).Select(i => "Example.Noise" + i).ToArray();
            var folders = Enumerable.Range(0, 3).Select(i => Path.Combine(Root, "priority", i.ToString())).ToArray();
            foreach (var folder in folders) Directory.CreateDirectory(folder);
            var xml = "<Defs>" + string.Join("", noise.Select(s => "<defName>" + s + "</defName>")) + "</Defs>";
            File.WriteAllText(Path.Combine(folders[0], "noise.xml"), xml);
            File.WriteAllText(Path.Combine(folders[1], "noise.xml"), xml);
            File.WriteAllText(Path.Combine(folders[2], "blocker.xml"), "<Defs><defName>" + Symbol + "</defName></Defs>");
            var mods = folders.Select((path, i) => Mod("priority." + i, path)).ToArray();
            var owners = DiagnosticSymbolOwners.Find(new(mods), mods.Select(m => m.PackageId).ToArray(),
                [Symbol, .. noise], CancellationToken.None, out _);
            Assert(owners.Length == 96 && owners[0].Symbol == Symbol && owners[0].PackageId == "priority.2");
        });
        check("Controller tests blocker exclusion then returns restored order for player confirmation", () =>
        {
            var feedback = OrdinaryGameTestSession.CreateFeedback(Report(), Context(), Root);
            var session = new Session(feedback);
            var client = new Client();
            var controller = new LevelOneMaintenance(client, new AiTokenEstimator(Root), session);
            var result = controller.RunAsync(Request(), Catalogue(), [], new(4) { TokenLimitEnabled = false },
                null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 2 && client.Calls == 1, result.Message);
            Assert(result.TrialExclusions.Single().SelectedId == Order[2] && result.OriginalRestored
                && !result.TrialRemovalsApproved && session.Finished);
            Assert(result.LastAttemptedOrder.SequenceEqual(Order[..2]));
        });
    }

    private static AiRequest Request() => new(AiSettings.Default with { Enabled = true, Model = "deepseek-test",
        Endpoint = "http://127.0.0.1/v1/" }, null, "1.7", Order, Order, [], []);
    private sealed class Client : IAiClient
    {
        public int Calls;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            Calls++;
            Assert(request.DiagnosticFacts.Any(f => f.Code == "GameTestBlockingFailure"));
            return Task.FromResult(new AiClientResult(false, "Simulated unavailable service", null, null)
                { Failure = AiReplyFailure.ServiceFailure, ServiceError = new("http", 503) });
        }
    }
    private sealed class Session(MaintenanceGameReport first) : ILevelOneGameSession, IMaintenanceTrialSession
    {
        private int attempts;
        private bool authorized;
        public bool Finished;
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        {
            if (++attempts == 1) return Task.FromResult(first);
            Assert(order.SequenceEqual(Order[..2]));
            return Task.FromResult(new MaintenanceGameReport(true, []));
        }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token)
        { Assert(authorized && plan.CanApply && plan.After.SequenceEqual(Order[..2])); return Task.CompletedTask; }
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token)
        { Assert(!acceptTestedOrder); Finished = true; return Task.CompletedTask; }
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials)
        { Assert(trials.Single().SelectedId == Order[2]); authorized = true; }
    }

    // The log and installation are supplied locally; no real mod identifiers are built into product behavior.
    internal static void Replay(string helperPath, string modDirectory)
    {
        var about = System.Xml.Linq.XDocument.Load(Path.Combine(modDirectory, "About", "About.xml"));
        var id = about.Descendants().First(e => e.Name.LocalName.Equals("packageId", StringComparison.OrdinalIgnoreCase)).Value.Trim();
        var log = new GameLogSnapshot(helperPath, DateTimeOffset.UtcNow, File.ReadAllText(helperPath), [], false);
        var catalogue = new ModCatalogue([Mod(id, modDirectory)]);
        var symbols = DiagnosticLogContext.BlockingSymbols([log]);
        var owners = DiagnosticSymbolOwners.Find(catalogue, [id], symbols, CancellationToken.None, out var limited);
        var facts = GameTestDiagnostics.Project([log], owners);
        Assert(facts.Any(f => f.PackageId == id && f.Code == "GameTestBlockingFailure"), "Real exception ownership not recovered");
        Console.WriteLine(JsonSerializer.Serialize(new { Replay = "Local exception and static PE ownership", PackageId = id,
            Symbols = symbols, Owners = owners, Facts = facts, Limited = limited }));
    }

    internal static void ReplayArchivedReport(string reportPath)
    {
        var report = JsonSerializer.Deserialize<OrdinaryGameReport>(File.ReadAllText(reportPath))!;
        using var archivedContext = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(reportPath)!, "diagnostic-context.json")));
        var context = archivedContext.RootElement.GetProperty("Logs").Deserialize<AiLogContext>()!;
        var symbols = DiagnosticLogContext.BlockingSymbols(report.Logs);
        Assert(symbols.Length > 0 && symbols.All(s => !DiagnosticLogContext.IsFrameworkSymbol(s)), "Archived blocking frames missing");
        var feedback = OrdinaryGameTestSession.CreateFeedback(report, context, Path.GetDirectoryName(reportPath)!);
        Assert(feedback.CanDiagnose && !feedback.StartupVerified, "Archived failed journey not diagnosable");
        // Original exported owners contain no verified blocker. Do not invent the absent PE mapping.
        var verifiedBlockerOwners = context.Owners.Where(o => symbols.Contains(o.Symbol)).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { Replay = "Original report and captured stack", Symbols = symbols,
            report.ProbeStage, report.FailedPhase, feedback.CanDiagnose, VerifiedBlockerOwners = verifiedBlockerOwners,
            DiscardedFrameworkOwners = context.Owners.Count(o => DiagnosticLogContext.IsFrameworkSymbol(o.Symbol)),
            StaticOwnershipReplay = "Separate existing-assembly regression; original mod installation unavailable" }));
    }
}
