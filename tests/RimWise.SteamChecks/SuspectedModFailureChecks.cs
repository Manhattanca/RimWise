using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;

/// <summary>Offline private-test authorization and recovery. Never contacts AI, Steam or a game.</summary>
internal static class SuspectedModFailureChecks
{
    private const string Core = "ludeon.rimworld";
    private static readonly GameVersion Version = new(1, 7);
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "suspected-mod-failure", Guid.NewGuid().ToString("N"));
    private static readonly string[] Order = [Core, "fault.mod", "healthy.mod", "fault.child"];
    private static readonly MaintenanceLimits Limits = new(6) { TokenLimitEnabled = false };
    private static ModInstallation Mod(string id, params string[] dependencies) => new(Path.Combine(Root, id), id,
        id == Core ? ModSource.Game : ModSource.Local, "", new(id, "fixture", id, ["1.7"], "",
            new(dependencies.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
    private static ModCatalogue Catalogue() => new([Mod(Core), Mod("fault.mod"), Mod("healthy.mod"), Mod("fault.child", "fault.mod")]);
    private static string Signature(char c) => new(c, 64);
    private static MaintenanceGameReport Failure(string id = "fault.mod", string code = "GameTypeLoadFailure", params char[] signatures) =>
        new(true, ["System.TypeLoadException: Could not load type 'Fixture.Missing' from assembly 'Fixture'"])
        {
            CanDiagnose = true, DiagnosticFacts = [new(code, id)],
            ErrorSignatures = (signatures.Length == 0 ? new[] { 'A' } : signatures).Select(Signature).ToArray()
        };
    private static AiRequest Request(string[]? order = null, ModCatalogue? catalogue = null, string[]? retained = null,
        string model = "deepseek-flash") => new(AiSettings.Default with { Enabled = true, Model = model,
            Endpoint = "http://127.0.0.1/v1/" }, null, "1.7", order ?? Order,
            (catalogue ?? Catalogue()).Installations.Select(m => m.PackageId).ToArray(), retained ?? [], []);
    private static MaintenanceResult Run(IAiClient client, ILevelOneGameSession session, AiRequest? request = null, ModCatalogue? catalogue = null,
        MaintenanceLimits? limits = null, string? root = null, string? resume = null) =>
        new LevelOneMaintenance(client, new AiTokenEstimator(root ?? Path.Combine(Root, Guid.NewGuid().ToString("N"))),
            session, resume, testOriginalFirst: true).RunAsync(request ?? Request(), catalogue ?? Catalogue(), [], limits ?? Limits,
                null, CancellationToken.None).GetAwaiter().GetResult();

    internal static void Run(Action<string, Action> check)
    {
        foreach (var model in new[] { "deepseek-flash", "gpt-4.1" })
            check(model + " valid empty diagnosis starts a private suspected-failure test", () =>
            {
                var client = new Client(); var session = new Session(n => n == 1 ? Failure() : new(true, []));
                var result = Run(client, session, Request(model: model));
                Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 2 && client.Calls == 1
                    && session.Orders[0].SequenceEqual(Order) && session.Orders[1].SequenceEqual([Core, "healthy.mod"])
                    && result.TrialExclusions.Single() is { SuspectedFailureVerification: true, ReadFailureRemoval: false }
                    && result.TrialExclusions[0].RemovedIds.SequenceEqual(["fault.mod", "fault.child"])
                    && result.OriginalRestored && !result.TrialRemovalsApproved && session.Finished, result.Message);
            });
        foreach (var code in new[] { "GameTypeLoadFailure", "GameMissingXmlParent", "GameModContentFailure" })
            check(code + " is a hypothesis, not proof or permanent-removal authority", () =>
            {
                var trial = Candidates(Failure(code: code)).Single(); trial.Validate();
                Assert(trial.SuspectedFailureVerification && !trial.ReadFailureRemoval
                    && trial.DiagnosticCodes.Contains(code) && trial.FailureFingerprint is not null);
            });
        foreach (var model in new[] { "deepseek-flash", "gpt-4.1" })
            check(model + " HTTP parse -> saved reply -> trial authorization -> retest works offline", () =>
            {
                using var handler = new Handler(); using var http = new HttpClient(handler); using var client = new AiClient(http);
                var session = new Session(n => n == 1 ? Failure() : new(true, []));
                var request = Request(model: model); request = request with { Settings = request.Settings with { StreamResponses = false } };
                var result = Run(client, session, request);
                Assert(result.Outcome == MaintenanceOutcome.Passed && handler.Calls == 1 && result.TrialExclusions.Count == 1, result.Message);
            });
        foreach (var category in new[] { "mod", "unknown", "environment" })
            check("Incomplete startup honors the AI category: " + category, () =>
            {
                var failure = Failure() with { StartupVerified = false,
                    StartupFailure = new(new("ProbeCompleted", true, true, [new("MissingType")]), []) };
                var session = new Session(n => n == 1 ? failure : new(true, [])); var client = new Client(category: category);
                var result = Run(client, session);
                Assert(category == "mod" ? result.Outcome == MaintenanceOutcome.Passed && result.TrialExclusions.Count == 1
                    : result.Outcome == MaintenanceOutcome.Stopped && result.TrialExclusions.Count == 0 && session.Applies == 0, result.Message);
            });
        foreach (var code in new[] { "GameLogError", "GamePatchFailure", "VersionNotDeclared", "VersionUnsupported", "UnknownWarning" })
            check(code + " alone never starts suspected-damage verification", () =>
            {
                Assert(Candidates(Failure(code: code)).Count == 0);
                var client = new Client(); var session = new Session(_ => Failure(code: code));
                var result = Run(client, session);
                Assert(result.Outcome == MaintenanceOutcome.Stopped && client.Calls == 1
                    && session.Applies == 0 && result.TrialExclusions.Count == 0, result.Message);
            });
        foreach (var retained in new[] { new[] { "fault.mod" }, new[] { "fault.child" } })
            check("Retained owner or dependent prevents suspected-failure removal: " + retained[0], () =>
            {
                Assert(Candidates(Failure(), retained: retained).Count == 0);
                var client = new Client(); var session = new Session(_ => Failure());
                var result = Run(client, session, Request(retained: retained));
                Assert(result.TrialExclusions.Count == 0 && session.Applies == 0, result.Message);
            });
        check("Official content, inactive IDs, unknown ownership and duplicate installations are not candidates", () =>
        {
            foreach (var id in new[] { Core, "", "uninstalled.mod" }) Assert(Candidates(Failure(id)).Count == 0);
            var duplicate = new ModCatalogue([.. Catalogue().Installations, Mod("fault.mod") with { Directory = Path.Combine(Root, "duplicate") }]);
            Assert(Candidates(Failure(), duplicate).Count == 0);
        });
        check("Workshop canonical IDs resolve to exact active installation identifiers", () =>
        {
            var workshop = Mod("fault.mod") with { Source = ModSource.Workshop, WorkshopId = "123" };
            var catalogue = new ModCatalogue([Mod(Core), workshop, Mod("healthy.mod")]);
            var trial = Candidates(Failure(), catalogue, [Core, "fault.mod_steam", "healthy.mod"]).Single();
            Assert(trial.SelectedId == "fault.mod_steam" && trial.RemovedIds.SequenceEqual(["fault.mod_steam"]));
        });
        foreach (var kind in new[] { "incomplete", "environment", "order-mismatch", "no-errors", "no-order-authority", "unresolved-blocker" })
            check(kind + " cannot authorize suspected-failure tests", () =>
            {
                var report = Failure();
                report = kind switch
                {
                    "incomplete" => report with { CanDiagnose = false },
                    "no-errors" => report with { Errors = [] },
                    "unresolved-blocker" => report with { LogContext = new([], [], false) { BlockingError = "System.Exception at Unknown.Mod.Run()" } },
                    _ => report with { StartupVerified = false, StartupFailure = new(new("ProbeCompleted", true,
                        kind != "no-order-authority", kind == "order-mismatch" ? [new("TestOrderMismatch")]
                            : kind == "environment" ? [new("GraphicsDriverUnavailable")] : []), []) }
                };
                Assert(Candidates(report).Count == 0);
            });
        check("Valid AI reorders take precedence over suspected-failure exclusion", () =>
        {
            string[] order = [Core, "fault.mod", "healthy.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("fault.mod", "healthy.mod"), Mod("healthy.mod")]);
            var client = new Client([new("reorder", "healthy.mod", 2, false, null, null)]);
            var session = new Session(n => n == 1 ? Failure() : new(true, []));
            var result = Run(client, session, Request(order, catalogue), catalogue);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.TrialExclusions.Count == 0
                && session.Orders[1].SequenceEqual([Core, "healthy.mod", "fault.mod"]), result.Message);
        });
        foreach (var failed in new[] { AiReplyFailure.ServiceFailure, AiReplyFailure.InvalidStructure, AiReplyFailure.OutputLimit })
            check(failed + " stops before any suspected-failure experiment", () =>
            {
                var client = new Client(failure: failed); var session = new Session(_ => Failure());
                var result = Run(client, session);
                Assert(result.Outcome == MaintenanceOutcome.Stopped && session.Applies == 0 && result.TrialExclusions.Count == 0
                    && client.Calls == 1, result.Message);
            });
        check("Invalid executable AI plan does not fall through to suspected-failure removal", () =>
        {
            var client = new Client([new("reorder", "invented.mod", 1, false, null, null)]);
            var session = new Session(_ => Failure()); var result = Run(client, session);
            Assert(result.ReplyFailure == AiReplyFailure.InvalidPlan && session.Applies == 0 && result.TrialExclusions.Count == 0, result.Message);
        });
        check("Last permitted test cannot start an untestable exclusion", () =>
        {
            var session = new Session(_ => Failure()); var client = new Client();
            var result = Run(client, session, limits: Limits with { MaximumAttempts = 1 });
            Assert(result.Attempts == 1 && result.TrialExclusions.Count == 0 && session.Applies == 0 && client.Calls == 1, result.Message);
        });
        check("Exhausted token budget cannot be bypassed by private verification", () =>
        {
            var session = new Session(_ => Failure()); var client = new Client();
            var result = Run(client, session, limits: Limits with { TokenLimitEnabled = true, TokenBudget = 100 });
            Assert(result.TrialExclusions.Count == 0 && session.Applies == 0 && client.Calls == 0, result.Message);
        });
        check("Unchanged errors stop after one suspect instead of deleting incidental owners", () =>
        {
            var session = new Session(n => n == 1 ? Failure() : Failure("healthy.mod")); var client = new Client();
            var result = Run(client, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Attempts == 2 && result.TrialExclusions.Count == 1
                && client.Calls == 1 && result.OriginalRestored && session.Orders[1].Contains("healthy.mod"), result.Message);
        });
        check("Strictly reduced known errors permit a second distinct suspect without another paid diagnosis", () =>
        {
            var session = new Session(n => n switch
            {
                1 => Failure(signatures: ['A', 'B']) with { DiagnosticFacts = [new("GameTypeLoadFailure", "fault.mod"), new("GameTypeLoadFailure", "healthy.mod")],
                    LogContext = new([], [new("Fixture.Missing", "fault.mod", "assembly-type")], false) },
                2 => Failure("healthy.mod", signatures: ['B']),
                _ => new(true, [])
            });
            var client = new Client(); var result = Run(client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 3 && result.TrialExclusions.Count == 2
                && client.Calls == 1 && session.Orders[2].SequenceEqual([Core]), result.Message);
        });
        check("New failure after verification receives AI diagnosis and preserves earlier exclusions", () =>
        {
            var session = new Session(n => n switch { 1 => Failure(), 2 => Failure("healthy.mod", signatures: ['B']), _ => new(true, []) });
            var client = new Client(); var result = Run(client, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 2 && result.TrialExclusions.Count == 2
                && session.Orders[2].SequenceEqual([Core]), result.Message);
        });
        check("Recovery replays a journalled suspect without another paid request", () =>
        {
            var root = Path.Combine(Root, Guid.NewGuid().ToString("N")); var client = new Client();
            var interrupted = new Session(_ => Failure()) { InterruptApply = true };
            var first = Run(client, interrupted, root: root);
            Assert(first.TrialExclusions.Count == 1 && client.Calls == 1);
            var resumed = new Session(_ => new(true, []));
            var result = Run(client, resumed, root: root, resume: first.TaskId);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 1 && result.OriginalRestored
                && resumed.Orders.Single().SequenceEqual([Core, "healthy.mod"]), result.Message);
        });
        check("A saved valid empty answer can start verification without resending the model request", () =>
        {
            var root = Path.Combine(Root, Guid.NewGuid().ToString("N")); var client = new Client();
            var first = Run(client, new NoTrialSession(), root: root);
            Assert(first.Outcome == MaintenanceOutcome.Stopped && first.TrialExclusions.Count == 0 && client.Calls == 1);
            var result = Run(client, new Session(_ => new(true, [])), root: root, resume: first.TaskId);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 1, result.Message);
        });
        check("Interruption after journalling but before trial authorization is recoverable", () =>
        {
            var root = Path.Combine(Root, Guid.NewGuid().ToString("N")); var client = new Client();
            var first = Run(client, new Session(_ => Failure()) { InterruptAuthorization = true }, root: root);
            var result = Run(client, new Session(_ => new(true, [])), root: root, resume: first.TaskId);
            Assert(first.TrialExclusions.Count == 1 && result.Outcome == MaintenanceOutcome.Passed && client.Calls == 1, result.Message);
        });
        check("Recovery preserves diagnostic-only survivor order and never silently sorts it", () =>
        {
            string[] order = [Core, "healthy.mod", "base.mod", "fault.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("healthy.mod", "base.mod"), Mod("base.mod"), Mod("fault.mod")]);
            var root = Path.Combine(Root, Guid.NewGuid().ToString("N")); var client = new Client(); var request = Request(order, catalogue);
            var first = Run(client, new Session(_ => Failure()) { InterruptApply = true }, request, catalogue, root: root);
            var resumed = new Session(_ => new(true, []));
            var result = Run(client, resumed, request, catalogue, root: root, resume: first.TaskId);
            Assert(result.TrialExclusions.Count == 1 && client.Calls == 1
                && resumed.Orders.Single().SequenceEqual([Core, "healthy.mod", "base.mod"]), result.Message);
        });
        check("Changed retention rules reject old verification authority on resume", () =>
        {
            var root = Path.Combine(Root, Guid.NewGuid().ToString("N")); var client = new Client();
            var first = Run(client, new Session(_ => Failure()) { InterruptApply = true }, root: root);
            var session = new Session(_ => new(true, []));
            var result = Run(client, session, Request(retained: ["fault.child"]), root: root, resume: first.TaskId);
            Assert(result.Outcome == MaintenanceOutcome.RecoveryRequired && session.Applies == 0 && client.Calls == 1, result.Message);
        });
        check("Private unchanged-survivor tests may preserve prior order violations but publication stays strict", () =>
        {
            string[] order = [Core, "healthy.mod", "base.mod", "fault.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("healthy.mod", "base.mod"), Mod("base.mod"), Mod("fault.mod")]);
            var trial = Candidates(Failure(), catalogue, order).Single();
            string[] target = [Core, "healthy.mod", "base.mod"];
            Assert(TrialExclusionPlanning.Plan(order, target, catalogue, [], Version, [], [trial], privateDiagnostic: true).CanApply);
            Assert(!TrialExclusionPlanning.Plan(order, target, catalogue, [], Version, [], [trial]).CanApply);
            Assert(!AiOrderPlanner.ConfirmedTrialOrder(order, target, catalogue, [], Version, [], [], [trial], true).CanApply);
            var client = new Client(); var session = new Session(n => n == 1 ? Failure() : new(true, []));
            var result = Run(client, session, Request(order, catalogue), catalogue);
            Assert(result.TrialExclusions.Count == 1 && session.Orders[1].SequenceEqual(target) && client.Calls == 1, result.Message);
        });
        check("Private exception cannot authorize unrelated moves, unapproved removals or ordinary trials", () =>
        {
            string[] order = [Core, "healthy.mod", "base.mod", "fault.mod", "other.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("healthy.mod", "base.mod"), Mod("base.mod"), Mod("fault.mod"), Mod("other.mod")]);
            var trial = Candidates(Failure(), catalogue, order).Single();
            Assert(!TrialExclusionPlanning.Plan(order, [Core, "other.mod", "healthy.mod", "base.mod"], catalogue, [], Version, [], [trial], privateDiagnostic: true).CanApply);
            Assert(!TrialExclusionPlanning.Plan(order, [Core, "healthy.mod", "base.mod"], catalogue, [], Version, [], [trial], privateDiagnostic: true).CanApply);
            Assert(!TrialExclusionPlanning.Plan(order, [Core, "healthy.mod", "base.mod", "other.mod"], catalogue, [], Version, [],
                [trial with { SuspectedFailureVerification = false }], privateDiagnostic: true).CanApply);
        });
        check("Breaking an advisory cycle must not grant authority for newly exposed order violations", () =>
        {
            ModInstallation After(string id, string after) => Mod(id) with { Metadata = Mod(id).Metadata! with
                { Requirements = new([], [], [after], []) } };
            string[] order = [Core, "first.mod", "second.mod", "fault.mod"];
            var catalogue = new ModCatalogue([Mod(Core), After("first.mod", "second.mod"), After("second.mod", "fault.mod"), After("fault.mod", "first.mod")]);
            var trial = Candidates(Failure(), catalogue, order).Single();
            Assert(!TrialExclusionPlanning.Plan(order, [Core, "first.mod", "second.mod"], catalogue, [], Version, [],
                [trial], privateDiagnostic: true).CanApply);
        });
        check("Tampered suspect authority without failure evidence or mixed automatic-removal authority is rejected", () =>
        {
            var trial = Candidates(Failure()).Single();
            foreach (var invalid in new[] { trial with { DiagnosticCodes = ["GameLogError"] }, trial with { ReadFailureRemoval = true } })
            {
                try { invalid.Validate(); } catch (InvalidDataException) { continue; }
                throw new InvalidOperationException("Tampered authority accepted");
            }
        });
        foreach (var error in new[] { "System.BadImageFormatException: invalid image", "System.IO.FileLoadException: could not load", "Could not load file or assembly 'Fixture'" })
            check("Corrupt or incompatible binary is projected to structured evidence: " + error, () =>
            {
                var issue = new GameLogIssue(error, 1, ["fault.mod"], error);
                var log = new GameLogSnapshot("fixture.log", DateTimeOffset.UtcNow, error, [issue], false);
                Assert(GameTestDiagnostics.Project([log]).Any(f => f.Code == "GameTypeLoadFailure" && f.PackageId == "fault.mod"));
            });
        foreach (var error in new[] { "System.Xml.XmlException: malformed document", "XML error: <bad> doesn't correspond to any field",
            "Texture's contents are not the format its extension names", "Type GameComponent is not a Def type or could not be found" })
            check("Invalid content has bounded local diagnostic code: " + error, () =>
            {
                var log = new GameLogSnapshot("fixture.log", DateTimeOffset.UtcNow, error, [new(error, 1, ["fault.mod"], error)], false);
                var facts = GameTestDiagnostics.Project([log]);
                Assert(facts.Any(f => f.Code == "GameModContentFailure" && f.PackageId == "fault.mod"));
                Assert(Candidates(Failure() with { DiagnosticFacts = facts }).Count == 1);
                var unknown = GameTestDiagnostics.Project([log with { Issues = [new(error, 1, [], error)] }]);
                Assert(Candidates(Failure() with { DiagnosticFacts = unknown }).Count == 0);
            });
    }

    internal static void Replay(string zipPath, string evidenceDirectory)
    {
        Directory.CreateDirectory(evidenceDirectory);
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry("tasks/c5b6247db47245ffaf57a4fd3317b805.json")!.Open());
        using var task = JsonDocument.Parse(reader.ReadToEnd());
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var checkpoint = task.RootElement.GetProperty("checkpoint");
        var feedback = checkpoint.GetProperty("feedback").Deserialize<MaintenanceFeedback>(options)!;
        using var snapshotReader = new StreamReader(zip.GetEntry("Data/GameTests/game-4293922c8745434191df6bbcdef5763b/catalogue-snapshot.json")!.Open());
        using var snapshot = JsonDocument.Parse(snapshotReader.ReadToEnd());
        var installations = snapshot.RootElement.GetProperty("Installations").EnumerateArray().Select(m =>
        {
            var req = m.GetProperty("Requirements"); var id = m.GetProperty("PackageId").GetString()!;
            string[] Strings(string field) => req.GetProperty(field).EnumerateArray().Select(v => v.GetString()!).ToArray();
            var deps = req.GetProperty("Dependencies").EnumerateArray().Select(v => new ModDependency(v.GetProperty("PackageId").GetString()!, v.GetProperty("DisplayName").GetString()!)).ToArray();
            return Mod(id) with { Source = (ModSource)m.GetProperty("Source").GetInt32(),
                WorkshopId = m.GetProperty("WorkshopId").GetString()!,
                Metadata = new(m.GetProperty("Name").GetString()!, "redacted", id, ["1.7"], "",
                    new(deps, Strings("LoadBefore"), Strings("LoadAfter"), Strings("IncompatibleWith")), false) };
        }).ToArray();
        var catalogue = new ModCatalogue(installations);
        var candidates = Candidates(feedback.Restore(), catalogue, feedback.TestedOrder);
        Assert(candidates.Count > 0 && candidates.All(c => c.SuspectedFailureVerification));
        var executable = candidates.Where(t => TrialExclusionPlanning.Plan(feedback.TestedOrder,
            feedback.TestedOrder.Except(t.RemovedIds, StringComparer.OrdinalIgnoreCase).ToArray(), catalogue, [], Version, [], [t], privateDiagnostic: true).CanApply).ToArray();
        Assert(executable.Length > 0, "No archived candidate executable");
        var session = new Session(n => n == 1 ? feedback.Restore() : new(true, [])); var client = new Client();
        var result = Run(client, session, Request(feedback.TestedOrder, catalogue), catalogue);
        Assert(result.TrialExclusions.Count == 1 && session.Orders.Count == 2 && client.Calls == 1, result.Message);
        File.WriteAllText(Path.Combine(evidenceDirectory, "archive-replay.json"), JsonSerializer.Serialize(new
        {
            Input = Path.GetFileName(zipPath), ArchivedReportedTokens = checkpoint.GetProperty("reportedTokens").GetInt32(),
            ArchivedAnalysis = feedback.Analysis, NewNetworkRequests = 0, NewBilledTokens = 0,
            RealGameRetest = false, SyntheticRetestResult = true, CandidateCount = candidates.Count,
            ExecutableCandidates = executable.Select(t => new { t.SelectedId, t.RemovedIds, t.DiagnosticCodes }),
            result.Outcome, result.Attempts, client.Calls, Trials = result.TrialExclusions
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static IReadOnlyList<TrialExclusion> Candidates(MaintenanceGameReport report, ModCatalogue? catalogue = null,
        string[]? order = null, string[]? retained = null) =>
        SuspectedModFailureTrials.Candidates(report, catalogue ?? Catalogue(), order ?? Order, retained ?? [], Version, 1);
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = "{\"schemaVersion\":1,\"summary\":\"疑似模组加载失败\",\"actions\":[],\"warnings\":[],\"conflicts\":[]}" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 10, total_tokens = 20 }
                }), Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class Client(AiAction[]? actions = null, AiReplyFailure failure = AiReplyFailure.None, string category = "mod") : IAiClient
    {
        internal int Calls;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            Calls++; Assert(Calls < 5, "Unexpected repeated paid request");
            return Task.FromResult(new AiClientResult(failure == AiReplyFailure.None, "synthetic", failure == AiReplyFailure.None
                ? new(1, "疑似损坏或版本不兼容，需验证而非断言根因", actions ?? [], [])
                    { StartupDiagnosis = request.StartupContext is null ? null : new(category, "synthetic", []) } : null, null)
                { Usage = new(10, 10, 20), Failure = failure });
        }
    }
    private sealed class Session(Func<int, MaintenanceGameReport> report) : ILevelOneGameSession, IMaintenanceTrialSession
    {
        internal readonly List<string[]> Orders = []; internal int Applies; internal bool Finished;
        internal bool InterruptApply, InterruptAuthorization;
        private HashSet<string> authorized = new(StringComparer.OrdinalIgnoreCase);
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        { Orders.Add(order.ToArray()); Assert(Orders.Count <= 6, "Runaway verification"); return Task.FromResult(report(Orders.Count)); }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token)
        {
            if (InterruptApply) throw new MaintenancePauseException("synthetic interruption before applying private trial");
            Assert(plan.CanApply && plan.Before.Except(plan.After, StringComparer.OrdinalIgnoreCase).All(authorized.Contains)); Applies++;
            return Task.CompletedTask;
        }
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials)
        {
            if (InterruptAuthorization) throw new MaintenancePauseException("synthetic interruption after journalling");
            foreach (var trial in trials) trial.Validate();
            authorized = trials.SelectMany(t => t.RemovedIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token) { Finished = true; return Task.CompletedTask; }
    }
    private sealed class NoTrialSession : ILevelOneGameSession
    {
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token) => Task.FromResult(Failure());
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) => throw new InvalidOperationException("No private trial authority");
        public Task FinishAsync(bool accepted, CancellationToken token) => Task.CompletedTask;
    }
    private static void Assert(bool value, string message = "Suspected mod failure regression")
    { if (!value) throw new InvalidOperationException(message); }
}
