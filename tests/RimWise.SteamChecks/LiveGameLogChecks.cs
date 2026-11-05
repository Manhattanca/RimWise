using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;

internal static class LiveGameLogChecks
{
    internal static void Run(Action<string, Action> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "实时日志 中文", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Player.log");
        check("UTF8 partial characters and lines survive append without duplicates", () =>
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("一行\n中文")[..^1]);
            var reader = new LiveGameLog(path);
            Assert(reader.Read().Lines.Single().Text == "一行");
            using (var file = new FileStream(path, FileMode.Append)) file.Write(Encoding.UTF8.GetBytes("文")[^1..]);
            File.AppendAllText(path, "结束\n");
            var batch = reader.Read();
            Assert(!batch.Reset && batch.Lines.Single().Text == "中文结束" && batch.TotalLines == 2);
            Assert(reader.Read().Lines.Count == 0);
        });
        check("Shared writer, rotation and truncation reset counts and preserve raw bytes", () =>
        {
            File.WriteAllText(path, "Error: original\n", new UTF8Encoding(false));
            var reader = new LiveGameLog(path);
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                Assert(reader.Read().Counts[2] == 1);
            File.Move(path, path + ".old");
            File.WriteAllText(path, "new\n");
            Assert(reader.Read() is { Reset: true, TotalLines: 1 } reset && reset.Counts[2] == 0);
            File.WriteAllText(path, "x\n");
            Assert(reader.Read().Lines.Single().Text == "x");
            Assert(File.ReadAllText(path + ".old") == "Error: original\n");
        });
        check("UTF16 and authoritative structured severity are retained", () =>
        {
            File.WriteAllText(path, "2026-11-05 12:00:00 INFO Error reporting ready\n2026-11-05 12:00:01 WARN optional content\n  System.Exception: example\n2026-11-05 12:00:02 ERROR invalid definition\n  at Example.Call()\n", Encoding.Unicode);
            var batch = new LiveGameLog(path).Read();
            Assert(batch.Counts.SequenceEqual(new long[] { 1, 2, 2, 0 }));
            Assert(batch.Lines[0].Text.StartsWith("2026"));
        });
        check("Large logs read in bounded batches without changing source", () =>
        {
            File.WriteAllText(path, string.Concat(Enumerable.Repeat("中文 example information\n", 40000)));
            var original = File.ReadAllBytes(path);
            var reader = new LiveGameLog(path);
            var batch = reader.Read();
            Assert(batch.HasMore && batch.BytesRead <= 256 * 1024);
            while (batch.HasMore) batch = reader.Read();
            Assert(batch.TotalLines == 40000 && original.SequenceEqual(File.ReadAllBytes(path)));
        });
        check("Missing source retries and a cancelled read does not consume data", () =>
        {
            var missing = Path.Combine(root, "waiting.log");
            var reader = new LiveGameLog(missing);
            try { reader.Read(); throw new Exception("Expected missing file"); } catch (FileNotFoundException) { }
            File.WriteAllText(missing, "ready\n");
            try { reader.Read(new CancellationToken(true)); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Assert(reader.Read().Lines.Single().Text == "ready");
        });
        foreach (var withUsage in new[] { true, false })
            check("Read-only AI prompt, redaction and durable usage: " + withUsage, () =>
            {
                File.WriteAllText(path, "Error: failed operation; credential=synthetic-secret-for-log-test\n at Example.Run()\n");
                var request = GameLogAnalysis.CreateRequest(PlayerLogReader.Read(path, new ModCatalogue([])), new([]),
                    AiSettings.Default with { Model = "fixture", Endpoint = "https://example.invalid/v1/" }, "synthetic-secret-for-log-test", default);
                var handler = new ReplyHandler(withUsage);
                using var http = new HttpClient(handler);
                using var client = new AiClient(http);
                var data = Path.Combine(root, "ai-" + withUsage);
                var result = GameLogAnalysis.AnalyzeAsync(request, data, client, default).GetAwaiter().GetResult();
                Assert(result.Succeeded && result.Response!.Actions.Count == 0 && result.Response.StartupDiagnosis!.Suggestions.Count == 0);
                Assert(handler.Calls == 1 && !handler.Body.Contains("synthetic-secret-for-log-test") && handler.Body.Contains("Owners"));
                var task = new AiTaskRecoveryStore(data).Inspect().Single().Checkpoint!;
                Assert(task.Kind == AiTaskKind.LogDiagnosis && task.Finished && !task.MayApply && task.Actions.Length == 0);
                Assert(withUsage ? task.ReportedTokens == 150 && !task.UnknownUsage : task.UnknownUsage && task.ReservedTokens > 0);
            });
        check("Invalid configuration fails before creating a request ledger", () =>
        {
            var request = GameLogAnalysis.CreateRequest(PlayerLogReader.Read(path, new([])), new([]), AiSettings.Default, null, default);
            var data = Path.Combine(root, "invalid");
            using var client = new AiClient(new HttpClient(new ReplyHandler(true)));
            try { GameLogAnalysis.AnalyzeAsync(request, data, client, default).GetAwaiter().GetResult(); throw new Exception("Expected validation"); }
            catch (ArgumentException) { Assert(!Directory.Exists(data)); }
        });
    }
    private sealed class ReplyHandler(bool usage) : HttpMessageHandler
    {
        public string Body = "";
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var answer = new { schemaVersion = 1, summary = "合成来源", actions = new[] { new { kind = "remove", packageId = "example.mod" } }, warnings = Array.Empty<string>(),
                startupDiagnosis = new { category = "unknown", reason = "日志未提供具体归属", suggestions = new[] { "不应执行" } } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { message = new { role = "assistant", content = JsonSerializer.Serialize(answer) }, finish_reason = "stop" } },
                usage = usage ? new { prompt_tokens = 100, completion_tokens = 50, total_tokens = 150 } : null })) };
        }
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Live game log regression"); }
}
