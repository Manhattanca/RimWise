using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Integration.AI;

internal static class AiResponseFormatChecks
{
    private const string Answer = """{"schemaVersion":1,"summary":"中文结果","actions":[{"kind":"reorder","packageId":"test.mod","targetIndex":2,"requiredDependency":false,"reason":"Dependency order"}],"warnings":[],"conflicts":[]}""";
    private static readonly object Usage = new
    {
        prompt_tokens = 10, completion_tokens = 20, total_tokens = 30,
        completion_tokens_details = new { reasoning_tokens = 5 }
    };

    internal static void Run(Action<string, Action> check)
    {
        check("Long reasoning streams and repeated envelopes do not limit a small final answer", () =>
        {
            var reasoning = "data: " + JsonSerializer.Serialize(new { id = "test-response",
                created = 1793712000, model = "test-model", choices = new[] { new { index = 0,
                    delta = new { reasoning_content = "private-reasoning" }, finish_reason = (string?)null } } }) + "\n\n";
            var wire = string.Concat(Enumerable.Repeat(reasoning, 20000)) + Events([Answer]);
            Assert(Encoding.UTF8.GetByteCount(wire) > 2 * 1024 * 1024);
            var progress = new CaptureProgress();
            var (reply, handler) = Call(wire, "deepseek-test", progress, streaming: true);
            Assert(reply.Succeeded && reply.Response!.Actions.Count == 1 && reply.Usage?.Total == 30 && handler.Calls == 1);
            Assert(!progress.Text.Contains("private-reasoning") && !JsonSerializer.Serialize(reply).Contains("private-reasoning"));
        });
        check("Oversized lines and multi-line frames report size rather than a format error", () =>
        {
            var usage = "data: " + JsonSerializer.Serialize(new { choices = Array.Empty<object>(), usage = Usage }) + "\n\n";
            foreach (var wire in new[] { ":" + new string('x', 1024 * 1024),
                string.Concat(Enumerable.Repeat("data: " + new string('x', 901) + "\n", 1200)) })
            {
                var (reply, handler) = Call(usage + wire, "deepseek-test", new CaptureProgress(), streaming: true);
                Assert(!reply.Succeeded && reply.Response is null && reply.ServiceError?.Kind == "size"
                    && reply.Usage?.Total == 30 && handler.Calls == 1);
            }
        });
        check("Accumulated answer data stays bounded across individually small frames", () =>
        {
            var (reply, handler) = Call(Events(Enumerable.Repeat(new string('x', 16384), 65)),
                "deepseek-test", new CaptureProgress(), streaming: true);
            Assert(!reply.Succeeded && reply.Response is null && reply.ServiceError?.Kind == "size" && handler.Calls == 1);
        });
        check("GPT and DeepSeek accept the same final JSON schema without reasoning persistence", () =>
        {
            foreach (var model in new[] { "gpt-model", "deepseek-r1:7b", "deepseek-reasoner", "deepseek-ai/DeepSeek-R1" })
            {
                var progress = new CaptureProgress();
                var (reply, handler) = Call(Envelope(Answer), model, progress);
                Assert(reply.Succeeded && reply.Response!.Actions.Single().PackageId == "test.mod");
                Assert(reply.Usage is { Total: 30, ReasoningOutput: 5 } && handler.Calls == 1);
                Assert(!JsonSerializer.Serialize(reply).Contains("private-reasoning")
                    && !progress.Text.Contains("private-reasoning"));
                using var payload = JsonDocument.Parse(handler.RequestBody!);
                var parameter = model.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ? "max_tokens" : "max_completion_tokens";
                Assert(payload.RootElement.GetProperty(parameter).GetInt32() == 4096
                    && !payload.RootElement.TryGetProperty(parameter == "max_tokens" ? "max_completion_tokens" : "max_tokens", out _)
                    && !payload.RootElement.TryGetProperty("response_format", out _));
                var messages = payload.RootElement.GetProperty("messages");
                Assert(messages.GetArrayLength() == 3 && messages[2].GetProperty("content").GetString()!.Contains("final JSON object"));
                var expected = AiPrompt.Messages(Request(model, progress));
                Assert(messages.EnumerateArray().Select(m => m.GetProperty("content").GetString())
                    .SequenceEqual(expected.Select(m => m.Content)));
            }
        });
        check("Complete think prefixes and JSON fences normalize without changing actions or string values", () =>
        {
            foreach (var text in new[] { "\uFEFF " + Answer, "```json\n" + Answer + "\n```",
                "```JSON\r\n" + Answer + "\r\n```", "<think>private-reasoning " + Answer + "</think>\n```json\n" + Answer + "\n```",
                "<think>private-reasoning</think>\n<think>private-reasoning</think>\n" + Answer,
                Answer.Replace("中文结果", "literal <think> inside a JSON string") })
            {
                var progress = new CaptureProgress();
                var (reply, _) = Call(Envelope(text), "deepseek-r1:7b", progress);
                Assert(reply.Succeeded && reply.Response!.Actions.Single().TargetIndex == 2);
                Assert(!JsonSerializer.Serialize(reply).Contains("private-reasoning") && !progress.Text.Contains("private-reasoning"));
            }
        });
        check("Streaming split think markers hide reasoning while preserving final answer usage and request identity", () =>
        {
            var parts = new[] { "\uFEFF ", "<", "thi", "nk>", "private-reasoning", "</t", "hink", ">\n", "```j", "son\n", Answer[..20], Answer[20..], "\n```" };
            var progress = new CaptureProgress();
            var (reply, handler) = Call(Events(parts), "deepseek-r1:7b", progress, streaming: true);
            Assert(reply.Succeeded && handler.Calls == 1 && reply.Response!.Summary == "中文结果");
            Assert(reply.Usage is { Total: 30, ReasoningOutput: 5 } && reply.ProviderIds.Contains(AiUsageRequest.Hash("test-response")));
            Assert(!progress.Text.Contains("private-reasoning") && !progress.Text.Contains("<think>")
                && !JsonSerializer.Serialize(reply).Contains("private-reasoning"));
            Assert(progress.Text.Contains("中文结果") && progress.Updates.Any(update => update.Usage?.Total == 30));
        });
        check("Thinking tag filters stay correct at every character boundary", () =>
        {
            var text = "<think>private-reasoning</think>\n<think>private-reasoning</think>\n" + Answer;
            for (var boundary = 1; boundary < 70; boundary++)
            {
                var progress = new CaptureProgress();
                var (reply, _) = Call(Events([text[..boundary], text[boundary..]]), "deepseek-r1:7b", progress, streaming: true);
                Assert(reply.Succeeded && !progress.Text.Contains("private-reasoning") && !progress.Text.Contains("<think>"));
            }
            var characterProgress = new CaptureProgress();
            var (characterReply, _) = Call(Events(text.Select(character => character.ToString())), "deepseek-r1:7b", characterProgress, streaming: true);
            Assert(characterReply.Succeeded && !characterProgress.Text.Contains("private-reasoning"));
        });
        check("Reasoning alone never becomes an executable answer", () =>
        {
            foreach (var text in new string?[] { null, "", "<think>" + Answer, "<think>" + Answer + "</think>" })
            {
                var (reply, _) = Call(Envelope(text, reasoning: Answer), "deepseek-r1:7b");
                Assert(!reply.Succeeded && reply.Response is null && reply.Usage?.Total == 30);
            }
        });
        check("Normalization never repairs malformed JSON or guesses actions from prose", () =>
        {
            foreach (var text in new[] { "Here is the answer: " + Answer, Answer + Answer,
                "```json\n" + Answer, "```json\n" + Answer + "\n```\nextra", "```python\n" + Answer + "\n```",
                Answer.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
                Answer.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"shell\":\"anything\""),
                Answer.Replace("\"schemaVersion\":1", "'schemaVersion':1") })
            {
                var (reply, handler) = Call(Envelope(text), "deepseek-r1:7b");
                Assert(!reply.Succeeded && reply.Response is null && handler.Calls == 1 && reply.Usage?.Total == 30);
            }
        });
        check("Truncated ordinary and incomplete streamed answers keep usage but authorize no actions", () =>
        {
            var (ordinary, _) = Call(Envelope(Answer, finish: "length"), "deepseek-r1:7b");
            Assert(!ordinary.Succeeded && ordinary.Failure == AiReplyFailure.OutputLimit && ordinary.Usage?.Total == 30);
            var (truncated, transport) = Call(Events([Answer], finish: "length"), "deepseek-r1:7b", new CaptureProgress(), streaming: true);
            Assert(truncated.Failure == AiReplyFailure.OutputLimit && truncated.Response is null
                && truncated.Usage?.Total == 30 && transport.Calls == 1);
            foreach (var events in new[] { Events([Answer], finish: "length"), Events([Answer]).Replace("\"stop\"", "null"),
                Events(["<think>" + Answer]) })
            {
                var (reply, _) = Call(events, "deepseek-r1:7b", new CaptureProgress(), streaming: true);
                Assert(!reply.Succeeded && reply.Response is null && reply.Usage?.Total == 30);
            }
        });
        check("Malformed streaming usage does not discard a complete answer or stop delivery", () =>
        {
            var missingCounters = Events([Answer]).Replace("data: [DONE]", "data: {\"usage\":{\"prompt_tokens\":null},\"choices\":[]}\n\ndata: [DONE]");
            var progress = new CaptureProgress();
            var (reply, handler) = Call(missingCounters, "deepseek-flash", progress, streaming: true);
            Assert(reply.Succeeded && reply.Usage is null && progress.Text.Contains("中文结果") && handler.Calls == 1);
        });
        check("Conflicting streamed counters become unknown usage while complete actions still validate", () =>
        {
            var conflict = Events([Answer]).Replace("data: [DONE]", "data: {\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":20,\"total_tokens\":31},\"choices\":[]}\n\ndata: [DONE]");
            var (reply, handler) = Call(conflict, "deepseek-flash", new CaptureProgress(), streaming: true);
            Assert(reply.Succeeded && reply.Usage is null && reply.Response!.Actions.Count == 1 && handler.Calls == 1);
        });
        check("HTTP 400 remains a service rejection and is never parsed as a model answer or automatically retried", () =>
        {
            var (reply, handler) = Call(Envelope(Answer), "deepseek-r1:7b", status: HttpStatusCode.BadRequest);
            Assert(!reply.Succeeded && reply.ServiceError is { Kind: "http", HttpStatus: 400 }
                && reply.Response is null && handler.Calls == 1 && reply.RawResponse is null);
        });
        check("Compatible provider token parameters and optional settings are selected before the only request", () =>
        {
            foreach (var model in new[] { "qwen3", "glm-4.6", "kimi-k2", "gemini-2.5-pro", "grok-4", "claude-sonnet-4",
                "llama-3.3-70b-versatile", "ep-test", "deepseek-ai/DeepSeek-R1", "gpt-5", "openai/o3" })
            {
                var (reply, handler) = Call(Envelope(Answer), model);
                using var json = JsonDocument.Parse(handler.RequestBody!);
                var root = json.RootElement;
                var parameter = model is "gpt-5" or "openai/o3" ? "max_completion_tokens" : "max_tokens";
                Assert(reply.Succeeded && root.GetProperty(parameter).GetInt32() == 4096
                    && !root.TryGetProperty("temperature", out _) && handler.Calls == 1);
            }
            var official = new ReplyHandler(Envelope(Answer), false, HttpStatusCode.OK);
            using var http = new HttpClient(official);
            using var client = new AiClient(http);
            var request = Request("deepseek-flash", null);
            var result = client.GetSuggestionAsync(request with { Settings = request.Settings with
                { Endpoint = "https://api.deepseek.com/chat/completions" } }, CancellationToken.None).GetAwaiter().GetResult();
            using var payload = JsonDocument.Parse(official.RequestBody!);
            Assert(result.Succeeded && payload.RootElement.GetProperty("reasoning_effort").GetString() == "low"
                && payload.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_object"
                && official.Calls == 1);
        });
        check("Explicit completed streams support clean EOF, trailing events, BOM and SSE newline variants", () =>
        {
            foreach (var wire in new[] { Events([Answer], done: false), Events([Answer]).TrimEnd(),
                "\uFEFF" + Events([Answer]).Replace("\n", "\r\n"), Events([Answer]).Replace("\n", "\r"),
                Events([Answer], done: false).TrimEnd(), Events([Answer]).Replace("\"index\":0,", ""),
                Events([Answer]).Replace("\"choices\":[],", "") })
            {
                var (reply, handler) = Call(wire, "deepseek-flash", new CaptureProgress(), streaming: true);
                Assert(reply.Succeeded && reply.Usage?.Total == 30 && handler.Calls == 1);
            }
            var (mislabeled, _) = Call(Events([Answer]), "qwen3", new CaptureProgress());
            Assert(mislabeled.Succeeded && mislabeled.Usage?.Total == 30);
        });
        check("DeepSeek final usage on terminal choice and OpenRouter repeated stop validate", () =>
        {
            var terminal = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0,
                delta = new { content = "" }, finish_reason = "stop" } }, usage = Usage }) + "\n\n";
            var wire = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = Answer } } } }) + "\n\n";
            foreach (var ending in new[] { terminal, terminal + terminal })
            {
                var (reply, _) = Call(wire + ending + "data: [DONE]\n\n", "deepseek-flash", new CaptureProgress(), true);
                Assert(reply.Succeeded && reply.Usage?.Total == 30);
            }
        });
        check("Text content arrays normalize while reasoning parts never become actions", () =>
        {
            var parts = new object[] { new { type = "thinking", thinking = "private-reasoning" },
                new { type = "text", text = Answer[..30] }, new { type = "text", text = Answer[30..] } };
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = parts }, finish_reason = "stop" } } });
            var (reply, _) = Call(body, "claude-sonnet-4");
            Assert(reply.Succeeded && !JsonSerializer.Serialize(reply).Contains("private-reasoning"));
            var events = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = parts }, finish_reason = "stop" } } }) + "\n\ndata: [DONE]\n\n";
            var (streamed, _) = Call(events, "claude-sonnet-4", new CaptureProgress(), true);
            Assert(streamed.Succeeded && !JsonSerializer.Serialize(streamed).Contains("private-reasoning"));
        });
        check("Cumulative usage snapshots are not summed and provider cache counts are retained", () =>
        {
            var early = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3,\"total_tokens\":13}}\n\n";
            var (reply, _) = Call(early + Events([Answer]), "glm-4.6", new CaptureProgress(), true);
            Assert(reply.Succeeded && reply.Usage is { Input: 10, Output: 20, Total: 30 });
            var counts = AiTokenUsage.Read("""{"usage":{"prompt_tokens":10,"completion_tokens":20,"prompt_cache_hit_tokens":7}}""");
            Assert(counts is { Total: 30, CachedInput: 7 });
            Assert(AiTokenUsage.Read("""{"usage":{"prompt_tokens":10,"completion_tokens":20,"total_tokens":null}}""") is null);
        });
        check("Errors, tool calls, refusals and unclosed final frames never authorize apparently valid actions", () =>
        {
            foreach (var wire in new[] { Events([Answer]).Replace("\"stop\"", "\"content_filter\""),
                Events([Answer]).Replace("\"stop\"", "\"insufficient_system_resource\""),
                Events([Answer]).Replace("data: [DONE]", "data: {\"error\":{\"message\":\"synthetic error\"}}\n\ndata: [DONE]"),
                Events([Answer], done: false) + "data: {", Events([Answer]).Replace("\"stop\"", "null"),
                Events([Answer], done: false) + "data: {\"choices\":[{\"delta\":{\"content\":\" \"}}]}\n\n",
                Events([Answer]).Replace("\"content\":", "\"tool_calls\":[{}],\"content\":") })
            {
                var (reply, handler) = Call(wire, "deepseek-flash", new CaptureProgress(), true);
                Assert(!reply.Succeeded && reply.Response is null && handler.Calls == 1);
            }
            foreach (var extra in new[] { "\"refusal\":\"declined\",", "\"tool_calls\":[{}]," })
            {
                var (reply, _) = Call(Envelope(Answer).Replace("\"content\":", extra + "\"content\":"), "gpt-5");
                Assert(!reply.Succeeded && reply.Response is null);
            }
        });
    }

    private static string Envelope(string? text, string finish = "stop", string reasoning = "private-reasoning") =>
        JsonSerializer.Serialize(new { id = "test-response", choices = new[] { new { index = 0,
            message = new { role = "assistant", content = text, reasoning_content = reasoning }, finish_reason = finish } }, usage = Usage });

    private static string Events(IEnumerable<string> parts, string finish = "stop", bool done = true)
    {
        static string Frame(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
        var events = new StringBuilder(Frame(new { id = "test-response", choices = new[] { new { index = 0,
            delta = new { reasoning_content = "private-reasoning" } } } }));
        foreach (var part in parts) events.Append(Frame(new { choices = new[] { new { index = 0, delta = new { content = part } } } }));
        events.Append(Frame(new { choices = new[] { new { index = 0, delta = new { }, finish_reason = finish } } }));
        events.Append(Frame(new { choices = Array.Empty<object>(), usage = Usage }));
        if (done) events.Append("data: [DONE]\n\n");
        return events.ToString();
    }

    private static AiRequest Request(string model, CaptureProgress? progress) => new(
        AiSettings.Default with { Enabled = true, Endpoint = "http://127.0.0.1:11434/v1/", Model = model },
        null, "1.7", ["ludeon.rimworld", "test.mod"], ["ludeon.rimworld", "test.mod"], [], [])
        { OutputTokenLimit = 4096, ResponseProgress = progress };

    private static (AiClientResult Reply, ReplyHandler Handler) Call(string body, string model,
        CaptureProgress? progress = null, bool streaming = false, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new ReplyHandler(body, streaming, status);
        using var http = new HttpClient(handler);
        using var client = new AiClient(http);
        var reply = client.GetSuggestionAsync(Request(model, progress), CancellationToken.None).GetAwaiter().GetResult();
        return (reply, handler);
    }

    private static void Assert(bool condition)
    { if (!condition) throw new Exception("AI final-answer compatibility regression"); }

    private sealed class CaptureProgress : IProgress<AiResponseUpdate>
    {
        public readonly List<AiResponseUpdate> Updates = [];
        public string Text => string.Concat(Updates.Select(update => update.ContentDelta));
        public void Report(AiResponseUpdate value) => Updates.Add(value);
    }

    private sealed class ReplyHandler(string body, bool streaming, HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;
        public string? RequestBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(status) { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
