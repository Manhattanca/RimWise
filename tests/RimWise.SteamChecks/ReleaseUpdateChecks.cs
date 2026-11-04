using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Updates;

internal static class ReleaseUpdateChecks
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("Synthetic update payload; never executed.");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    private const string Tag = "v0.5.0";
    private const string ZipName = "RimWise-v0.5.0.zip";
    private static object Asset(string name = ZipName, string? url = null, string? digest = null, long? size = null) => new
    {
        name, browser_download_url = url ?? $"https://github.com/{GitHubReleaseUpdater.Repository}/releases/download/{Tag}/{name}",
        digest = digest ?? "sha256:" + Hash, size = size ?? Payload.Length, state = "uploaded"
    };
    private static byte[] Release(string tag = Tag, bool draft = false, bool prerelease = false, params object[] assets) =>
        JsonSerializer.SerializeToUtf8Bytes(new { tag_name = tag, draft, prerelease, assets = assets.Length == 0 ? new[] { Asset() } : assets });
    private static ReleaseDownload Candidate() => GitHubReleaseUpdater.ParseRelease(Release(), "0.4.9")!;

    internal static void Run(Action<string, Action> check)
    {
        check("Updater compares numeric versions and ignores drafts, prereleases and older releases", () =>
        {
            Assert(GitHubReleaseUpdater.ParseRelease(Release(), "0.4.9-dev.20261104") is not null);
            Assert(GitHubReleaseUpdater.ParseRelease(Release(), "0.5.0.0") is null);
            Assert(GitHubReleaseUpdater.ParseRelease(Release(), "0.10.0") is null);
            Assert(GitHubReleaseUpdater.ParseRelease(Release(draft: true), "0.4.9") is null);
            Assert(GitHubReleaseUpdater.ParseRelease(Release(prerelease: true), "0.4.9") is null);
            Assert(GitHubReleaseUpdater.ParseRelease(Release(tag: "v0.5.0.1", assets: [Asset("RimWise-v0.5.0.1.zip",
                $"https://github.com/{GitHubReleaseUpdater.Repository}/releases/download/v0.5.0.1/RimWise-v0.5.0.1.zip")]), "0.5.0") is not null);
        });
        check("Updater prefers installer and falls back only to the exact program ZIP", () =>
        {
            var release = GitHubReleaseUpdater.ParseRelease(Release(assets: [Asset(), Asset("RimWise-v0.5.0-Setup.exe")]), "0.4.9");
            Assert(release is { IsInstaller: true });
            ExpectFailure(() => GitHubReleaseUpdater.ParseRelease(Release(assets: [Asset("RimWise-v0.5.0-source.zip")]), "0.4.9"));
        });
        check("Updater rejects changed repository, digest, duplicated assets and malformed versions", () =>
        {
            foreach (var bytes in new[] { Release(assets: [Asset(url: "https://example.invalid/update.zip")]),
                Release(assets: [Asset(digest: "")]), Release(assets: [Asset(), Asset()]),
                Release(tag: "v0.5.0-beta"), Release(assets: [Asset(size: -1)]) })
                ExpectFailure(() => GitHubReleaseUpdater.ParseRelease(bytes, "0.4.9"));
        });
        check("Public release lookup never sends user credentials and keeps HTTP errors distinct", () =>
        {
            using var handler = new Handler(request =>
            {
                Assert(request.RequestUri!.Host == "api.github.com" && request.Headers.Authorization is null);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Release()) };
            });
            using var client = new HttpClient(handler);
            Assert(new GitHubReleaseUpdater(client).CheckAsync("0.4.9", default).GetAwaiter().GetResult() is not null);
            foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
            {
                using var failureHandler = new Handler(_ => new HttpResponseMessage(status));
                using var failureClient = new HttpClient(failureHandler);
                ExpectFailure(() => new GitHubReleaseUpdater(failureClient).CheckAsync("0.4.9", default).GetAwaiter().GetResult());
            }
        });
        check("Verified downloads use Unicode paths, cache without redownloading and preserve unrelated files", () => WithDirectory(directory =>
        {
            using var handler = new Handler(_ => Response(Payload));
            using var client = new HttpClient(handler);
            var updater = new GitHubReleaseUpdater(client);
            var candidate = Candidate();
            File.WriteAllText(Path.Combine(directory, "personal.txt"), "keep");
            var old = "RimWise-v0.4.8.zip";
            File.WriteAllText(Path.Combine(directory, old), "old");
            File.WriteAllText(Path.Combine(directory, "latest-download.json"), JsonSerializer.Serialize(old));
            var path = updater.DownloadAsync(candidate, directory, null, default).GetAwaiter().GetResult();
            Assert(File.ReadAllBytes(path).SequenceEqual(Payload));
            Assert(!File.Exists(Path.Combine(directory, old)) && File.Exists(Path.Combine(directory, "personal.txt")));
            Assert(updater.DownloadAsync(candidate, directory, null, default).GetAwaiter().GetResult() == path && handler.Calls == 1);
            Assert(!Directory.EnumerateFiles(directory, "*.partial").Any());
        }));
        check("Corrupt, short and oversized downloads never become a completed update", () =>
        {
            foreach (var bytes in new[] { Encoding.UTF8.GetBytes(new string('x', Payload.Length)), Payload[..^1], Payload.Concat(new byte[] { 0 }).ToArray() })
                WithDirectory(directory =>
                {
                    using var handler = new Handler(_ => Response(bytes));
                    using var client = new HttpClient(handler);
                    ExpectFailure(() => new GitHubReleaseUpdater(client).DownloadAsync(Candidate(), directory, null, default).GetAwaiter().GetResult());
                    Assert(!File.Exists(Path.Combine(directory, ZipName)) && !Directory.EnumerateFiles(directory, "*.partial").Any());
                });
        });
        check("Update redirects stay on HTTPS GitHub asset hosts", () => WithDirectory(directory =>
        {
            using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
                { Headers = { Location = new Uri("http://example.invalid/payload.exe") } });
            using var client = new HttpClient(handler);
            ExpectFailure(() => new GitHubReleaseUpdater(client).DownloadAsync(Candidate(), directory, null, default).GetAwaiter().GetResult());
            Assert(handler.Calls == 1 && !Directory.EnumerateFiles(directory, "*.partial").Any());
        }));
        check("Valid GitHub CDN redirects download and validate normally", () => WithDirectory(directory =>
        {
            using var handler = new Handler(request => request.RequestUri!.Host == "github.com"
                ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/asset") } }
                : Response(Payload));
            using var client = new HttpClient(handler);
            new GitHubReleaseUpdater(client).DownloadAsync(Candidate(), directory, null, default).GetAwaiter().GetResult();
            Assert(handler.Calls == 2);
        }));
        check("Update cancellation deletes partial data and does not replace previous files", () => WithDirectory(directory =>
        {
            using var cancel = new CancellationTokenSource();
            using var handler = new Handler(_ => { cancel.Cancel(); return Response(Payload); });
            using var client = new HttpClient(handler);
            try { new GitHubReleaseUpdater(client).DownloadAsync(Candidate(), directory, null, cancel.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation lost"); }
            catch (OperationCanceledException) { }
            Assert(!File.Exists(Path.Combine(directory, ZipName)) && !Directory.EnumerateFiles(directory, "*.partial").Any());
        }));
        check("Download write boundary rejects path traversal independently of release parser", () => WithDirectory(directory =>
        {
            using var handler = new Handler(_ => Response(Payload));
            using var client = new HttpClient(handler);
            ExpectFailure(() => new GitHubReleaseUpdater(client).DownloadAsync(Candidate() with { FileName = "../escape.exe" }, directory, null, default).GetAwaiter().GetResult());
            Assert(handler.Calls == 0);
        }));
    }

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (UpdateDownloadException) { return; }
        throw new Exception("Expected update failure");
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Release update check failed"); }
    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "work", "update-checks", "中文 缓存", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        action(directory);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
