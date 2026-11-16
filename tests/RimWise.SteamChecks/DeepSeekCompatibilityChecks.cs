using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Integration.AI;

internal static class DeepSeekCompatibilityChecks
{
    private const string Answer = """{"schemaVersion":1,"summary":"合成诊断","actions":[],"warnings":[],"conflicts":[]}""";
    internal static void Run(Action<string, Action> check)
    {
        check("SiliconFlow DeepSeek aliases share bounded reasoning and JSON configuration", () =>
        {
            foreach (var host in new[] { "api.siliconflow.cn", "api.siliconflow.com" })
                foreach (var model in new[] { "deepseek-ai/DeepSeek-V4.1-Flash", "Pro/deepseek-ai/DeepSeek-V4", "DeepSeek-V4-Flash", "deepseek-ai/DeepSeek-V3.2" })
                {
                    var (reply, handler, _) = Call(host, model, Envelope(), false);
                    using var json = JsonDocument.Parse(handler.Payload!); var body = json.RootElement;
                    Assert(reply.Succeeded && handler.Calls == 1 && body.GetProperty("model").GetString() == model);
                    Assert(body.GetProperty("max_tokens").GetInt32() + body.GetProperty("thinking_budget").GetInt32() == 16384
                        && body.GetProperty("enable_thinking").GetBoolean()
                        && body.GetProperty("response_format").GetProperty("type").GetString() == "json_object"
                        && !body.TryGetProperty("reasoning_effort", out _) && !body.TryGetProperty("max_completion_tokens", out _));
                }
        });
        check("Official DeepSeek and unrecognized relays never receive SiliconFlow controls", () =>
        {
            foreach (var host in new[] { "api.deepseek.com", "relay.invalid", "api.siliconflow.cn.relay.invalid" })
            {
                var (reply, handler, _) = Call(host, "deepseek-v4", Envelope(), false);
                using var json = JsonDocument.Parse(handler.Payload!); var body = json.RootElement;
                Assert(reply.Succeeded && body.GetProperty("max_tokens").GetInt32() == 16384
                    && !body.TryGetProperty("thinking_budget", out _) && !body.TryGetProperty("enable_thinking", out _)
                    && body.TryGetProperty("reasoning_effort", out _) == (host == "api.deepseek.com"));
            }
            var (_, r1, _) = Call("api.siliconflow.cn", "deepseek-ai/DeepSeek-R1", Envelope(), false);
            using var r1Json = JsonDocument.Parse(r1.Payload!);
            Assert(!r1Json.RootElement.TryGetProperty("response_format", out _) && !r1Json.RootElement.TryGetProperty("enable_thinking", out _));
        });
        check("DeepSeek small output allowances do not send invalid thinking budgets", () =>
        {
            foreach (var limit in new[] { 1, 127, 255, 256, 4096, 32768 })
            {
                var (_, handler, _) = Call("api.siliconflow.cn", "deepseek-ai/DeepSeek-V4.1-Flash", Envelope(), false, limit: limit);
                using var json = JsonDocument.Parse(handler.Payload!); var body = json.RootElement;
                var thinking = body.TryGetProperty("thinking_budget", out var budget) ? budget.GetInt32() : 0;
                Assert(body.GetProperty("max_tokens").GetInt32() > 0
                    && body.GetProperty("max_tokens").GetInt32() + thinking == limit
                    && (thinking == 0 || thinking is >= 128 and <= 8192));
            }
        });
        check("SiliconFlow authentication failure is Chinese, provider-specific and never retried", () =>
        {
            var (reply, handler, updates) = Call("api.siliconflow.cn", "deepseek-ai/DeepSeek-V4.1-Flash",
                "{\"error\":{\"message\":\"private-fixture-secret\"}}", false, HttpStatusCode.Unauthorized);
            Assert(!reply.Succeeded && reply.Failure == AiReplyFailure.ServiceFailure && handler.Calls == 1
                && reply.ServiceError is { HttpStatus: 401, Provider: "siliconflow", RequiresConfigurationChange: true }
                && reply.Message.Contains("硅基流动") && !reply.Message.Contains("private-fixture-secret")
                && reply.Response is null && reply.Usage is null && updates.Text.Length == 0);
        });
        check("DeepSeek reasoning stays private across streamed and ordinary final answers", () =>
        {
            var frame = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_content = Answer } } } }) + "\n\n";
            var delta = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Answer }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 } }) + "\n\n";
            foreach (var wire in new[] { frame + delta + "data: [DONE]\n\n", frame + delta.TrimEnd(), Envelope() })
            {
                var (reply, handler, updates) = Call("api.siliconflow.cn", "deepseek-ai/DeepSeek-V4.1-Flash", wire, wire.StartsWith("data:"));
                Assert(reply.Succeeded && reply.Usage?.Total == 30 && handler.Calls == 1 && updates.Text == Answer
                    && !JsonSerializer.Serialize(reply).Contains("private-fixture-secret"));
            }
        });
        check("DeepSeek missing finish, truncation and reasoning-only responses do not authorize actions", () =>
        {
            foreach (var body in new[] {
                "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Answer } } } }) + "\n\ndata: [DONE]\n\n",
                Envelope().Replace("\"stop\"", "\"length\""),
                Envelope().Replace("\"content\":", "\"content\":null,\"unused\":") })
            {
                var (reply, handler, _) = Call("api.siliconflow.cn", "deepseek-ai/DeepSeek-V4.1-Flash", body, body.StartsWith("data:"));
                Assert(!reply.Succeeded && reply.Response is null && handler.Calls == 1);
            }
        });
    }

    private static string Envelope() => JsonSerializer.Serialize(new { choices = new[] { new {
        message = new { content = Answer, reasoning_content = "private-fixture-secret" }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 } });
    private static (AiClientResult, Handler, Capture) Call(string host, string model, string body, bool stream,
        HttpStatusCode status = HttpStatusCode.OK, int limit = 16384)
    {
        var handler = new Handler(body, stream, status); var capture = new Capture();
        using var http = new HttpClient(handler); using var client = new AiClient(http);
        var result = client.GetSuggestionAsync(new(AiSettings.Default with { Endpoint = "https://" + host + "/v1/chat/completions", Model = model },
            "synthetic-key", "1.7", [], [], [], []) { OutputTokenLimit = limit, ResponseProgress = capture }, default).GetAwaiter().GetResult();
        return (result, handler, capture);
    }
    private sealed class Capture : IProgress<AiResponseUpdate>
    {
        private readonly StringBuilder text = new(); public string Text => text.ToString();
        public void Report(AiResponseUpdate update) => text.Append(update.ContentDelta);
    }
    private sealed class Handler(string body, bool stream, HttpStatusCode status) : HttpMessageHandler
    {
        internal int Calls; internal string? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Payload = await request.Content!.ReadAsStringAsync(token);
            return new(status) { Content = new StringContent(body, Encoding.UTF8, stream ? "text/event-stream" : "application/json") }; }
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("DeepSeek compatibility regression"); }
}
