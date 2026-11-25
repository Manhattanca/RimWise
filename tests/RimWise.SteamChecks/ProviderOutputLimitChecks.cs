using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;

// No network, game or Steam. Large counters represent long hidden reasoning,
// not a large trusted action list or permission to run unvalidated actions.
internal static class ProviderOutputLimitChecks
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "provider-output", Guid.NewGuid().ToString("N"));
    private static readonly string[] Order = ["ludeon.rimworld", "leaf.mod", "base.mod"];
    private static readonly MaintenanceLimits Limits = new(5, OutputTokensPerRequest: 16384)
        { UseProviderOutputLimit = true, ExpandOutputBudget = true, TokenLimitEnabled = false };
    private static AiRequest Request(string host, string model, bool stream = false) => new(
        AiSettings.Default with { Endpoint = $"https://{host}/v1/chat/completions", Model = model, StreamResponses = stream },
        "synthetic-only", "1.7", Order, Order, [], []) { ResponseProgress = stream ? new Capture() : null };
    private static ModCatalogue Catalogue() => new(Order.Select(id => new ModInstallation(Path.Combine(Root, id), id,
        id == Order[0] ? ModSource.Game : ModSource.Local, "", new(id, "fixture", id, ["1.7"], "", new([], [], [], []), false), [])).ToArray());

    internal static void Run(Action<string, Action> check)
    {
        check("Unbudgeted requests omit both token cap names and SiliconFlow thinking budgets across providers", () =>
        {
            foreach (var (host, model) in new[] { ("api.deepseek.com", "deepseek-flash"), ("api.openai.com", "gpt-4.1"),
                ("relayrax.com", "deepseek-v4-pro"), ("api.siliconflow.cn", "deepseek-ai/DeepSeek-V4.1-Flash"),
                ("relay.invalid", "unknown-model") })
            foreach (var stream in new[] { false, true })
            {
                using var handler = new Handler(stream, "stop", output: 65000);
                var result = Run(Request(host, model, stream), handler, new Session(), Limits, NewRoot());
                using var body = JsonDocument.Parse(handler.Payload!);
                Assert(!body.RootElement.TryGetProperty("max_tokens", out _) && !body.RootElement.TryGetProperty("max_completion_tokens", out _)
                    && !body.RootElement.TryGetProperty("thinking_budget", out _), handler.Payload!);
                Assert(result.Outcome == MaintenanceOutcome.Passed && result.ReportedTokens == 107784
                    && !result.HasUnknownUsage && handler.Calls == 1, result.Message);
            }
        });
        check("Long reasoning final answers are applied only after validation, counted exactly and persisted without a fake cap", () =>
        {
            var root = NewRoot(); var session = new Session();
            using var handler = new Handler(true, "stop", output: 65000);
            var result = Run(Request("api.deepseek.com", "deepseek-flash", true), handler, session, Limits, root);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.ReportedTokens == 107784
                && session.Tests == 2 && session.Applies == 1 && handler.Calls == 1 && result.Requests == 1, result.Message);
            var saved = new AiTaskRecoveryStore(root).Inspect().Single().Checkpoint!;
            Assert(saved.LastRequestOutputLimit is null && saved.Limits!.UseProviderOutputLimit
                && saved.LastUsage is { Input: 42784, Output: 65000, ReasoningOutput: 64000 }
                && saved.UsageRequests.Single().Usage?.Total == 107784);
            saved.Validate();
        });
        foreach (var stream in new[] { false, true })
            check($"Provider length failure ({stream}) records usage, names the provider ceiling and recovers without replay", () =>
            {
                var root = NewRoot(); var request = Request("api.deepseek.com", "deepseek-flash", stream);
                using var handler = new Handler(stream, "length", output: 65000); var session = new Session();
                var result = Run(request, handler, session, Limits, root);
                Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == AiReplyFailure.OutputLimit
                    && session.Tests == 1 && session.Applies == 0 && handler.Calls == 1 && result.ReportedTokens == 107784
                    && !result.HasUnknownUsage && result.TrialExclusions.Count == 0
                    && result.Message.Contains("软件未设置单次输出上限") && !result.Message.Contains("未找到可排除"), result.Message);
                var saved = new AiTaskRecoveryStore(root).Inspect().Single().Checkpoint!;
                Assert(saved.OutputBudgetExhausted && saved.LastRequestOutputLimit is null && !saved.MayApply && saved.Actions.Length == 0);
                saved.Validate();
                var next = new Session(); var resumed = Run(request, handler, next, Limits, root, result.TaskId);
                Assert(resumed.Outcome == MaintenanceOutcome.Stopped && handler.Calls == 1 && next.Tests == 0
                    && next.Applies == 0 && resumed.ReportedTokens == result.ReportedTokens, resumed.Message);
                var wrongPolicy = saved with { Limits = Limits with { UseProviderOutputLimit = false } };
                try { wrongPolicy.Validate(); throw new Exception("Unknown old ceiling record accepted"); }
                catch (InvalidDataException) { }
            });
        check("Missing usage stays unknown with an estimate, never becomes free usage or an enforced estimate ceiling", () =>
        {
            var root = NewRoot(); using var handler = new Handler(false, "stop", output: 65000, omitUsage: true);
            var result = Run(Request("api.deepseek.com", "deepseek-flash"), handler, new Session(), Limits, root);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.HasUnknownUsage && result.ReportedTokens == 0
                && handler.Calls == 1, result.Message);
            var saved = new AiTaskRecoveryStore(root).Inspect().Single().Checkpoint!;
            Assert(saved.UnknownUsage && saved.ReservedTokens > 0 && saved.UsageRequests.Single() is { ResponseReceived: true, Usage: null });
            var estimate = new AiTokenEstimator(root).Estimate(Request("api.deepseek.com", "deepseek-flash"));
            Assert(estimate.OutputLimit is null && estimate.Description.Contains("不是实际用量上限"));
        });
        check("Explicit total budgets and old tasks keep bounded requests and reject a provider exceeding that cap", () =>
        {
            foreach (var policy in new[] { Limits with { TokenLimitEnabled = true, TokenBudget = 200000 },
                Limits with { UseProviderOutputLimit = false, ExpandOutputBudget = false } })
            {
                var root = NewRoot(); using var handler = new Handler(false, "stop", output: 65000); var session = new Session();
                var result = Run(Request("api.openai.com", "gpt-4.1"), handler, session, policy, root);
                using var payload = JsonDocument.Parse(handler.Payload!);
                Assert(payload.RootElement.GetProperty("max_completion_tokens").GetInt32() == 16384
                    && result.Outcome == MaintenanceOutcome.Stopped && result.ReportedTokens == 107784 && session.Applies == 0, result.Message);
            }
            var old = JsonSerializer.Deserialize<MaintenanceLimits>("{\"MaximumAttempts\":5,\"TokenLimitEnabled\":false,\"OutputTokensPerRequest\":16384}")!;
            Assert(!old.UseProviderOutputLimit && old.RequestOutputLimit == 16384);
        });
        check("An interrupted provider-default request retains uncertain billing and cannot resume with another paid request", () =>
        {
            var root = NewRoot(); using var handler = new Handler(false, "interrupt", output: 0); var request = Request("api.deepseek.com", "deepseek-flash");
            var result = Run(request, handler, new Session(), Limits, root);
            Assert(result.HasUnknownUsage && result.Outcome != MaintenanceOutcome.Passed && handler.Calls == 1);
            var next = new Session(); var resumed = Run(request, handler, next, Limits, root, result.TaskId);
            // The client returns a classified network failure receipt, not an unresolved
            // pending checkpoint. It must remain terminal without rebilling either way.
            Assert(resumed.Outcome == MaintenanceOutcome.Stopped && resumed.HasUnknownUsage
                && handler.Calls == 1 && next.Tests == 0 && next.Applies == 0, resumed.Message);
        });
    }
    private static string NewRoot() => Path.Combine(Root, Guid.NewGuid().ToString("N"));
    private static MaintenanceResult Run(AiRequest request, Handler handler, Session session, MaintenanceLimits limits, string root, string? resume = null)
    {
        using var http = new HttpClient(handler, disposeHandler: false); using var client = new AiClient(http);
        return new LevelOneMaintenance(client, new AiTokenEstimator(root), session, resume, testOriginalFirst: true)
            .RunAsync(request, Catalogue(), [], limits, null, default).GetAwaiter().GetResult();
    }
    private sealed class Capture : IProgress<AiResponseUpdate>
    { public void Report(AiResponseUpdate update) { Assert(!update.ContentDelta.Contains("hidden-fixture")); } }
    private sealed class Session : ILevelOneGameSession
    {
        internal int Tests, Applies;
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        {
            Tests++;
            return Task.FromResult(Tests == 1 ? new(true, new[] { "synthetic error" })
                { CanDiagnose = true, DiagnosticFacts = [new("GamePatchFailure", "leaf.mod")] } : new MaintenanceGameReport(true, []));
        }
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) { Assert(plan.CanApply); Applies++; return Task.CompletedTask; }
        public Task FinishAsync(bool accepted, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Handler(bool stream, string finish, int output, bool omitUsage = false) : HttpMessageHandler
    {
        internal int Calls; internal string? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert(Calls == 1, "Unexpected automatic retry"); Payload = await request.Content!.ReadAsStringAsync(token);
            if (finish == "interrupt") throw new OperationCanceledException("Synthetic lost receipt");
            var answer = finish == "stop" ? "{\"schemaVersion\":1,\"summary\":\"fixture\",\"actions\":[{\"kind\":\"reorder\",\"packageId\":\"base.mod\",\"targetIndex\":2}],\"warnings\":[],\"conflicts\":[],\"startupDiagnosis\":null}" : "";
            object? usage = omitUsage ? null : new { prompt_tokens = 42784, completion_tokens = output, total_tokens = 42784 + output,
                completion_tokens_details = new { reasoning_tokens = 64000 } };
            var body = stream ? "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0,
                delta = new { content = answer, reasoning_content = "hidden-fixture" }, finish_reason = finish } }, usage }) + "\n\ndata: [DONE]\n\n"
                : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer, reasoning_content = "hidden-fixture" }, finish_reason = finish } }, usage });
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
    private static void Assert(bool value, string message = "Provider output regression")
    { if (!value) throw new InvalidOperationException(message); }
}
