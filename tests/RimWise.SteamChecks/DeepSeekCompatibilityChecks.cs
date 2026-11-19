using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Core;
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
        check("Relay DeepSeek sorts with canonical quoted integers and unchanged wire parameters", () =>
        {
            const string answer = """{"schemaVersion":"1","actions":[{"kind":"reorder","packageId":"test.mod","targetIndex":"2"}]}""";
            var (reply, handler, _) = Call("relay.invalid", "deepseek-v4.1-flash", Envelope(answer), false);
            using var json = JsonDocument.Parse(handler.Payload!);
            var body = json.RootElement;
            Assert(reply.Succeeded && reply.Response?.Actions.Single().TargetIndex == 2 && reply.Usage?.Total == 30
                && handler.Calls == 1 && body.GetProperty("max_tokens").GetInt32() == 16384
                && !body.TryGetProperty("response_format", out _) && !body.TryGetProperty("reasoning_effort", out _));
            var messages = body.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()!).ToArray();
            Assert(messages[0].Contains("CurrentOrder") && !messages[0].Contains("reorder|add|remove")
                && messages[^1].Contains("Focus reasoning") && messages[^1].Contains("\"targetIndex\":2"));
            var (other, _, _) = Call("relay.invalid", "other-model", Envelope(answer), false);
            Assert(!other.Succeeded && other.Failure == AiReplyFailure.InvalidStructure);
        });
        check("DeepSeek compatibility refuses ambiguous numbers, unknown or duplicate fields, and truncated proposals", () =>
        {
            foreach (var action in new[] { "\"targetIndex\":\"2.0\"", "\"targetIndex\":\"02\"", "\"targetIndex\":\"+2\"",
                "\"targetIndex\":\"2147483648\"", "\"targetIndex\":\"2\",\"targetIndex\":3", "\"unexpected\":true" })
            {
                var (reply, handler, _) = Call("relay.invalid", "deepseek-v4.1-flash",
                    Envelope("{\"actions\":[{\"kind\":\"reorder\",\"packageId\":\"test.mod\"," + action + "}]}"), false);
                Assert(!reply.Succeeded && reply.Response is null && reply.Failure == AiReplyFailure.InvalidStructure
                    && reply.Usage?.Total == 30 && handler.Calls == 1);
            }
            var (truncated, calls, _) = Call("relay.invalid", "deepseek-v4.1-flash",
                Envelope("""{"actions":[{"kind":"reorder","packageId":"test.mod","targetIndex":"2"}]}""").Replace("\"stop\"", "\"length\""), false);
            Assert(!truncated.Succeeded && truncated.Response is null && truncated.Failure == AiReplyFailure.OutputLimit
                && truncated.Usage?.Total == 30 && calls.Calls == 1);
        });
        check("GPT keeps its request parameters and strict reply schema on OpenAI and compatible relays", () =>
        {
            const string answer = """{"schemaVersion":1,"actions":[{"kind":"reorder","packageId":"test.mod_steam","targetIndex":2}]}""";
            foreach (var host in new[] { "api.openai.com", "relay.invalid", "api.siliconflow.cn" })
            {
                var (reply, handler, _) = Call(host, "gpt-6.2-sol", Envelope(answer), false);
                using var json = JsonDocument.Parse(handler.Payload!);
                var body = json.RootElement;
                Assert(reply.Succeeded && reply.Response?.Actions.Single().TargetIndex == 2 && handler.Calls == 1
                    && body.GetProperty("max_completion_tokens").GetInt32() == 16384
                    && new[] { "max_tokens", "thinking_budget", "enable_thinking", "response_format", "reasoning_effort" }
                        .All(field => !body.TryGetProperty(field, out _)));
                Assert(!body.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("content").GetString()!.Contains("Focus reasoning")));
                var (invalid, invalidHandler, _) = Call(host, "gpt-6.2-sol",
                    Envelope(answer.Replace("\"targetIndex\":2", "\"targetIndex\":\"2\"")), false);
                Assert(!invalid.Succeeded && invalid.Failure == AiReplyFailure.InvalidStructure && invalidHandler.Calls == 1);
            }
        });
        check("Only DeepSeek model names get active Workshop alias normalization", () =>
        {
            var catalogue = new ModCatalogue([new("fixture", "fixture", ModSource.Workshop, "123",
                new("Fixture", "", "test.mod", ["1.7"], "", ModRequirements.Empty, false), [])]);
            var version = new GameVersion(1, 7);
            string[] order = ["test.mod_steam"];
            AiAction[] actions = [new("reorder", "test.mod", 1, false, null, null)];
            Assert(AiChatCompatibility.NormalizePlanActions("deepseek-ai/DeepSeek-V4.1-Flash", actions, order, catalogue, version)
                .Single().PackageId == "test.mod_steam");
            foreach (var model in new[] { "gpt-6.2-sol", "relay/gpt-6.2-sol", "other-model" })
                Assert(AiChatCompatibility.NormalizePlanActions(model, actions, order, catalogue, version).Single().PackageId == "test.mod");
        });
    }

    private static string Envelope(string answer = Answer) => JsonSerializer.Serialize(new { choices = new[] { new {
        message = new { content = answer, reasoning_content = "private-fixture-secret" }, finish_reason = "stop" } },
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
