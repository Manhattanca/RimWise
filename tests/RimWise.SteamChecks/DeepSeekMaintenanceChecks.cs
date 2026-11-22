using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;
using RimWise.Integration.Diagnostics;
using RimWise.TestHelper;

// Entire request -> parser -> planner -> journal -> executor -> retest path, with an
// in-memory HTTP handler and synthetic session. No AI, game or Steam is contacted.
internal static class DeepSeekMaintenanceChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "deepseek-maintenance", Guid.NewGuid().ToString("N"));
    private static readonly GameVersion Version = new(1, 7);
    private const string Core = "ludeon.rimworld";
    private static ModInstallation Mod(string id, params string[] deps) => new(Path.Combine(Root, id), id,
        id == Core ? ModSource.Game : ModSource.Local, "", new(id, "fixture", id, ["1.7"], "",
            new(deps.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
    private static readonly MaintenanceLimits Limits = new(5) { TokenLimitEnabled = false };
    private static MaintenanceGameReport Failure() => new(true, ["synthetic patch error", "other non-orderable error"])
    { CanDiagnose = true, DiagnosticFacts = [new("GamePatchFailure", "leaf.mod")], ErrorSignatures = [new('A', 64), new('B', 64)] };
    private static object Move(string id, object index) => new { kind = "reorder", packageId = id, targetIndex = index };
    private static object Add(string id, int index) => new { kind = "add", packageId = id, targetIndex = index, requiredDependency = true };
    private static string Answer(object[] actions, string? category = null) => JsonSerializer.Serialize(new
    { schemaVersion = 1, summary = "合成分析，不代表已修复", actions, warnings = new[] { "仍需复测其他错误" }, conflicts = Array.Empty<object>(),
        startupDiagnosis = category is null ? null : new { category, reason = "合成失败证据", suggestions = Array.Empty<string>() } });
    private static StartupFailureDetails Startup(params string[] codes) => new(new("ProbeCompleted", true, true,
        codes.Select(c => new AiStartupEvidence(c)).ToArray()), []);
    private static AiRequest Request(string[] order, ModCatalogue catalogue, string model = "deepseek-flash") => new(
        AiSettings.Default with { Model = model, Endpoint = "https://api.deepseek.com/v1/chat/completions", StreamResponses = false },
        "synthetic-key", "1.7", order, catalogue.Installations.Select(m => catalogue.IdentifierFor(m, Version)).ToArray(), [], [])
        { ModRequirements = AiModRequirementFact.FromCatalogue(catalogue, order, Version) };

    internal static void Run(Action<string, Action> check)
    {
        check("DeepSeek executes a required-order fix despite unrelated non-orderable errors and first tests the untouched order", () =>
        {
            string[] order = [Core, "leaf.mod", "base.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod", "base.mod"), Mod("base.mod")]);
            using var handler = new Handler(Answer([Move("base.mod", 2)], "mod"));
            var session = new Session(n => n == 1 ? Failure() with { StartupVerified = false,
                StartupFailure = Startup("PawnTestFailure") } : new(true, []));
            var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && handler.Calls == 1 && session.Applies == 1
                && session.Orders.Count == 2 && session.Orders[0].SequenceEqual(order)
                && session.Orders[1].SequenceEqual([Core, "base.mod", "leaf.mod"]) && result.TrialExclusions.Count == 0, result.Message);
        });
        check("DeepSeek adds installed recursive prerequisites in one validated proposal", () =>
        {
            string[] order = [Core, "leaf.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod", "middle.mod"), Mod("middle.mod", "base.mod"), Mod("base.mod")]);
            using var handler = new Handler(Answer([Add("base.mod", 2), Add("middle.mod", 3)]));
            var session = new Session(n => n == 1 ? Failure() : new(true, []));
            var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && handler.Calls == 1 && session.Applies == 1
                && session.Orders[1].SequenceEqual([Core, "base.mod", "middle.mod", "leaf.mod"]), result.Message);
        });
        check("DeepSeek sequential moves use updated indices rather than original row numbers", () =>
        {
            string[] order = [Core, "leaf.mod", "middle.mod", "base.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod", "middle.mod"), Mod("middle.mod", "base.mod"), Mod("base.mod")]);
            using var handler = new Handler(Answer([Move("base.mod", "2"), Move("middle.mod", "3")]));
            var session = new Session(n => n == 1 ? Failure() : new(true, []));
            var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && session.Orders[1].SequenceEqual([Core, "base.mod", "middle.mod", "leaf.mod"]), result.Message);
        });
        check("DeepSeek canonical Workshop IDs and quoted integers resolve only to the unique active installation", () =>
        {
            string[] order = [Core, "leaf.mod", "base.mod_steam"];
            var workshop = Mod("base.mod") with { Source = ModSource.Workshop, WorkshopId = "123" };
            var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod", "base.mod"), workshop]);
            using var handler = new Handler(Answer([Move("base.mod", "2")]));
            var session = new Session(n => n == 1 ? Failure() : new(true, []));
            var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && session.Orders[1].SequenceEqual([Core, "base.mod_steam", "leaf.mod"]), result.Message);
        });
        check("A half-fixed dependency chain remains atomic, never partly applies or silently invokes local sorting", () =>
        {
            string[] order = [Core, "leaf.mod", "middle.mod", "base.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod", "middle.mod"), Mod("middle.mod", "base.mod"), Mod("base.mod")]);
            using var handler = new Handler(Answer([Move("base.mod", 2)]));
            var session = new Session(_ => Failure()); var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == AiReplyFailure.InvalidPlan
                && handler.Calls == 1 && session.Applies == 0 && session.Orders.Count == 1 && result.LastAttemptedOrder.SequenceEqual(order), result.Message);
        });
        check("A live-provider regression cannot move a middle dependency ahead of its own repaired prerequisite", () =>
        {
            string[] order = [Core, "leaf.mod", "middle.mod", "base.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod", "middle.mod"), Mod("middle.mod", "base.mod"), Mod("base.mod")]);
            // Actual live failure pattern: each reason sounded plausible, but the final
            // order was Core,middle,leaf,base. Do not rewrite or partly apply it.
            using var handler = new Handler(Answer([Move("base.mod", 3), Move("middle.mod", 2)]));
            var session = new Session(_ => Failure());
            var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == AiReplyFailure.InvalidPlan
                && session.Applies == 0 && handler.Calls == 1 && session.Orders.Count == 1
                && result.LastAttemptedOrder.SequenceEqual(order), result.Message);
        });
        check("A complete longer-chain proposal executes exact sequential moves without locally guessing a repair", () =>
        {
            var chain = Enumerable.Range(1, 6).Select(i => "chain." + i).ToArray();
            string[] order = [Core, .. chain.Reverse()];
            var catalogue = new ModCatalogue([Mod(Core), .. chain.Select((id, i) => Mod(id, i == 0 ? [] : [chain[i - 1]]))]);
            using var handler = new Handler(Answer(chain.Take(5).Select((id, i) => Move(id, i + 2)).ToArray()));
            var session = new Session(n => n == 1 ? Failure() : new(true, []));
            var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Passed && session.Applies == 1 && handler.Calls == 1
                && session.Orders[1].SequenceEqual(new[] { Core }.Concat(chain)), result.Message);
        });
        check("Empty DeepSeek actions with a claimed fix never become executable instructions", () =>
        {
            string[] order = [Core, "leaf.mod"]; var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod")]);
            using var handler = new Handler(Answer([]).Replace("合成分析，不代表已修复", "已经修复"));
            var session = new Session(_ => Failure()); var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && handler.Calls == 1 && session.Applies == 0
                && result.Message.Contains("未提出可执行") && session.Orders.Count == 1, result.Message);
        });
        check("A live-provider conflict with an extra reason field remains a single atomic schema rejection", () =>
        {
            string[] order = [Core, "leaf.mod", "base.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod"), Mod("base.mod")]);
            var answer = Answer([]).Replace("\"conflicts\":[]",
                "\"conflicts\":[{\"packageIds\":[\"leaf.mod\",\"base.mod\"],\"reason\":\"unsupported extra field\"}]");
            using var handler = new Handler(answer);
            var session = new Session(_ => Failure());
            var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == AiReplyFailure.InvalidStructure
                && handler.Calls == 1 && session.Applies == 0 && session.Orders.Count == 1, result.Message);
        });
        check("A live-provider nested startupDiagnosis is rejected without replaying its public analysis as actions", () =>
        {
            string[] order = [Core, "leaf.mod"];
            var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod")]);
            var answer = Answer([], "unknown").Replace("\"suggestions\":[]",
                "\"suggestions\":[],\"startupDiagnosis\":null");
            using var handler = new Handler(answer);
            var session = new Session(_ => Failure() with { StartupVerified = false, StartupFailure = Startup("WorldGenerationFailure") });
            var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == AiReplyFailure.InvalidStructure
                && handler.Calls == 1 && session.Applies == 0 && session.Orders.Count == 1, result.Message);
        });
        check("A repeated DeepSeek candidate stops without a third test or unrelated exclusions", () =>
        {
            string[] order = [Core, "leaf.mod", "base.mod"]; var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod"), Mod("base.mod")]);
            using var handler = new Handler(Answer([Move("base.mod", 2)]));
            var session = new Session(_ => Failure()); var result = Run(Request(order, catalogue), catalogue, handler, session);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && handler.Calls == 2 && session.Applies == 1
                && session.Orders.Count == 2 && result.TrialExclusions.Count == 0, result.Message);
        });
        check("Order mismatch blocks DeepSeek actions and remains explicit after checkpoint recovery without billing again", () =>
        {
            foreach (var legacyEligibility in new[] { false, true })
            {
            string[] order = [Core, "leaf.mod", "base.mod"]; var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod"), Mod("base.mod")]);
            using var handler = new Handler(Answer([Move("base.mod", 2)], "mod"));
            var session = new Session(_ => Failure() with { StartupVerified = false, CanDiagnose = legacyEligibility,
                StartupFailure = Startup("TestOrderMismatch") });
            var request = Request(order, catalogue); var root = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            var result = Run(request, catalogue, handler, session, root);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Message.Contains("实际加载顺序")
                && session.Applies == 0 && handler.Calls == 1, result.Message);
            var next = new Session(_ => throw new Exception("Should reuse the saved evidence"));
            var resumed = Run(request, catalogue, handler, next, root, result.TaskId);
            Assert(resumed.Outcome == MaintenanceOutcome.Stopped && resumed.Message.Contains("实际加载顺序")
                && next.Applies == 0 && next.Orders.Count == 0 && handler.Calls == 1, resumed.Message);
            }
        });
        check("DeepSeek environment and unknown startup diagnoses cannot authorize plausible actions", () =>
        {
            foreach (var category in new[] { "environment", "unknown" })
              {
                string[] order = [Core, "leaf.mod", "base.mod"]; var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod"), Mod("base.mod")]);
                using var handler = new Handler(Answer([Move("base.mod", 2)], category));
                var session = new Session(_ => Failure() with { StartupVerified = false, StartupFailure = Startup("PawnTestFailure") });
                var result = Run(Request(order, catalogue), catalogue, handler, session);
                Assert(result.Outcome == MaintenanceOutcome.Stopped && session.Applies == 0 && handler.Calls == 1, result.Message);
              }
        });
        check("GPT still executes strict numeric proposals and rejects DeepSeek-only integer coercion", () =>
        {
            foreach (object index in new object[] { 2, "2" })
              {
                string[] order = [Core, "leaf.mod", "base.mod"]; var catalogue = new ModCatalogue([Mod(Core), Mod("leaf.mod", "base.mod"), Mod("base.mod")]);
                using var handler = new Handler(Answer([Move("base.mod", index)]));
                var session = new Session(n => n == 1 ? Failure() : new(true, []));
                var result = Run(Request(order, catalogue, "gpt-test"), catalogue, handler, session);
                Assert(result.Outcome == (index is int ? MaintenanceOutcome.Passed : MaintenanceOutcome.Stopped)
                    && session.Applies == (index is int ? 1 : 0) && handler.Calls == 1, result.Message);
                Assert(!handler.Payload!.Contains("DeepSeek 专项证据摘要"));
              }
        });
        check("Loaded order snapshot records the exact first difference without accepting aliases, missing mods or transpositions", () =>
        {
            string[] expected = [Core, "中文.补丁<&", "base.mod_steam"];
            foreach (var actual in new[] { expected.ToArray(), new[] { Core.ToUpperInvariant(), expected[1], expected[2] },
                new[] { Core, expected[2], expected[1] }, new[] { Core, expected[1], "base.mod" }, expected[..2], expected.Append("extra.mod").ToArray() })
              {
                var doc = ProbeOrderEvidence.Create(Guid.NewGuid().ToString("N"), 123, expected, actual);
                var root = doc.DocumentElement!;
                Assert((root.GetAttribute("matches") == "1") == expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase));
                Assert(doc.SelectNodes("/loadedOrder/actual/li")!.Cast<System.Xml.XmlNode>().Select(n => n.InnerText).SequenceEqual(actual));
                Assert(doc.SelectNodes("/loadedOrder/expected/li")!.Cast<System.Xml.XmlNode>().Select(n => n.InnerText).SequenceEqual(expected));
                Assert(root.GetAttribute("firstDifference") == (actual.Length == 4 ? "4" : actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)
                    ? "0" : actual.Length == 2 || actual[2] == "base.mod" ? "3" : "2"));
              }
        });
        check("Loaded order snapshot rejects unbounded or malformed data instead of truncating it into proof", () =>
        {
            foreach (var actual in new[] { new[] { "bad\nid" }, new[] { new string('x', 513) }, Enumerable.Repeat(new string('x', 512), 600).ToArray() })
              {
                try { ProbeOrderEvidence.Create(Guid.NewGuid().ToString("N"), 123, [Core], actual); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Invalid snapshot accepted");
              }
        });
        check("Diagnostic export retains the loaded-order comparison without widening its file allowlist", () =>
        {
            var root = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            var attempt = Path.Combine(root, "Data", "GameTests", "game-" + Guid.NewGuid().ToString("N"), "attempt-01");
            Directory.CreateDirectory(attempt);
            ProbeOrderEvidence.Create(Guid.NewGuid().ToString("N"), 123, [Core, "fixture.a", "fixture.b"], [Core, "fixture.b", "fixture.a"])
                .Save(Path.Combine(attempt, "workbench-loaded-order.xml"));
            File.WriteAllText(Path.Combine(attempt, "workbench-extra.xml"), "unrelated personal content");
            var target = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
            var exported = DiagnosticExportService.Export(root, target);
            using var archive = ZipFile.OpenRead(exported.Path);
            var snapshot = archive.Entries.Single(e => e.FullName.EndsWith("/workbench-loaded-order.xml"));
            using var stream = snapshot.Open(); var doc = System.Xml.Linq.XDocument.Load(stream);
            Assert((string?)doc.Root!.Attribute("firstDifference") == "2"
                && doc.Root.Element("actual")!.Elements("li").Select(e => e.Value).SequenceEqual([Core, "fixture.b", "fixture.a"])
                && !archive.Entries.Any(e => e.FullName.EndsWith("workbench-extra.xml")) && exported.OmissionCount == 0);
        });
    }
    private static MaintenanceResult Run(AiRequest request, ModCatalogue catalogue, Handler handler, Session session,
        string? root = null, string? resume = null)
    {
        using var http = new HttpClient(handler, disposeHandler: false); using var client = new AiClient(http);
        return new LevelOneMaintenance(client, new AiTokenEstimator(root ?? Path.Combine(Root, Guid.NewGuid().ToString("N"))),
            session, resume, testOriginalFirst: true).RunAsync(request, catalogue, [], Limits, null, default).GetAwaiter().GetResult();
    }
    internal static void Replay(string extractedRoot)
    {
        // Read only this explicit exported attempt, never relocate into a live profile.
        var result = Directory.GetFiles(Path.Combine(extractedRoot, "Data", "GameTests"), "result.json", SearchOption.AllDirectories).Single();
        var directory = Path.GetDirectoryName(result)!;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var report = JsonSerializer.Deserialize<OrdinaryGameReport>(File.ReadAllText(result), options)!;
        report = report with { Logs = report.Logs.Select(l => l with { Path = Path.Combine(directory, Path.GetFileName(l.Path)) }).ToArray() };
        using var context = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "diagnostic-context.json")));
        var logContext = context.RootElement.GetProperty("Logs").Deserialize<AiLogContext>(options)!;
        var feedback = OrdinaryGameTestSession.CreateFeedback(report, logContext, directory);
        Assert(report.ProbeStage == "order-mismatch" && !feedback.CanDiagnose && !feedback.StartupVerified
            && feedback.StartupFailure!.Context.Evidence.Any(e => e.Code == "TestOrderMismatch")
            && !feedback.StartupFailure.Context.CanAdjustOrder);
        Console.WriteLine(JsonSerializer.Serialize(new { ReportReplay = true, report.ProbeStage, report.CaptureCompleted,
            feedback.CanDiagnose, feedback.StartupVerified, feedback.StartupFailure!.Context.CanAdjustOrder,
            Reason = feedback.StartupFailure.LocalReason(),
            ActualOrderSnapshotAvailable = File.Exists(Path.Combine(directory, "workbench-loaded-order.xml")),
            NewAiRequest = false, RealGameRetest = false }));
    }
    internal static void ReplayProviderReply(string path)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        var answer = saved.RootElement.GetProperty("PublicContent").GetString()
            ?? throw new InvalidDataException("Saved public final answer missing");
        string[] order = [Core, "fixture.a", "fixture.b"];
        var catalogue = new ModCatalogue([Mod(Core), Mod("fixture.a"), Mod("fixture.b")]);
        using var handler = new Handler(answer);
        var session = new Session(_ => Failure() with { StartupVerified = false, DiagnosticFacts = [],
            StartupFailure = Startup("WorldGenerationFailure"), LogContext = new([], [], false) });
        var result = Run(Request(order, catalogue, "deepseek-v4-pro"), catalogue, handler, session);
        Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == AiReplyFailure.None
            && handler.Calls == 1 && session.Applies == 0 && result.StartupFailure?.Diagnosis?.Category == "unknown", result.Message);
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, ReplayedPublicFinalAnswer = true,
            InputSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
            NewNetworkRequests = 0, NewBilledTokens = 0, OfflineEnvelopeUsageSynthetic = true,
            result.Outcome, result.ReplyFailure, session.Applies, Category = result.StartupFailure?.Diagnosis?.Category }));
    }
    private sealed class Handler(string answer) : HttpMessageHandler
    {
        internal int Calls; internal string? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert(Calls <= 3); Payload = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Session(Func<int, MaintenanceGameReport> report) : ILevelOneGameSession
    {
        internal readonly List<string[]> Orders = []; internal int Applies;
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        { Orders.Add(order.ToArray()); Assert(Orders.Count < 5); return Task.FromResult(report(Orders.Count)); }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) { Assert(plan.CanApply); Applies++; return Task.CompletedTask; }
        public Task FinishAsync(bool accepted, CancellationToken token) => Task.CompletedTask;
    }
    private static void Assert(bool value, string message = "DeepSeek end-to-end regression")
    { if (!value) throw new InvalidOperationException(message); }
}
