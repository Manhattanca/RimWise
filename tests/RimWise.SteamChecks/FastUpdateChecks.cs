using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Marketplace;
using RimWise.Integration.Updates;

internal static class FastUpdateChecks
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("可信更新测试文件");
    private static ReleaseDownload Candidate(byte[]? payload = null) => new("0.7.2", "RimWise-v0.7.2.zip",
        new Uri("https://github.com/Manhattanca/RimWise/releases/download/v0.7.2/RimWise-v0.7.2.zip"),
        (payload ?? Payload).Length, Convert.ToHexString(SHA256.HashData(payload ?? Payload)), false);

    internal static void Run(Action<string, Action> check)
    {
        check("Fast update switches blocked headers in one to two seconds, even if handler ignores cancellation", () => InDirectory(async dir =>
        {
            var stalled = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var primary = new Handler((_, _) => stalled.Task);
            using var backup = new Handler((request, _) =>
            {
                VerifyMirror(request);
                return Task.FromResult(Response(Payload));
            });
            using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
            var timer = Stopwatch.StartNew();
            var path = await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(), dir, null, default);
            Assert(timer.Elapsed.TotalSeconds is >= 0.85 and < 2);
            Assert(primary.Calls == 1 && backup.Calls == 1 && File.ReadAllBytes(path).SequenceEqual(Payload));
            stalled.SetResult(Response(Payload));
            await Task.Delay(100);
            Assert(!Directory.EnumerateFiles(dir, "*.partial").Any());
        }));
        check("Fast update detects a stalled body without waiting for the next data chunk", () => InDirectory(async dir =>
        {
            using var body = new ThrottledStream(Payload, TimeSpan.FromSeconds(10));
            using var primary = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
            using var backup = new Handler((_, _) => Task.FromResult(Response(Payload)));
            using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
            var timer = Stopwatch.StartNew();
            await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(), dir, null, default);
            Assert(timer.Elapsed.TotalSeconds is >= 0.85 and < 2 && backup.Calls == 1);
            await Task.Delay(50);
            Assert(!Directory.EnumerateFiles(dir, "*.partial").Any());
        }));
        check("Fast update switches continuous sub-MB traffic and resets progress for the mirror", () => InDirectory(async dir =>
        {
            var bytes = new byte[3_000_000];
            using var body = new ThrottledStream(bytes, TimeSpan.FromMilliseconds(100));
            using var primary = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
            using var backup = new Handler((_, _) => Task.FromResult(Response(bytes)));
            using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
            var notices = new List<DownloadProgress>();
            var timer = Stopwatch.StartNew();
            await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(bytes), dir, new ProgressSink(p => { lock(notices) notices.Add(p); }), default);
            Assert(timer.Elapsed.TotalSeconds < 2 && backup.Calls == 1);
            Assert(notices.Any(p => p.UsingMirror && p.Received == 0) && notices.Last().UsingMirror && notices.Last().Received == bytes.Length);
        }));
        check("Fast update does not switch a small completed file or a sufficiently fast body", () =>
        {
            foreach (var bytes in new[] { Payload, new byte[3_000_000] }) InDirectory(async dir =>
            {
                using var primary = new Handler((_, _) => Task.FromResult(Response(bytes)));
                using var backup = new Handler((_, _) => throw new Exception("Unexpected mirror"));
                using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
                await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(bytes), dir, null, default);
                Assert(backup.Calls == 0);
            });
        });
        check("Fast update switches connection failures immediately", () => InDirectory(async dir =>
        {
            using var primary = new Handler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")));
            using var backup = new Handler((_, _) => Task.FromResult(Response(Payload)));
            using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
            var timer = Stopwatch.StartNew();
            await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(), dir, null, default);
            Assert(timer.Elapsed.TotalSeconds < 0.8 && backup.Calls == 1);
        }));
        check("Fast update cancellation never starts the mirror and removes partial files", () => InDirectory(async dir =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            using var body = new ThrottledStream(Payload, TimeSpan.FromSeconds(10));
            using var primary = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
            using var backup = new Handler((_, _) => throw new Exception("Cancellation must not fail over"));
            using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
            try { await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(), dir, null, cancel.Token); throw new Exception("Cancellation lost"); }
            catch (OperationCanceledException) { }
            await Task.Delay(50);
            Assert(backup.Calls == 0 && !Directory.EnumerateFiles(dir, "*.partial").Any() && !File.Exists(Path.Combine(dir, Candidate().FileName)));
        }));
        check("Fast update preserves sustained above-threshold traffic across multiple watchdog ticks", () => InDirectory(async dir =>
        {
            var bytes = new byte[5_000_000];
            using var body = new ThrottledStream(bytes, TimeSpan.FromMilliseconds(10));
            using var primary = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
            using var backup = new Handler((_, _) => throw new Exception("Fast source must not switch"));
            using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
            await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(bytes), dir, null, default);
            Assert(backup.Calls == 0);
        }));
        check("Fast update notices a later stall after the first fast window", () => InDirectory(async dir =>
        {
            var bytes = new byte[5_000_000];
            using var body = new LaterStallStream(bytes);
            using var primary = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
            using var backup = new Handler((_, _) => Task.FromResult(Response(bytes)));
            using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
            var timer = Stopwatch.StartNew();
            await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(bytes), dir, null, default);
            Assert(timer.Elapsed.TotalSeconds is >= 1.8 and < 3 && backup.Calls == 1);
        }));
        check("Fast mirror enforces size and digest, refuses redirects and never commits bad content", () =>
        {
            foreach (var response in new Func<HttpResponseMessage>[] {
                () => Response(new byte[Payload.Length]), () => Response(Payload[..^1]),
                () => Response(Payload.Concat(new byte[] { 0 }).ToArray()),
                () => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.invalid/evil.exe") } } })
                InDirectory(async dir =>
                {
                    using var primary = new Handler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException()));
                    using var backup = new Handler((_, _) => Task.FromResult(response()));
                    using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
                    try { await new GitHubReleaseUpdater(http, mirror).DownloadAsync(Candidate(), dir, null, default); throw new Exception("Bad mirror accepted"); }
                    catch (UpdateDownloadException) { }
                    Assert(backup.Calls == 1 && !File.Exists(Path.Combine(dir, Candidate().FileName)) && !Directory.EnumerateFiles(dir, "*.partial").Any());
                });
        });
        check("Fast version lookup fails over and subsequent download skips the known slow source", () => InDirectory(async dir =>
        {
            using var primary = new Handler(async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(10), token); return Response([]); });
            using var backup = new Handler((request, _) =>
            {
                Assert(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"));
                if (request.RequestUri!.AbsolutePath.EndsWith("/latest"))
                {
                    var c = Candidate();
                    return Task.FromResult(Response(JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v0.7.2", draft = false, prerelease = false,
                        assets = new[] { new { name = c.FileName, browser_download_url = c.Url.AbsoluteUri, size = c.Size, state = "uploaded", digest = "sha256:" + c.Sha256 } } })));
                }
                VerifyMirror(request); return Task.FromResult(Response(Payload));
            });
            using var http = new HttpClient(primary); using var mirror = new HttpClient(backup);
            var updater = new GitHubReleaseUpdater(http, mirror);
            var timer = Stopwatch.StartNew();
            var release = await updater.CheckAsync("0.7.1", default);
            Assert(timer.Elapsed.TotalSeconds is >= 0.85 and < 2 && release is not null);
            await updater.DownloadAsync(release!, dir, null, default);
            Assert(primary.Calls == 1 && backup.Calls == 2);
        }));
    }

    internal static async Task ProbeLiveAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var primary = new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception("Unreachable"); });
        using var http = new HttpClient(primary);
        using var mirror = GitHubReleaseUpdater.CreateMirrorHttpClient();
        var updater = new GitHubReleaseUpdater(http, mirror);
        var timer = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var release = await updater.CheckAsync("0.7.0", timeout.Token) ?? throw new Exception("No mirrored release");
        var checkSeconds = timer.Elapsed.TotalSeconds;
        var delta = release.Delta ?? throw new Exception("Adjacent delta missing");
        var switchedSeconds = 0d;
        var downloadTimer = Stopwatch.StartNew();
        // A fresh updater exercises the asset watchdog too, through real pinned TLS and real mirrored bytes.
        var downloadUpdater = new GitHubReleaseUpdater(http, mirror);
        var path = await downloadUpdater.DownloadAsync(delta, directory, new ProgressSink(p =>
        {
            if (p.UsingMirror && p.Received == 0) switchedSeconds = downloadTimer.Elapsed.TotalSeconds;
        }), timeout.Token);
        var result = new { Passed = true, release.Version, delta.FromVersion, delta.FileName, delta.Size,
            delta.Sha256, CheckSeconds = checkSeconds, SwitchTriggeredSeconds = switchedSeconds,
            DownloadSeconds = downloadTimer.Elapsed.TotalSeconds, Verified = await GitHubReleaseUpdater.IsVerifiedAsync(path, delta, timeout.Token),
            GitHubRequests = primary.Calls, CredentialsSent = false, Trust = "Embedded scoped TLS root; exact HTTPS origin; SHA256 and length" };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "live-probe.json"), json);
        Console.WriteLine(json);
    }

    private static void VerifyMirror(HttpRequestMessage request) => Assert(request.Method == HttpMethod.Get &&
        request.RequestUri == new Uri(MarketApiConnection.DefaultOrigin, "api/updates/files/0.7.2/RimWise-v0.7.2.zip") &&
        request.Headers.Authorization is null && !request.Headers.Contains("Cookie"));
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static void Assert(bool value) { if (!value) throw new Exception("Fast update assertion failed"); }
    private static void InDirectory(Func<string, Task> test)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "work", "fast-update-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); test(dir).GetAwaiter().GetResult();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return respond(request, token); }
    }
    private sealed class ProgressSink(Action<DownloadProgress> action) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => action(value); }
    private sealed class ThrottledStream(byte[] bytes, TimeSpan delay) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(delay, cancellationToken); return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 32768)], cancellationToken); }
    }
    private sealed class LaterStallStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 2_000_000) await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
