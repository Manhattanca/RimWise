using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Updates;

internal static class ReleaseNotesChecks
{
    internal static void Run(Action<string, Action> check)
    {
        byte[] Json(string tag = "v0.7.3", string? body = "第一段。\n\n第二段。", bool draft = false, bool prerelease = false)
            => JsonSerializer.SerializeToUtf8Bytes(new { tag_name = tag, body, draft, prerelease });
        check("Release notes retain exact Chinese text and paragraph breaks", () =>
            Assert(GitHubReleaseUpdater.ParseReleaseNotes(Json(), "0.7.3")?.Body == "第一段。\n\n第二段。"));
        check("Release notes reject latest of a different version and preview/draft text", () =>
        {
            Assert(GitHubReleaseUpdater.ParseReleaseNotes(Json("v0.7.4"), "0.7.3") is null);
            Assert(GitHubReleaseUpdater.ParseReleaseNotes(Json(draft: true), "0.7.3") is null);
            Assert(GitHubReleaseUpdater.ParseReleaseNotes(Json(prerelease: true), "0.7.3") is null);
            Assert(GitHubReleaseUpdater.ParseReleaseNotes(Json(), "0.7.3-dev.1") is null);
            Assert(GitHubReleaseUpdater.ParseReleaseNotes(Json(body: null), "0.7.3") is null);
        });
        check("Incomplete or incorrectly typed optional release metadata is safely ignored", () =>
        {
            foreach (var json in new[] { "{}", "[]", "null", "{\"draft\":\"false\"}", "{\"draft\":false,\"prerelease\":false}" })
                Assert(GitHubReleaseUpdater.ParseReleaseNotes(Encoding.UTF8.GetBytes(json), "0.7.3") is null);
        });
        check("Release notes fetch installed tag with no account authorization", () =>
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert(request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v0.7.3", StringComparison.Ordinal));
                Assert(request.Headers.Authorization is null);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Json()) };
            }));
            Assert(new GitHubReleaseUpdater(client).ReadReleaseNotesAsync("0.7.3", default).GetAwaiter().GetResult()?.Version == "0.7.3");
        });
        check("Release notes cache and seen receipts persist across store instances and versions", () => WithDirectory(root =>
        {
            var first = new ReleaseNotesStore(root); first.Cache(new("0.7.3", "精确原文。"));
            Assert(!first.HasShown("0.7.3")); first.MarkShown("0.7.3");
            var second = new ReleaseNotesStore(root);
            Assert(second.Read("0.7.3")?.Body == "精确原文。" && second.HasShown("0.7.3") && !second.HasShown("0.7.4"));
        }));
        check("Release notes exclusive lease prevents two simultaneous first-start dialogs", () => WithDirectory(root =>
        {
            var store = new ReleaseNotesStore(root);
            using (store.Acquire("0.7.3"))
            {
                try { using var duplicate = new ReleaseNotesStore(root).Acquire("0.7.3"); throw new Exception("duplicate lease"); }
                catch (IOException) { }
            }
            using var next = store.Acquire("0.7.3");
        }));
        check("Release notes reject path traversal and oversized content before writing", () => WithDirectory(root =>
        {
            var store = new ReleaseNotesStore(root);
            foreach (var version in new[] { "../escape", "0.7.3/../", "v0.7.3", "0.7.3-dev.1" })
            {
                try { store.HasShown(version); throw new Exception("invalid version accepted"); } catch (ArgumentException) { }
            }
            try { store.Cache(new("0.7.3", new string('a', 65537))); throw new Exception("oversize accepted"); } catch (InvalidDataException) { }
        }));
        check("Release notes mismatched or malformed seen/cache data is not trusted", () => WithDirectory(root =>
        {
            var store = new ReleaseNotesStore(root); store.Cache(new("0.7.3", "原文。"));
            var directory = Path.Combine(root, "Data", "ReleaseNotes");
            File.WriteAllText(Path.Combine(directory, "0.7.3.json"), JsonSerializer.Serialize(new InstalledReleaseNotes("0.7.4", "其他版本。")));
            File.WriteAllText(Path.Combine(directory, "0.7.3.seen"), "0.7.4");
            Assert(store.Read("0.7.3") is null && !store.HasShown("0.7.3"));
        }));
        check("Release notes do not execute or rewrite markup", () =>
        {
            const string text = "<script>alert(1)</script> [link](file:///C:/test)";
            Assert(GitHubReleaseUpdater.ParseReleaseNotes(Json(body: text), "0.7.3")?.Body == text);
        });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Release notes check failed"); }
    private static void WithDirectory(Action<string> action)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "work", "release-notes-checks", "中文 缓存", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); action(root);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
