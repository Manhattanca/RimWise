using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;

// Offline reproduction of 2026-11-25: a successful HTTP response with length,
// zero final text, and 16,384 reasoning tokens. No private logs or AI credentials.
internal static class DeepSeekOutputBudgetChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "deepseek-output", Guid.NewGuid().ToString("N"));
    private static AiSettings Settings => AiSettings.Default with { Model = "deepseek-flash", Endpoint = "https://api.deepseek.com/v1/chat/completions" };
    private static MaintenanceLimits Limits => new(5, OutputTokensPerRequest: 16384)
        { ExpandOutputBudget = true, TokenLimitEnabled = false };

    internal static void Run(Action<string, Action> check)
    {
        check("Official Flash reserves a larger first reasoning allowance only for new opted-in unbudgeted tasks", () =>
        {
            Assert(AiOutputBudget.Next(Limits, null, false, Settings) == 32768);
            foreach (var model in new[] { "deepseek-v4-pro", "deepseek-r1", "gpt-4.1", "unrecognized" })
                Assert(AiOutputBudget.Next(Limits, null, false, Settings with { Model = model }) == 16384);
            foreach (var host in new[] { "relayrax.com", "api.siliconflow.cn", "api.deepseek.com.relay.invalid", "127.0.0.1" })
                Assert(AiOutputBudget.Next(Limits, null, false, Settings with { Endpoint = "https://" + host + "/v1/chat/completions" }) == 16384);
            Assert(AiOutputBudget.Next(Limits, null, false, Settings with { Endpoint = "https://api.deepseek.com:8443/v1/chat/completions" }) == 16384);
            Assert(AiOutputBudget.Next(Limits with { ExpandOutputBudget = false }, null, false, Settings) == 16384);
            Assert(AiOutputBudget.Next(Limits with { TokenLimitEnabled = true }, null, false, Settings) == 16384);
            Assert(AiOutputBudget.Next(Limits with { OutputTokensPerRequest = 4096 }, null, false, Settings) == 4096);
            Assert(AiOutputBudget.Next(Limits, 16384, false, Settings) == 16384);
            Assert(AiOutputBudget.Next(Limits, 32768, true, Settings) is null);
        });
        foreach (var stream in new[] { false, true })
            check($"Reasoning-only length ({stream}) stays terminal, exact-accounted and non-replayable after recovery", () =>
            {
                var root = Path.Combine(Root, Guid.NewGuid().ToString("N"));
                var request = Request(stream); var catalogue = Catalogue();
                using var handler = new Handler(stream, success: false);
                using var http = new HttpClient(handler); using var client = new AiClient(http);
                var session = new Session();
                var result = new LevelOneMaintenance(client, new AiTokenEstimator(root), session, testOriginalFirst: true)
                    .RunAsync(request, catalogue, [], Limits, null, default).GetAwaiter().GetResult();
                Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == AiReplyFailure.OutputLimit
                    && result.ReportedTokens == 59168 && !result.HasUnknownUsage && result.Requests == 1
                    && handler.Calls == 1 && handler.Limit == 32768 && session.Tests == 1 && session.Applies == 0
                    && result.TrialExclusions.Count == 0 && result.LastAttemptedOrder.SequenceEqual(request.CurrentOrder), result.Message);
                Assert(result.Message.Contains("推理 16384") && result.Message.Contains("输入 42784")
                    && !result.Message.Contains("未找到可排除") && !result.Message.Contains("满足可恢复试排除"), result.Message);
                var store = new AiTaskRecoveryStore(root);
                var saved = store.Inspect().Single().Checkpoint!;
                var feedback = saved.Feedback!;
                Assert(saved.OutputBudgetExhausted && !saved.MayApply && saved.Actions.Length == 0
                    && feedback.ReplyFailure == AiReplyFailure.OutputLimit
                    && feedback.ServiceError?.Kind == (stream ? "output-limit" : null));
                saved.Validate();
                // An older generic classification remains valid and displays the specific cause.
                if (stream)
                {
                    var legacy = saved with { Feedback = feedback with { ReplyFailure = AiReplyFailure.ServiceFailure } };
                    legacy.Validate();
                    Assert(AiReplyFailureText.ServiceFailureKind(legacy.Feedback!.ServiceError, legacy.Feedback.ReplyFailure)
                        == AiReplyFailure.OutputLimit);
                    var mismatch = saved with { Feedback = feedback with { ReplyFailure = AiReplyFailure.IncompleteResponse } };
                    try { mismatch.Validate(); throw new Exception("Mismatched service classification accepted"); }
                    catch (InvalidDataException) { }
                }
                var next = new Session();
                var resumed = new LevelOneMaintenance(client, new AiTokenEstimator(root), next, result.TaskId, testOriginalFirst: true)
                    .RunAsync(request, catalogue, [], Limits, null, default).GetAwaiter().GetResult();
                Assert(resumed.Outcome == MaintenanceOutcome.Stopped && resumed.ReplyFailure == AiReplyFailure.OutputLimit
                    && resumed.ReportedTokens == 59168 && handler.Calls == 1 && next.Tests == 0 && next.Applies == 0);
            });
        check("A complete answer after 16K reasoning is validated once and a GPT task keeps the old allowance", () =>
        {
            foreach (var model in new[] { "deepseek-flash", "gpt-4.1" })
            {
                var request = Request(false) with { Settings = Settings with { Model = model } };
                var root = Path.Combine(Root, Guid.NewGuid().ToString("N"));
                using var handler = new Handler(false, success: true);
                using var http = new HttpClient(handler); using var client = new AiClient(http);
                var session = new Session();
                var result = new LevelOneMaintenance(client, new AiTokenEstimator(root), session, testOriginalFirst: true)
                    .RunAsync(request, Catalogue(), [], Limits, null, default).GetAwaiter().GetResult();
                if (model == "deepseek-flash")
                    Assert(result.Outcome == MaintenanceOutcome.Passed && session.Applies == 1 && session.Tests == 2
                        && handler.Calls == 1 && handler.Limit == 32768, result.Message);
                else Assert(handler.Limit == 16384 && result.Outcome == MaintenanceOutcome.Stopped && session.Applies == 0);
            }
        });
    }

    private static AiRequest Request(bool stream) => new(Settings with { StreamResponses = stream }, "synthetic", "1.7",
        ["ludeon.rimworld", "leaf.mod", "base.mod"], ["ludeon.rimworld", "leaf.mod", "base.mod"], [], [])
        { ResponseProgress = stream ? new Capture() : null };
    private static ModCatalogue Catalogue() => new([Mod("ludeon.rimworld"), Mod("leaf.mod"), Mod("base.mod")]);
    private static ModInstallation Mod(string id) => new(Path.Combine(Root, id), id,
        id == "ludeon.rimworld" ? ModSource.Game : ModSource.Local, "",
        new(id, "fixture", id, ["1.7"], "", new([], [], [], []), false), []);
    private sealed class Capture : IProgress<AiResponseUpdate>
    { public void Report(AiResponseUpdate update) { Assert(!update.ContentDelta.Contains("hidden-sentinel")); } }
    private sealed class Session : ILevelOneGameSession
    {
        internal int Tests, Applies;
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        {
            Tests++;
            return Task.FromResult(Tests > 1 ? new MaintenanceGameReport(true, []) : new(true, ["synthetic error"])
                { CanDiagnose = true, DiagnosticFacts = [new("GamePatchFailure", "leaf.mod")] });
        }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) { Applies++; return Task.CompletedTask; }
        public Task FinishAsync(bool accepted, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Handler(bool stream, bool success) : HttpMessageHandler
    {
        internal int Calls, Limit;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert(Calls == 1, "Unexpected paid retry");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Limit = payload.RootElement.TryGetProperty("max_tokens", out var max) ? max.GetInt32()
                : payload.RootElement.GetProperty("max_completion_tokens").GetInt32();
            var answer = success ? "{\"schemaVersion\":1,\"summary\":\"fixture\",\"actions\":[{\"kind\":\"reorder\",\"packageId\":\"base.mod\",\"targetIndex\":2}],\"warnings\":[],\"conflicts\":[],\"startupDiagnosis\":null}" : "";
            var output = success ? 17000 : 16384;
            var usage = new { prompt_tokens = 42784, completion_tokens = output, total_tokens = 42784 + output,
                completion_tokens_details = new { reasoning_tokens = 16384 } };
            var finish = success ? "stop" : "length";
            var body = stream
                ? "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { reasoning_content = "hidden-sentinel", content = answer }, finish_reason = finish } }, usage }) + "\n\ndata: [DONE]\n\n"
                : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer, reasoning_content = "hidden-sentinel" }, finish_reason = finish } }, usage });
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
    private static void Assert(bool value, string message = "DeepSeek output regression")
    { if (!value) throw new InvalidOperationException(message); }
}
