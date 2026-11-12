using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Game;

internal static class MaintenanceDiagnosisChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "诊断 回归", Guid.NewGuid().ToString("N"));
    private static readonly AiSettings Settings = AiSettings.Default with { Enabled = true, Model = "test", Endpoint = "http://127.0.0.1/v1/" };
    private static void Assert(bool value, string message = "Diagnosis regression") { if (!value) throw new Exception(message); }
    private static ModInstallation Mod(string id, params string[] dependencies) => new(Path.Combine(Root, id), id,
        id == "ludeon.rimworld" ? ModSource.Game : ModSource.Local, "", new(id, "test", id, ["1.7"], "",
            new(dependencies.Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
    private static readonly string[] Order = ["ludeon.rimworld", "test.cause", "test.dependent", "test.keep"];
    private static ModCatalogue Catalogue() => new([Mod(Order[0]), Mod(Order[1]), Mod(Order[2], Order[1]), Mod(Order[3])]);
    private static AiRequest Request() => new(Settings, null, "1.7", Order, Order, [], []);
    private static OrdinaryGameReport FailedReport() => new(123, 0, GameTestStopReason.LogLimit, true, true,
        [new(Path.Combine(Root, "Player.log"), DateTimeOffset.UtcNow, "TypeLoadException: Could not load type 'Test.Missing'",
            [new("TypeLoadException: Could not load type 'Test.Missing'", 1, [Order[1]], "TypeLoadException: Could not load type 'Test.Missing'")], true)]);
    private static MaintenanceGameReport Feedback(OrdinaryGameReport report) =>
        OrdinaryGameTestSession.CreateFeedback(report, new([], [], false), Root);

    internal static void Run(Action<string, Action> check)
    {
        Directory.CreateDirectory(Root);
        GameLogSnapshot OverlayLog(string extra = "")
        {
            const string prefix = "[Vulkan] [General] [Loader Message] loader_get_json: Failed to open JSON file ";
            var text = string.Join('\n', new[] {
                @"C:\Program Files (x86)\Overwolf\0.283.0.11\ow-vulkan-overlay64.json",
                @"E:\Program Files\Overwolf\0.283.0.11\ow-graphics-vulkan64.json",
                @"C:\Program Files (x86)\Epic Games\Epic Online Services\managedArtifacts\abc\EOSOverlayVkLayer-Win64.json"
            }.Select(p => "2026-11-12 12:00:00.000 ERROR " + prefix + p)) + "\n" + extra;
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllText(path, text);
            return PlayerLogReader.Read(path) with { Kind = "game-errors" };
        }
        OrdinaryGameReport Completed(GameLogSnapshot log) =>
            new(123, 0, GameTestStopReason.ProbeCompleted, true, true, [log])
            {
                ProbeStage = "map-ready", ProbeTicks = 250, ProbeColonists = 2, ProbeErrors = 3,
                MenuPassed = true, FactionsPassed = true, PawnsTested = 1
            };
        check("Approved Overwolf and Epic Vulkan errors pass with a warning, no AI or trial removals, and recover without retesting", () =>
        {
            var log = OverlayLog();
            var report = Completed(log);
            var feedback = Feedback(report);
            Assert(log.Issues.Count == 0 && log.ExcludedErrors.Count == 3 && log.Text.Contains("Failed to open JSON")
                && PlayerLogReader.ExactErrorCounts(log.Path)!.Count == 0);
            var manyWarnings = log with { Warnings = Enumerable.Range(0, 150).Select(i =>
                new GameLogIssue("Warning", 1, [$"test.mod{i}"], "Optional warning")).ToArray() };
            var runtime = GameTestDiagnostics.ProjectWarnings([manyWarnings])!;
            Assert(runtime.Observations.Count == 100 && runtime.HasVulkanConfigurationWarning
                && runtime.CopyValidated().HasVulkanConfigurationWarning);
            Assert(report.MapSuccessVerified && feedback.StartupVerified && feedback.Errors.Count == 0
                && !feedback.CanDiagnose && feedback.StartupFailure is null
                && feedback.RuntimeContext?.HasVulkanConfigurationWarning == true && feedback.ErrorSignatures.Length == 0);
            var session = new OverlaySession(feedback, feedback);
            var client = new FailingClient();
            var root = Path.Combine(Root, "approved-overlay-only");
            var result = new LevelOneMaintenance(client, new AiTokenEstimator(root), session)
                .RunAsync(Request(), Catalogue(), [], new(5) { TokenLimitEnabled = false }, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 1 && result.Requests == 0
                && client.Calls == 0 && result.TrialExclusions.Count == 0 && session.Accepted
                && result.Message.Contains("Vulkan") && result.HasVulkanConfigurationWarning, result.Message);
            var saved = new AiTaskRecoveryStore(root).Inspect().Single().Checkpoint!;
            Assert(saved.Feedback is { StartupVerified: true, HadErrors: false, Passed: true }
                && saved.Feedback.RuntimeContext?.HasVulkanConfigurationWarning == true);
            // Simulate interruption after the durable test result, before completion.
            // The desktop suite separately exercises pending publication and failed saves.
            var resumeRoot = Path.Combine(Root, "approved-overlay-recovery");
            string resumeId;
            using (var interrupted = new AiTaskRecoveryStore(resumeRoot).Create(saved with { Phase = AiTaskPhase.TestSaved }))
                resumeId = interrupted.Id;
            var resumedSession = new OverlaySession(feedback, feedback);
            var resumed = new LevelOneMaintenance(client, new AiTokenEstimator(resumeRoot), resumedSession, resumeId)
                .RunAsync(Request(), Catalogue(), [], new(5) { TokenLimitEnabled = false }, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(resumed.Outcome == MaintenanceOutcome.Passed && resumedSession.Tests == 0
                && client.Calls == 0 && resumed.HasVulkanConfigurationWarning, "Recovery lost the warning or repeated work.");
        });
        check("Mixed overlay and mod errors continue sorting until only the approved Vulkan warning remains", () =>
        {
            var mixed = Completed(OverlayLog("2026-11-12 12:00:01.000 ERROR PatchOperationReplace failed\n")) with { ProbeErrors = 4 };
            var feedback = Feedback(mixed);
            Assert(feedback.Errors.Count == 1 && feedback.CanDiagnose && feedback.StartupFailure is null
                && feedback.RuntimeContext?.HasVulkanConfigurationWarning == true);
            var session = new OverlaySession(feedback, Feedback(Completed(OverlayLog())));
            var client = new OverlayClient();
            var result = new LevelOneMaintenance(client, new AiTokenEstimator(Path.Combine(Root, "mixed-overlay")), session)
                .RunAsync(Request(), Catalogue(), [], new(5) { TokenLimitEnabled = false }, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Passed && session.Tests == 2 && session.Applies == 1
                && session.Accepted && client.Calls == 1 && result.Requests == 1 && result.TrialExclusions.Count == 0
                && result.HasVulkanConfigurationWarning && !result.LastAttemptedOrder.SequenceEqual(Order), result.Message);
        });
        check("Vulkan exception cannot bypass missing map, incomplete capture, unknown helper errors or genuine driver failure", () =>
        {
            var log = OverlayLog();
            var report = Completed(log);
            foreach (var incomplete in new[] { report with { CaptureCompleted = false }, report with { ProcessStopped = false },
                report with { Logs = [log with { Truncated = true }] }, report with { ProbeStage = "waiting-map" },
                report with { Logs = [log with { ModResetError = "Mods reset" }] },
                report with { StopReason = GameTestStopReason.Exited, ExitCode = 1 } })
                Assert(!Feedback(incomplete).StartupVerified);
            Assert(Feedback(report with { ProbeErrors = 4 }).Errors.Count > 0);
            Assert(Feedback(report with { Logs = [log with { Kind = "startup" }] }).Errors.Count > 0);
            var driver = Completed(OverlayLog("2026-11-12 12:00:01.000 ERROR vkCreateInstance: Found no drivers\n"));
            Assert(!Feedback(driver).StartupVerified && Feedback(driver).Errors.Count > 0 && !Feedback(driver).CanDiagnose);
        });
        check("Only exact overlay vendor and filename pairs are excluded; FATAL and unrelated JSON errors remain", () =>
        {
            const string prefix = "[Vulkan] [General] [Loader Message] loader_get_json: Failed to open JSON file ";
            foreach (var path in new[] { @"C:\Overwolf\ow-vulkan-overlay64.json", @"E:\Overwolf\ow-graphics-vulkan64.json",
                @"E:\Epic Online Services\EOSOverlayVkLayer-Win64.json" })
            {
                var message = prefix + "\"" + path.ToUpperInvariant() + "\"";
                Assert(PlayerLogReader.Parse(message).Count == 0 && PlayerLogReader.ParseExcludedErrors(message).Count == 1);
                Assert(PlayerLogReader.Parse("2026-11-12 12:00:00.000 FATAL " + message).Count == 1);
            }
            foreach (var message in new[] { prefix + @"C:\Mods\ow-vulkan-overlay64.json",
                prefix + @"C:\Overwolf\other.json", prefix + @"C:\Epic Online Services\config.json",
                prefix + @"C:\Overwolf\..\Mods\ow-vulkan-overlay64.json", "Vulkan mod failed to load JSON file C:\\Mods\\config.json" })
                Assert(PlayerLogReader.Parse("2026-11-12 12:00:00.000 ERROR " + message).Count == 1);
        });
        check("Missing model stops before game preparation, AI calls and usage records", () =>
        {
            var root = Path.Combine(Root, "missing-model");
            var session = new FakeSession(Feedback(FailedReport()));
            var client = new FailingClient();
            var controller = new LevelOneMaintenance(client, new AiTokenEstimator(root), session);
            var result = controller.RunAsync(Request() with { Settings = Settings with { Model = "" } },
                Catalogue(), [], new(3), null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Attempts == 0 && client.Calls == 0
                && !session.Began && !result.HasUnknownUsage && new AiTaskRecoveryStore(root).Inspect().Count == 0);
        });
        check("Output allowance grows only for a new tested order and stops paid requests at the ceiling", () =>
        {
            var root = Path.Combine(Root, "output-ceiling");
            var client = new TruncatedClient();
            var session = new RepeatedFailureSession();
            var controller = new LevelOneMaintenance(client, new AiTokenEstimator(root), session);
            var limits = new MaintenanceLimits(5, OutputTokensPerRequest: 16384)
                { TokenLimitEnabled = false, ExpandOutputBudget = true };
            var result = controller.RunAsync(Request(), Catalogue(), [], limits, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(client.Limits.SequenceEqual([16384, 32768]), result.Message);
            Assert(session.Attempts == 3 && result.Requests == 2 && result.ReportedTokens == 49172
                && result.Outcome == MaintenanceOutcome.Stopped && result.Message.Contains("输出额度"), result.Message);
            var saved = new AiTaskRecoveryStore(root).Inspect().Single().Checkpoint!;
            Assert(saved.OutputBudgetExhausted && saved.LastRequestOutputLimit == 32768 && saved.ReportedTokens == 49172);
            Assert(AiOutputBudget.Next(saved.Limits!, saved.LastRequestOutputLimit, saved.OutputBudgetExhausted) is null);
        });
        check("Adaptive output still respects total token budget and legacy opt-out", () =>
        {
            var limits = new MaintenanceLimits(5, OutputTokensPerRequest: 16384) { ExpandOutputBudget = true };
            var root = Path.Combine(Root, "output-budget");
            var client = new TruncatedClient();
            var session = new RepeatedFailureSession();
            var controller = new LevelOneMaintenance(client, new AiTokenEstimator(root), session);
            var result = controller.RunAsync(Request(), Catalogue(), [], limits with { TokenBudget = 40000 },
                null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(client.Limits.SequenceEqual([16384]) && result.Requests == 1 && result.Message.Contains("预算"), result.Message);
            Assert(AiOutputBudget.Next(new(5), 4096, true) is null
                && AiOutputBudget.Next(limits, 32768, false) == 32768);
        });
        check("Log limit keeps authenticated error evidence but cannot pass a map test", () =>
        {
            var feedback = Feedback(FailedReport());
            Assert(feedback.CanDiagnose && !feedback.StartupVerified && feedback.Errors.Count > 0);
            Assert(feedback.StartupFailure!.Context is { CompleteCapture: false, BoundedErrorCaptureVerified: true, CanAdjustOrder: true });
            var candidates = TrialExclusionPlanning.Candidates(feedback, Catalogue(), Order, [], new(1, 7), 1);
            Assert(candidates.Count == 1 && candidates[0].RemovedIds.ToHashSet().SetEquals([Order[1], Order[2]]));
            Assert(TrialExclusionPlanning.Candidates(feedback, Catalogue(), Order, [Order[2]], new(1, 7), 1).Count == 0);
        });
        check("Untrusted partial captures and mismatched orders remain blocked", () =>
        {
            foreach (var report in new[] { FailedReport() with { CaptureCompleted = false },
                FailedReport() with { ProcessStopped = false }, FailedReport() with { ProbeStage = "order-mismatch" },
                FailedReport() with { Logs = [] } }) Assert(!Feedback(report).CanDiagnose);
            Assert(!(FailedReport() with { StopReason = GameTestStopReason.Cancelled }).BoundedErrorCaptureVerified);
            var environment = FailedReport() with { Logs = [FailedReport().Logs[0] with { Text = "Unable to initialize Vulkan" }] };
            Assert(TrialExclusionPlanning.Candidates(Feedback(environment), Catalogue(), Order, [], new(1, 7), 1).Count == 0);
        });
        check("Log-limit feedback survives recovery without becoming complete or successful", () =>
        {
            var saved = MaintenanceFeedback.Capture(1, Order, Feedback(FailedReport()), new string('A', 64));
            var recovered = JsonSerializer.Deserialize<MaintenanceFeedback>(JsonSerializer.Serialize(saved))!.Restore();
            Assert(recovered.CanDiagnose && !recovered.StartupVerified
                && recovered.StartupFailure!.Context is { CompleteCapture: false, BoundedErrorCaptureVerified: true });
        });
        check("Only active dependency closure is sent, with full order and missing dependency IDs", () =>
        {
            var context = AiOutboundContext.Create(Request() with
            {
                ModRequirements = [new(Order[1], ["test.dep1", "test.missing"], [Order[3], "test.inactive"], [], []),
                    new("test.dep1", ["test.dep2"], [], [], []), new("test.dep2", ["test.dep1"], [], [], []),
                    new("test.inactive", [], [Order[1]], [], [])],
                InstalledMods = [.. Order, "test.dep1", "test.dep2", "test.inactive"]
            });
            Assert(context.CurrentOrder.SequenceEqual(Order) && context.InstalledMods.Count == 7);
            Assert(context.ModRequirements.Count == 3 && context.ModRequirements[0].Dependencies.Contains("test.missing"));
            Assert(context.ModRequirements[0].LoadBefore.SequenceEqual([Order[3]]));
        });
        check("Readable Unicode JSON preserves escaped data while reducing prompt size", () =>
        {
            var request = Request() with { CurrentOrder = ["中文.补丁"], InstalledMods = ["中文.补丁"],
                ModRequirements = [new("中文.补丁", ["中文.前置"], [], [], [])] };
            var outbound = AiPrompt.Messages(request)[1].Content;
            var previous = JsonSerializer.Serialize(AiOutboundContext.Create(request));
            Assert(outbound.Contains("中文.补丁") && !outbound.Contains("\\u4E"));
            Assert(Encoding.UTF8.GetByteCount(outbound) < Encoding.UTF8.GetByteCount(previous));
            using var json = JsonDocument.Parse(outbound);
            Assert(json.RootElement.GetProperty("CurrentOrder")[0].GetString() == "中文.补丁");
        });
        check("HTTP service failures keep status without persisting arbitrary provider content", () =>
        {
            using var handler = new ErrorHandler();
            using var http = new HttpClient(handler);
            using var client = new AiClient(http);
            var reply = client.GetSuggestionAsync(Request(), CancellationToken.None).GetAwaiter().GetResult();
            Assert(reply.Failure == AiReplyFailure.ServiceFailure && reply.ServiceError is { Kind: "http", HttpStatus: 413 });
            Assert(!JsonSerializer.Serialize(reply).Contains("private-provider-content") && handler.Calls == 1);
        });
        check("Service failure and unknown usage continue to reversible trial and clean retest", () =>
        {
            var session = new FakeSession(Feedback(FailedReport()));
            var client = new FailingClient();
            var controller = new LevelOneMaintenance(client, new AiTokenEstimator(Path.Combine(Root, "controller")), session);
            var visible = new List<MaintenanceProgress>();
            var result = controller.RunAsync(Request(), Catalogue(), [],
                new(3) { TokenLimitEnabled = false }, new CaptureProgress(visible), CancellationToken.None).GetAwaiter().GetResult();
            Assert(visible.Any(p => p.ReportText?.Contains("HTTP 413") == true)
                && visible.Any(p => p.Attempt == 2 && p.PreparedOrder?.SequenceEqual([Order[0], Order[3]]) == true),
                "Round report or current test order missing");
            Assert(result.Outcome == MaintenanceOutcome.Passed, result.Message);
            Assert(client.Calls == 1 && result.Attempts == 2 && result.HasUnknownUsage && result.OriginalRestored);
            Assert(result.TrialExclusions.Count == 1 && !result.TrialRemovalsApproved && session.Finished);
            Assert(result.LastAttemptedOrder.SequenceEqual([Order[0], Order[3]]));
        });
        check("Protected failure stops with safe status instead of removing required mods", () =>
        {
            var session = new FakeSession(Feedback(FailedReport()));
            var controller = new LevelOneMaintenance(new FailingClient(), new AiTokenEstimator(Path.Combine(Root, "protected")), session);
            var result = controller.RunAsync(Request() with { RetainedMods = [Order[2]] }, Catalogue(), [],
                new(3) { TokenLimitEnabled = false }, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Message.Contains("413")
                && result.TrialExclusions.Count == 0 && result.Attempts == 1);
        });
        check("Build diagnostics identify actual modules without exporting installation paths", () =>
        {
            var components = ApplicationBuildIdentity.Capture();
            Assert(components.Length == 4 && components.All(c => c.DiskSha256?.Length == 64
                && Guid.TryParse(c.LoadedModuleId, out _)));
            Assert(!JsonSerializer.Serialize(components).Contains(AppContext.BaseDirectory));
        });
    }

    private sealed class CaptureProgress(List<MaintenanceProgress> updates) : IProgress<MaintenanceProgress>
    {
        public void Report(MaintenanceProgress value) => updates.Add(value);
    }

    internal static void ReplayCompletedMapReports(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "result.json.gz", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();
        Assert(files.Length > 0, "No archived game reports found.");
        foreach (var file in files)
        {
            using var input = File.OpenRead(file);
            using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            var report = JsonSerializer.Deserialize<OrdinaryGameReport>(gzip)!;
            // Reparse the preserved evidence using today's exact exclusions; the archived
            // report's precomputed issue groups reflect the historical policy.
            var logs = report.Logs.Select(log => log with { Issues = PlayerLogReader.Parse(log.Text),
                Warnings = PlayerLogReader.ParseWarnings(log.Text), ExcludedErrors = PlayerLogReader.ParseExcludedErrors(log.Text) }).ToArray();
            var feedback = Feedback(report with { Logs = logs });
            Assert(report.MapSuccessVerified, "Replay expected a completed map.");
            var originalErrors = report.Logs.SelectMany(log => log.Issues).Count();
            Assert(feedback.RuntimeContext?.HasVulkanConfigurationWarning == true && feedback.StartupFailure is null);
            Assert(originalErrors == 5 ? feedback.Errors.Count == 2 && feedback.CanDiagnose
                : originalErrors == 3 && feedback.Errors.Count == 0 && !feedback.CanDiagnose,
                "Archived mixed errors must continue; the overlay-only result must pass.");
            Console.WriteLine(JsonSerializer.Serialize(new { Replay = "Completed map error classification",
                Attempt = Path.GetFileName(Path.GetDirectoryName(file)), report.MapSuccessVerified,
                OriginalErrors = originalErrors, Errors = feedback.Errors.Count,
                VulkanWarning = feedback.RuntimeContext?.HasVulkanConfigurationWarning == true, feedback.CanDiagnose,
                WouldPass = feedback.StartupVerified && feedback.Errors.Count == 0 }));
        }
    }

    internal static void Replay(string directory)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = Directory.EnumerateFiles(directory, "result.json", SearchOption.AllDirectories).Single();
        var report = JsonSerializer.Deserialize<OrdinaryGameReport>(File.ReadAllText(result), options)!;
        var contextPath = Path.Combine(Path.GetDirectoryName(result)!, "diagnostic-context.json");
        using var document = JsonDocument.Parse(File.ReadAllText(contextPath));
        var context = document.RootElement.GetProperty("Logs").Deserialize<AiLogContext>(options)!;
        var feedback = OrdinaryGameTestSession.CreateFeedback(report, context, Path.GetDirectoryName(result)!);
        Assert(feedback.CanDiagnose && !feedback.StartupVerified && feedback.StartupFailure!.Context.CanAdjustOrder);
        var order = feedback.DiagnosticFacts.Where(f => f.PackageId.Length > 0).Select(f => f.PackageId).Distinct().ToArray();
        var candidates = TrialExclusionPlanning.Candidates(feedback, new(order.Select(id => Mod(id)).ToArray()), order, [], new(1, 7), 1);
        // Synthetic catalogue here verifies only evidence eligibility, not the user's actual dependency constraints.
        Assert(candidates.Count > 0);
        Console.WriteLine(JsonSerializer.Serialize(new { Replay = "reported LogLimit", feedback.CanDiagnose,
            feedback.StartupVerified, feedback.StartupFailure!.Context.CompleteCapture,
            feedback.StartupFailure.Context.CanAdjustOrder, ErrorFacts = feedback.DiagnosticFacts.Count,
            SyntheticCandidateCount = candidates.Count }));
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
            { Content = new StringContent("{\"error\":\"private-provider-content\"}") }); }
    }
    private sealed class OverlayClient : IAiClient
    {
        public int Calls;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            Calls++;
            Assert(request.RuntimeContext?.HasVulkanConfigurationWarning == true);
            Assert(AiPrompt.Messages(request).Any(m => m.Content.Contains("不为此重排或试排除模组")));
            return Task.FromResult(new AiClientResult(true, "synthetic reorder", new(1, "Move the unrelated mod", [
                new("reorder", Order[3], 2, false, null, "synthetic reorder")], []), null) { Usage = new(100, 20, 120) });
        }
    }
    private sealed class OverlaySession(MaintenanceGameReport first, MaintenanceGameReport last) : ILevelOneGameSession
    {
        public int Tests;
        public int Applies;
        public bool Accepted;
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token) =>
            Task.FromResult(++Tests == 1 ? first : last);
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) { Assert(plan.CanApply); Applies++; return Task.CompletedTask; }
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token) { Accepted = acceptTestedOrder; return Task.CompletedTask; }
    }
    private sealed class FailingClient : IAiClient
    {
        public int Calls;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        { Calls++; return Task.FromResult(new AiClientResult(false, "Failure", null, null)
            { Failure = AiReplyFailure.ServiceFailure, ServiceError = new("http", 413) }); }
    }
    private sealed class FakeSession(MaintenanceGameReport first) : ILevelOneGameSession, IMaintenanceTrialSession
    {
        private int attempts;
        public int TestCalls => attempts;
        private bool authorized;
        public bool Finished;
        public bool Began;
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) { Began = true; return Task.CompletedTask; }
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token) =>
            Task.FromResult(++attempts == 1 ? first : new MaintenanceGameReport(true, []));
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token)
        { Assert(authorized && plan.CanApply); return Task.CompletedTask; }
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token)
        { Assert(!acceptTestedOrder); Finished = true; return Task.CompletedTask; }
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials)
        { Assert(trials.Count > 0); authorized = true; }
    }

    private sealed class TruncatedClient : IAiClient
    {
        internal readonly List<int> Limits = [];
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            var limit = request.OutputTokenLimit!.Value;
            Limits.Add(limit);
            return Task.FromResult(new AiClientResult(false, "Truncated", null, null)
                { Failure = AiReplyFailure.OutputLimit, Usage = new(10, limit, 10 + limit) });
        }
    }
    private sealed class RepeatedFailureSession : ILevelOneGameSession, IMaintenanceTrialSession
    {
        public int Attempts;
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        {
            Attempts++;
            var id = order.Contains(Order[1]) ? Order[1] : order.Contains(Order[3]) ? Order[3] : Order[0];
            var source = FailedReport();
            return Task.FromResult(Feedback(source with { Logs = [source.Logs[0] with
                { Issues = [new("TypeLoadException: Could not load type 'Test.Missing'", 1, [id], "TypeLoadException: Could not load type 'Test.Missing'")] }] }));
        }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) { Assert(plan.CanApply); return Task.CompletedTask; }
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token) { Assert(!acceptTestedOrder); return Task.CompletedTask; }
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials) { Assert(trials.Count > 0); }
    }
}
