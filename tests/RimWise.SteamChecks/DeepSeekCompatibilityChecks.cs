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
        check("The validated RelayRax V4 profile is exact-host and exact-model scoped, never a global DeepSeek or GPT override", () =>
        {
            foreach (var host in new[] { "relayrax.com", "relayrax.com.relay.invalid", "relay.invalid" })
            foreach (var model in new[] { "deepseek-v4-pro", "deepseek-v4.1-flash", "deepseek-r1", "gpt-6.2-sol" })
            {
                var (_, handler, _) = Call(host, model, Envelope(), false);
                using var json = JsonDocument.Parse(handler.Payload!); var body = json.RootElement;
                var explicitMode = host == "relayrax.com" && model is "deepseek-v4-pro" or "deepseek-v4.1-flash";
                Assert(body.TryGetProperty("thinking", out var thinking) == explicitMode
                    && body.TryGetProperty("reasoning_effort", out _) == explicitMode
                    && body.TryGetProperty("response_format", out _) == explicitMode);
                if (explicitMode) Assert(thinking.GetProperty("type").GetString() == "enabled"
                    && body.GetProperty("reasoning_effort").GetString() == "high"
                    && body.GetProperty("response_format").GetProperty("type").GetString() == "json_object");
                Assert(!body.TryGetProperty("enable_thinking", out _) && !body.TryGetProperty("thinking_budget", out _));
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
        check("DeepSeek alone accepts repeated identical display suggestions without relaxing any control field", () =>
        {
            const string answer = """{"schemaVersion":1,"summary":"合成未知归属","actions":[],"warnings":[],"conflicts":[],"startupDiagnosis":{"category":"unknown","reason":"无归属","suggestions":["保留日志"],"suggestions":["保留日志"]}}""";
            var (accepted, handler, _) = Call("relayrax.com", "deepseek-v4-pro", Envelope(answer), false);
            Assert(accepted.Succeeded && accepted.Response is { Actions.Count: 0, Conflicts.Count: 0,
                StartupDiagnosis.Category: "unknown", StartupDiagnosis.Suggestions.Count: 1 } && handler.Calls == 1);
            foreach (var (model, malformed) in new[] {
                ("gpt-6.2-sol", answer),
                ("deepseek-v4-pro", answer.Replace("\"suggestions\":[\"保留日志\"],\"suggestions\":[\"保留日志\"]", "\"suggestions\":[\"保留日志\"],\"suggestions\":[\"另一条\"]")),
                ("deepseek-v4-pro", answer.Replace("\"actions\":[]", "\"actions\":[],\"actions\":[]")),
                ("deepseek-v4-pro", answer.Replace("\"category\":\"unknown\"", "\"category\":\"unknown\",\"category\":\"unknown\"")),
                ("deepseek-v4-pro", answer.Replace("\"reason\":\"无归属\"", "\"reason\":\"无归属\",\"startupDiagnosis\":null")) })
            {
                var (reply, calls, _) = Call("relayrax.com", model, Envelope(malformed), false);
                Assert(!reply.Succeeded && reply.Failure == AiReplyFailure.InvalidStructure && calls.Calls == 1);
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
        check("DeepSeek receives a compact verified-order focus without changing GPT prompts", () =>
        {
            var requirements = new[]
            {
                new AiModRequirementFact("owner.mod", ["dependency.mod"], [], [], []),
                new AiModRequirementFact("dependency.mod", [], [], [], [])
            };
            var deepSeek = Request("deepseek-flash") with
            {
                CurrentOrder = ["owner.mod", "dependency.mod"],
                InstalledMods = ["owner.mod", "dependency.mod"],
                ModRequirements = requirements,
                DiagnosticFacts = [new AiDiagnosticFact("GameLogError", "owner.mod")]
            };
            var deepMessages = AiPrompt.Messages(deepSeek);
            using var focus = Focus(deepSeek);
            var edge = focus.RootElement.GetProperty("OrderObservations").EnumerateArray().Single();
            Assert(deepMessages.Count == 4
                && edge.GetProperty("Kind").GetString() == "dependency"
                && edge.GetProperty("Before").GetString() == "dependency.mod"
                && edge.GetProperty("After").GetString() == "owner.mod"
                && edge.GetProperty("BeforeIndex").GetInt32() == 2 && edge.GetProperty("AfterIndex").GetInt32() == 1
                && edge.GetProperty("State").GetString() == "wrong-order"
                && edge.GetProperty("Required").GetBoolean() && edge.GetProperty("DiagnosticRelated").GetBoolean()
                && deepMessages[2].Content.Contains("必须生成精确 reorder/add action", StringComparison.Ordinal));
            var gptMessages = AiPrompt.Messages(deepSeek with { Settings = deepSeek.Settings with { Model = "gpt-test" } });
            Assert(gptMessages.Count == 3 && gptMessages.All(message => !message.Content.Contains("DeepSeek 专项证据摘要", StringComparison.Ordinal)));
            Assert(gptMessages[0] == deepMessages[0] && gptMessages[1] == deepMessages[1]);
        });
        check("DeepSeek distinguishes installed inactive dependencies from unavailable ones without changing the order", () =>
        {
            var order = new[] { "owner.mod" };
            var request = Request("deepseek-flash") with { CurrentOrder = order, InstalledMods = ["owner.mod", "inactive.mod"],
                ModRequirements = [new("owner.mod", ["inactive.mod", "missing.mod"], [], [], [])] };
            using var focus = Focus(request);
            var edges = focus.RootElement.GetProperty("OrderObservations").EnumerateArray().ToArray();
            Assert(edges.Length == 2 && edges[0].GetProperty("State").GetString() == "installed-inactive"
                && edges[1].GetProperty("State").GetString() == "not-installed"
                && edges.All(e => e.GetProperty("BeforeIndex").GetInt32() == 0)
                && request.CurrentOrder.SequenceEqual(order));
        });
        check("DeepSeek does not promote deferred advisory cycles or original-order preferences to required repairs", () =>
        {
            using var focus = Focus(Request("deepseek-flash") with { CurrentOrder = ["a.mod", "b.mod"],
                ModRequirements = [new("a.mod", ["b.mod"], [], ["b.mod"], [])],
                DiagnosticFacts = [new("OrderingCycleDeferred", "a.mod"), new("OrderingCycleDeferred", "b.mod")],
                OrderRules = [new("b.mod", "a.mod") { OriginalOrderIndex = 1 }, new("b.mod", "a.mod") { Required = true }] });
            var edges = focus.RootElement.GetProperty("OrderObservations").EnumerateArray().ToArray();
            Assert(edges.Length == 3 && edges.Count(e => e.GetProperty("Required").GetBoolean()) == 2
                && edges.Single(e => !e.GetProperty("Required").GetBoolean()).GetProperty("DeferredCycleReviewRequired").GetBoolean()
                && edges.Where(e => e.GetProperty("Required").GetBoolean()).All(e => !e.GetProperty("DeferredCycleReviewRequired").GetBoolean()));
        });
        check("DeepSeek sees satisfied adjacent dependency links and exact active alias spelling", () =>
        {
            using var focus = Focus(Request("deepseek-flash") with { CurrentOrder = ["BASE.Mod_steam", "owner.mod"],
                InstalledMods = ["base.mod_steam", "owner.mod"], DiagnosticFacts = [new("GameLogError", "owner.mod")],
                ModRequirements = [new("owner.mod", ["base.mod_steam"], [], [], [])] });
            Assert(focus.RootElement.GetProperty("OrderObservations").GetArrayLength() == 0);
            var relation = focus.RootElement.GetProperty("RelatedRelationships").EnumerateArray().Single();
            Assert(relation.GetProperty("Before").GetString() == "BASE.Mod_steam"
                && relation.GetProperty("State").GetString() == "satisfied");
        });
        check("DeepSeek focus stays bounded while prioritizing late error-related dependencies and reporting omissions", () =>
        {
            var owners = Enumerable.Range(0, 100).Select(i => "owner." + i).ToArray();
            var dependencies = Enumerable.Range(0, 100).Select(i => "dependency." + i).ToArray();
            using var focus = Focus(Request("deepseek-flash") with { CurrentOrder = [.. owners, .. dependencies],
                DiagnosticFacts = [new("GameLogError", owners[^1])], ModRequirements = owners.Select((o, i) =>
                    new AiModRequirementFact(o, [dependencies[i]], [], [], [])).ToArray() });
            var root = focus.RootElement;
            Assert(root.GetProperty("OrderObservations").GetArrayLength() == 64
                && root.GetProperty("OrderObservationsOmitted").GetInt32() == 36
                && root.GetProperty("OrderObservations")[0].GetProperty("After").GetString() == owners[^1]);
        });
        check("DeepSeek hard-constraint coverage is separate from the smaller diagnostic focus and never truncated silently", () =>
        {
            var owners = Enumerable.Range(0, 150).Select(i => "consumer." + i).ToArray();
            var dependencies = Enumerable.Range(0, 150).Select(i => "provider." + i).ToArray();
            using var focus = Focus(Request("deepseek-flash") with { CurrentOrder = [.. owners, .. dependencies],
                DiagnosticFacts = [new("GameLogError", owners[^1])], ModRequirements = owners.Select((o, i) =>
                    new AiModRequirementFact(o, [dependencies[i]], [], [], [])).ToArray() });
            var root = focus.RootElement;
            Assert(root.GetProperty("RequiredOrderViolationCount").GetInt32() == 150
                && root.GetProperty("RequiredOrderViolations").GetArrayLength() == 128
                && root.GetProperty("RequiredOrderViolationsOmitted").GetInt32() == 22
                && root.GetProperty("OrderObservations").GetArrayLength() == 64
                && root.GetProperty("OrderObservationsOmitted").GetInt32() == 86);
            var first = root.GetProperty("RequiredOrderViolations")[0];
            Assert(first.GetProperty("Before").GetString() == dependencies[0]
                && first.GetProperty("After").GetString() == owners[0]
                && first.GetProperty("BeforeIndex").GetInt32() == 151 && first.GetProperty("AfterIndex").GetInt32() == 1);
        });
        check("DeepSeek focus uses validated package owners rather than matching startup symbols and quotes untrusted IDs", () =>
        {
            const string id = "author.\"quoted\" mod";
            var request = Request("deepseek-flash") with { CurrentOrder = ["symbol.mod", id],
                StartupContext = new("ProbeCompleted", true, true, [new("MissingType", "symbol.mod", "startup")]),
                LogContext = new([], [], false) { BlockingError = "synthetic blocking exception",
                    CodeStatus = "available", Code = [new("code-0", id, "Fixture.Method", new string('A', 64), "return 0;", false)] } };
            using var focus = Focus(request);
            Assert(focus.RootElement.GetProperty("HasBlockingError").GetBoolean()
                && focus.RootElement.GetProperty("Stage").GetString() == "ProbeCompleted"
                && focus.RootElement.GetProperty("AssociatedPackageIds").EnumerateArray().Select(e => e.GetString()).SequenceEqual([id]));
            var text = AiPrompt.Messages(request)[2].Content;
            Assert(!text.Contains("synthetic blocking exception") && !text.Contains("return 0;"));
        });
        check("DeepSeek evidence focus never reads raw legacy diagnostics, secrets, or log-only requests", () =>
        {
            var request = Request("deepseek-flash") with { ApiKey = "private-credential-sentinel", Diagnostics = ["private-legacy-sentinel"] };
            Assert(AiPrompt.Messages(request).All(m => !m.Content.Contains("private-credential-sentinel") && !m.Content.Contains("private-legacy-sentinel")));
            Assert(AiPrompt.Messages(request with { LogDiagnosisOnly = true,
                LogContext = new([new("game-errors", "synthetic error")], [], false) })
                .All(m => !m.Content.Contains("DeepSeek 专项证据摘要")));
        });
        check("DeepSeek sees the full inactive prerequisite chain without inventing an active order violation", () =>
        {
            using var focus = Focus(Request("deepseek-flash") with { CurrentOrder = ["leaf.mod", "base.mod"],
                InstalledMods = ["leaf.mod", "middle.mod", "base.mod", "bottom.mod"],
                ModRequirements = [new("leaf.mod", ["middle.mod"], [], [], []),
                    new("middle.mod", ["base.mod", "bottom.mod"], ["leaf.mod"], [], []), new("bottom.mod", ["missing.mod"], [], [], [])] });
            var edges = focus.RootElement.GetProperty("OrderObservations").EnumerateArray().ToArray();
            Assert(edges.Length == 4 && edges.All(e => e.GetProperty("Kind").GetString() == "dependency")
                && edges.Any(e => e.GetProperty("Before").GetString() == "base.mod"
                    && e.GetProperty("State").GetString() == "dependent-inactive" && e.GetProperty("AfterIndex").GetInt32() == 0)
                && edges.Any(e => e.GetProperty("Before").GetString() == "missing.mod" && e.GetProperty("State").GetString() == "not-installed"));
        });
        check("DeepSeek focus exposes an order mismatch as a diagnostic-only constraint", () =>
        {
            using var focus = Focus(Request("deepseek-flash") with { StartupContext = new("ProbeCompleted", true, true, [new("TestOrderMismatch")]) });
            Assert(focus.RootElement.GetProperty("TestOrderMismatch").GetBoolean()
                && !focus.RootElement.GetProperty("StartupAllowsOrderChanges").GetBoolean());
        });
        check("DeepSeek self references do not crowd real actionable relations, including inactive prerequisites", () =>
        {
            using var focus = Focus(Request("deepseek-flash") with { CurrentOrder = ["leaf.mod"], InstalledMods = ["leaf.mod", "middle.mod"],
                ModRequirements = [new("leaf.mod", ["leaf.mod", "middle.mod"], ["leaf.mod"], [], []), new("middle.mod", ["middle.mod"], [], [], [])] });
            Assert(focus.RootElement.GetProperty("IgnoredSelfReferences").GetInt32() == 3
                && focus.RootElement.GetProperty("OrderObservations").GetArrayLength() == 1);
        });
    }

    private static string Envelope(string answer = Answer) => JsonSerializer.Serialize(new { choices = new[] { new {
        message = new { content = answer, reasoning_content = "private-fixture-secret" }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 } });
    private static AiRequest Request(string model) => new(AiSettings.Default with { Enabled = true, Model = model },
        "synthetic-key", "1.7", [], [], [], []);
    private static JsonDocument Focus(AiRequest request) => JsonDocument.Parse(AiPrompt.Messages(request)[2].Content.Split('\n')[1]);
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
