using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Marketplace;
using RimWise.Core;
using RimWise.Integration.Steam;

var output = Path.GetFullPath(args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "marketplace-results") : args[0]);
Directory.CreateDirectory(output);
var results = new List<object>();
var failed = 0;
foreach (var (name, check) in Checks.All(output))
{
    try
    {
        await check();
        results.Add(new { Name = name, Passed = true, Error = (string?)null });
        Console.WriteLine("PASS " + name);
    }
    catch (Exception exception)
    {
        failed++;
        results.Add(new { Name = name, Passed = false, Error = exception.ToString() });
        Console.WriteLine("FAIL " + name + ": " + exception);
    }
}
var report = JsonSerializer.Serialize(new { Passed = results.Count - failed, Failed = failed, Checks = results },
    new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), report, new UTF8Encoding(false));
Console.WriteLine($"{results.Count - failed} passed; {failed} failed. Result: {Path.Combine(output, "result.json")}");
return failed == 0 ? 0 : 1;

internal static class Checks
{
    private const string Token = "synthetic-test-token";
    private static readonly string Commit = new('a', 40);
    internal static MarketplaceManifest Sample(string author = "contributor") => new(1, "synthetic-order", "1.0.0",
        "合成排序", author, "Only a synthetic test fixture.", "1.6", ["ludeon.rimworld.royalty"], "123456789",
        [new("ludeon.rimworld", null), new("ludeon.rimworld.royalty", null), new("synthetic.example", "123")]);

    internal static IEnumerable<(string Name, Func<Task> Check)> All(string output)
    {
        yield return ("versions group by author and identity with numeric SemVer latest first", () => Sync(() =>
        {
            var source = new[] { "1.0.9", "1.0.10-rc.10", "1.0.10", "1.0.10-rc.2", "2.0.0-beta" }
                .Select(version => Sample() with { Version = version, ReleaseNotes = version });
            var entries = MarketplaceJson.CreateCatalogue(source).Entries;
            var groups = MarketplaceVersions.Group(entries.Concat([entries[0] with { Id = "another-order" }]));
            Equal(groups.Count, 2);
            Equal(string.Join(',', groups[0].Select(entry => entry.Version)), "2.0.0-beta,1.0.10,1.0.10-rc.10,1.0.10-rc.2,1.0.9");
            True(MarketplaceVersions.Compare("1.0.0", "1.0.0-rc") > 0);
            True(MarketplaceVersions.Compare("1.0.0-beta.2", "1.0.0-beta.11") < 0);
            Equal(MarketplaceVersions.Group([entries[0], entries[0] with { Author = "AnotherAuthor" }]).Count, 2);
        }));
        foreach (var check in ImageChecks.All(output)) yield return check;
        yield return ("software and marketplace repositories are separate", () => Sync(() =>
        {
            Equal(MarketplaceClient.CatalogueUrl, "https://raw.githubusercontent.com/Manhattanca/RimWise-Marketplace/main/marketplace/index.json");
            Equal(MarketplaceRepository.FullName, "Manhattanca/RimWise-Marketplace");
        }));
        if (Environment.GetEnvironmentVariable("RIMWISE_MARKETPLACE_LIVE_CATALOGUE") == "1")
            yield return ("live anonymous marketplace catalogue is reachable and cached", async () =>
            {
                using var live = new MarketplaceClient(Path.Combine(output, "live-cache"));
                var result = await live.GetCatalogueAsync();
                True(!result.FromCache && result.Value.SchemaVersion == 1);
            });
        if (Environment.GetEnvironmentVariable("RIMWISE_MARKETPLACE_LIVE_ORDER_ID") is { Length: > 0 } liveId)
            yield return ("live approved order is visible and its downloaded manifest verifies against the catalogue", async () =>
            {
                using var live = new MarketplaceClient(Path.Combine(output, "live-order-cache"));
                var result = await live.GetCatalogueAsync();
                var entry = result.Value.Entries.Single(e => e.Id == liveId);
                var downloaded = await live.GetManifestAsync(entry);
                True(!result.FromCache && !downloaded.FromCache);
                Equal(downloaded.Value.Id, liveId);
                Equal(downloaded.Value.Mods.Count, entry.ModCount!.Value);
            });
        yield return ("catalogue contains browse metadata without the mod list", () => Sync(() =>
        {
            var manifest = Sample() with { ReleaseNotes = "合成更新日志" };
            var entry = MarketplaceJson.CreateCatalogue([manifest]).Entries.Single();
            Equal(entry.ModCount, manifest.Mods.Count);
            Equal(entry.ReleaseNotes, manifest.ReleaseNotes);
            var bytes = MarketplaceJson.SerializeCatalogue(new(1, [entry]));
            True(!Encoding.UTF8.GetString(bytes).Contains("\"mods\"", StringComparison.Ordinal));
            Equal(MarketplaceJson.ParseCatalogue(bytes).Entries.Single().ModCount, manifest.Mods.Count);
        }));
        foreach (var declared in new[] { "1.7", "1.7.4985", "1.6", "1.6, 1.7", "1.5, 1.6" })
        {
            var versions = declared;
            yield return ("marketplace import only compares declared versions without inspecting mods: " + versions, () => Sync(() =>
            {
                var manifest = Sample() with { RequiredDlc = [], GameVersion = versions,
                    Mods = [new("missing.dependent", "123"), new("missing.prerequisite", "456")] };
                var notice = MarketplaceOrderCompatibility.VersionNotice(manifest, new(1, 7));
                True((notice.Length == 0) == versions.Contains("1.7"));
                if (notice.Length > 0) True(notice.Contains("版本不同仍按原排序导入并保存"));
                var converted = MarketplaceOrderCompatibility.ToOrderFile(manifest);
                True(converted.Configuration.Order.PackageIds.SequenceEqual(manifest.Mods.Select(mod => mod.PackageId)));
                True(converted.WorkshopReferences.Select(item => item.WorkshopId).SequenceEqual(["123", "456"]));
            }));
        }
        foreach (var scenario in new[] { "collection complete", "collection idle", "collection active", "collection failure", "cancelled",
            "unbound accept", "unbound decline", "unbound installed", "unbound cancel" })
        {
            var mode = scenario;
            yield return ("marketplace subscribes once and imports after batch completion or idle: " + mode, async () =>
            {
                var events = new List<string>();
                var manifest = Sample() with { RequiredDlc = [], Mods = [new("a.mod", "123"), new("b.mod", "456")] };
                if (mode.StartsWith("unbound")) manifest = manifest with { WorkshopCollectionId = null };
                using var cancellation = new CancellationTokenSource();
                var clock = new WorkshopTestClock();
                var queries = 0;
                var subscribed = mode == "unbound installed";
                var prompts = 0;
                var success = false;
                try
                {
                    var completed = await MarketplaceWorkshopDownloads.DownloadAsync(manifest, (ids, token) =>
                    {
                        queries++;
                        True(ids.SequenceEqual(["123", "456"]));
                        var installed = mode is "collection complete" or "unbound installed" or "unbound accept"
                            || mode == "collection active" && clock.Elapsed >= TimeSpan.FromSeconds(45);
                        return Task.FromResult<IReadOnlyList<WorkshopItemState>>([
                            new("123", true, true, Path.Combine(output, "123")),
                            new("456", subscribed, installed, installed ? Path.Combine(output, "456") : "",
                                Downloading: mode == "collection active" && !installed)]);
                    }, (id, _, token) =>
                    {
                        Equal(id, manifest.WorkshopCollectionId);
                        events.Add("collection");
                        if (mode == "collection failure") throw new IOException("synthetic collection failure");
                        subscribed = true;
                        if (mode == "cancelled") cancellation.Cancel();
                        return Task.FromResult<IReadOnlyList<WorkshopItemState>>([new("123", true, false, ""), new("456", true, false, "")]);
                    }, (ids, _, token) =>
                    {
                        True(ids.SequenceEqual(["456"]));
                        events.Add("enqueue-missing"); subscribed = true;
                        return Task.FromResult<IReadOnlyList<WorkshopItemState>>([new("456", true, false, "")]);
                    }, (count, token) =>
                    {
                        prompts++; Equal(count, 1);
                        if (mode == "unbound cancel") throw new OperationCanceledException(token);
                        return Task.FromResult(mode == "unbound accept");
                    }, null, cancellation.Token, clock, clock.Delay);
                    True(completed.Count == 2);
                    if (mode == "collection idle") True(!completed[1].Installed && clock.Elapsed == TimeSpan.FromSeconds(30));
                    if (mode == "collection active") True(completed[1].Installed && clock.Elapsed == TimeSpan.FromSeconds(45));
                    if (mode == "unbound decline") True(!completed[1].Subscribed && queries == 1);
                    success = true;
                }
                catch (IOException) when (mode == "collection failure") { }
                catch (OperationCanceledException) when (mode is "cancelled" or "unbound cancel") { }
                True(success == (mode is not ("cancelled" or "collection failure" or "unbound cancel")));
                Equal(prompts, mode.StartsWith("unbound") && mode != "unbound installed" ? 1 : 0);
                True(events.SequenceEqual(mode.StartsWith("unbound")
                    ? mode == "unbound accept" ? ["enqueue-missing"] : Array.Empty<string>() : ["collection"]));
            });
        }
        foreach (var scenario in new[] { "pending", "byte progress", "active cancellation" })
        {
            var mode = scenario;
            yield return ("Steam batch wait handles activity without restarting downloads: " + mode, async () =>
            {
                var clock = new WorkshopTestClock();
                using var cancellation = new CancellationTokenSource();
                var messages = new List<string>();
                try
                {
                    var result = await WorkshopDownloadMonitor.WaitAsync(["123"], (_, token) =>
                    {
                        if (mode == "active cancellation" && clock.Elapsed >= TimeSpan.FromSeconds(40)) cancellation.Cancel();
                        var bytes = mode == "byte progress" && clock.Elapsed >= TimeSpan.FromSeconds(20) ? 10ul : 0;
                        return Task.FromResult<IReadOnlyList<WorkshopItemState>>([new("123", true, false, "",
                            Downloading: mode == "active cancellation", DownloadPending: true, BytesDownloaded: bytes, BytesTotal: 100)]);
                    }, new ImmediateProgress(messages.Add), cancellation.Token, clock, clock.Delay);
                    True(mode != "active cancellation" && !result.Single().Installed);
                    Equal(clock.Elapsed, TimeSpan.FromSeconds(mode == "byte progress" ? 50 : 30));
                    True(messages.Last().Contains("未检测到") && !messages.Last().Contains("下载已完成"));
                }
                catch (OperationCanceledException) when (mode == "active cancellation")
                { Equal(clock.Elapsed, TimeSpan.FromSeconds(40)); }
            });
        }
        yield return ("bound collection content is monitored but never inserted into the authored order", async () =>
        {
            var manifest = Sample() with { RequiredDlc = [], Mods = [new("a.mod", "123")] };
            var result = await MarketplaceWorkshopDownloads.DownloadAsync(manifest, (ids, token) =>
            {
                True(ids.SequenceEqual(["123", "999"]));
                return Task.FromResult<IReadOnlyList<WorkshopItemState>>(ids.Select(id => new WorkshopItemState(id, true, true, Path.Combine(output, id))).ToArray());
            }, (_, _, _) => Task.FromResult<IReadOnlyList<WorkshopItemState>>([new("999", true, true, output)]),
                (_, _, _) => throw new InvalidOperationException("Unrelated manifest items must not be subscribed"),
                (_, _) => throw new InvalidOperationException("Bound collection must not prompt for individual subscriptions"), null, default);
            True(result.Select(item => item.Id).SequenceEqual(["123"]) && manifest.Mods.Count == 1);
        });
        yield return ("canonical UTF8 roundtrip preserves order and optional release notes", () => Sync(() =>
        {
            var manifest = Sample() with { ReleaseNotes = "新增说明\nsecond line" };
            var bytes = MarketplaceJson.SerializeManifest(manifest);
            var parsed = MarketplaceJson.ParseManifest(bytes);
            Equal(parsed.ReleaseNotes, manifest.ReleaseNotes);
            Equal(parsed.Mods[2].PackageId, "synthetic.example");
            True(bytes.SequenceEqual(MarketplaceJson.SerializeManifest(parsed)));
            True(!bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) && bytes[^1] == '}');
            Equal(MarketplaceJson.ParseManifest(MarketplaceJson.SerializeManifest(Sample())).ReleaseNotes, "");
        }));
        yield return ("SHA256 known vector hashes exact bytes", () => Sync(() =>
        {
            Equal(MarketplaceJson.ComputeSha256("abc"u8), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            var bytes = MarketplaceJson.SerializeManifest(Sample());
            True(MarketplaceJson.ComputeSha256(bytes) != MarketplaceJson.ComputeSha256(bytes.Concat(new byte[] { 10 }).ToArray()));
        }));
        yield return ("catalogue generation deterministic, namespaced, bounded and honest empty", () => Sync(() =>
        {
            var first = Sample();
            var second = Sample("another") with { Id = "another-order", Version = "2.0.0" };
            var a = MarketplaceJson.CreateCatalogue([first, second]);
            var b = MarketplaceJson.CreateCatalogue([second, first]);
            True(MarketplaceJson.SerializeCatalogue(a).SequenceEqual(MarketplaceJson.SerializeCatalogue(b)));
            Equal(a.Entries[1].ManifestPath, "marketplace/manifests/contributor/synthetic-order/1.0.0.json");
            Equal(a.Entries[1].Sha256, MarketplaceJson.ComputeSha256(MarketplaceJson.SerializeManifest(first)));
            Equal(MarketplaceJson.ParseCatalogue(MarketplaceJson.SerializeCatalogue(MarketplaceJson.CreateCatalogue([]))).Entries.Count, 0);
        }));
        yield return ("sorting identifiers retry library collisions case-insensitively", () => Sync(() =>
        {
            var candidates = new Queue<string>(["taken-id", "new-id"]);
            Equal(MarketplaceJson.GenerateUniqueId(["Taken-ID"], candidates.Dequeue), "new-id");
            Equal(candidates.Count, 0);
        }));
        yield return ("catalogue rejects an identifier shared by different authors but allows later versions by its owner", async () =>
        {
            var owner = Sample() with { Id = "shared-order", Version = "1.0.0" };
            var later = owner with { Version = "2.0.0" };
            Equal(MarketplaceJson.CreateCatalogue([owner, later]).Entries.Count, 2);
            await Throws<MarketplaceException>(() => Task.Run(() => MarketplaceJson.CreateCatalogue([owner, owner with { Author = "another" }])));
        });

        foreach (var (name, change) in InvalidManifests())
            yield return (name, () => Throws<MarketplaceException>(() => MarketplaceJson.SerializeManifest(change(Sample()))));

        foreach (var text in new[] { "{", "null", "[]", "{}", "{\"schemaVersion\":1,\"schemaVersion\":1,\"entries\":[]}",
                     "{\"schemaVersion\":1,\"SchemaVersion\":1,\"entries\":[]}", "{\"schemaVersion\":2,\"entries\":[]}",
                     "{\"schemaVersion\":1,\"entries\":[],\"script\":\"bad\"}" })
        {
            var input = text;
            yield return ("malformed or unsafe catalogue " + input, () => Throws<MarketplaceException>(() => MarketplaceJson.ParseCatalogue(Encoding.UTF8.GetBytes(input))));
        }
        yield return ("unknown executable fields, missing nullable fields, nested duplicate JSON rejected", async () =>
        {
            var json = Encoding.UTF8.GetString(MarketplaceJson.SerializeManifest(Sample()));
            foreach (var invalid in new[] { json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"script\":\"evil.ps1\""),
                         json.Replace("\"workshopCollectionId\":\"123456789\",", ""),
                         json.Replace("\"packageId\":\"synthetic.example\"", "\"packageId\":\"synthetic.example\",\"packageId\":\"other\"") })
                await Throws<MarketplaceException>(() => MarketplaceJson.ParseManifest(Encoding.UTF8.GetBytes(invalid)));
        });
        yield return ("oversized JSON rejected before parsing", async () =>
        {
            await Throws<MarketplaceException>(() => MarketplaceJson.ParseCatalogue(new byte[MarketplaceJson.MaximumCatalogueBytes + 1]));
            await Throws<MarketplaceException>(() => MarketplaceJson.ParseManifest(new byte[MarketplaceJson.MaximumManifestBytes + 1]));
        });
        foreach (var path in new[] { "../manifest.json", "/marketplace/manifests/contributor/synthetic-order/1.0.0.json",
                     "https://evil.example/a.json", "file:///C:/evil.json", "C:\\evil.json", "marketplace\\manifests\\a.json",
                     "marketplace/manifests/contributor/synthetic-order/../1.0.0.json", "marketplace/manifests/contributor/synthetic-order/%2e%2e/1.0.0.json",
                     "marketplace/manifests/contributor/synthetic-order/1.0.0.json?url=evil", "marketplace/manifests/other/synthetic-order/1.0.0.json" })
        {
            var input = path;
            yield return ("unsafe path rejected before transport: " + input, async () =>
            {
                var handler = new FakeHandler(_ => throw new Exception("Transport must not run."));
                using var client = new MarketplaceClient(handler: handler);
                var entry = Entry() with { ManifestPath = input };
                await Throws<MarketplaceException>(async () => await client.GetManifestAsync(entry));
                Equal(handler.Requests.Count, 0);
            });
        }
        yield return ("catalogue rejects duplicate identity, path, hashes and invalid hash", async () =>
        {
            var entry = Entry();
            await Throws<MarketplaceException>(() => MarketplaceJson.SerializeCatalogue(new(1, [entry, entry])));
            await Throws<MarketplaceException>(() => MarketplaceJson.SerializeCatalogue(new(1, [entry, entry with
            {
                Id = "different", ManifestPath = MarketplaceJson.GetManifestPath(entry.Author, "different", entry.Version)
            }])));
            await Throws<MarketplaceException>(() => MarketplaceJson.SerializeCatalogue(new(1, [entry with { Sha256 = new string('g', 64) }])));
        });
        yield return ("anonymous on-demand fixed endpoint and valid empty catalogue cache", async () =>
        {
            var directory = Fresh(output);
            var handler = new FakeHandler(request =>
            {
                Equal(request.RequestUri!.GetLeftPart(UriPartial.Path), MarketplaceClient.CatalogueUrl);
                True(request.Headers.Authorization is null);
                return Json(new MarketplaceCatalogue(1, []), codec: true);
            });
            using (var client = new MarketplaceClient(directory, handler))
            {
                Equal(handler.Requests.Count, 0);
                var result = await client.GetCatalogueAsync();
                Equal(result.Value.Entries.Count, 0);
                True(!result.FromCache);
            }
            using var offline = new MarketplaceClient(directory, Offline());
            var cached = await offline.GetCatalogueAsync();
            True(cached.FromCache && cached.Value.Entries.Count == 0 && cached.Warning is not null);
        });
        yield return ("explicit refresh escapes a stale CDN catalogue without login, polling or manifest prefetch", async () =>
        {
            var latest = MarketplaceJson.CreateCatalogue([Sample()]);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var handler = new FakeHandler(request =>
            {
                var uri = request.RequestUri!;
                Equal(uri.GetLeftPart(UriPartial.Path), MarketplaceClient.CatalogueUrl);
                True(request.Headers.Authorization is null);
                return Bytes(MarketplaceJson.SerializeCatalogue(
                    uri.Query.StartsWith("?rimwise-refresh=", StringComparison.Ordinal) && keys.Add(uri.Query) ? latest : new(1, [])));
            });
            using var client = new MarketplaceClient(Fresh(output), handler);
            Equal(handler.Requests.Count, 0);
            Equal((await client.GetCatalogueAsync()).Value.Entries.Count, 1);
            Equal((await client.GetCatalogueAsync()).Value.Entries.Count, 1);
            Equal(handler.Requests.Count, 2);
            Equal(keys.Count, 2);
        });
        yield return ("missing index is explicit error, no synthetic fallback", async () =>
        {
            using var client = new MarketplaceClient(Fresh(output), new FakeHandler(_ => Status(HttpStatusCode.NotFound)));
            await Throws<MarketplaceException>(async () => await client.GetCatalogueAsync());
        });
        yield return ("last valid catalogue and manifest survive malformed/hash/oversized responses and restart", async () =>
        {
            var directory = Fresh(output);
            var manifest = Sample();
            var bytes = MarketplaceJson.SerializeManifest(manifest);
            var entry = Entry();
            var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("index.json", StringComparison.Ordinal)
                ? Bytes(MarketplaceJson.SerializeCatalogue(new(1, [entry]))) : Bytes(bytes));
            using (var online = new MarketplaceClient(directory, handler))
            {
                True(!(await online.GetCatalogueAsync()).FromCache);
                True(!(await online.GetManifestAsync(entry)).FromCache);
            }
            foreach (var bad in new[] { "{"u8.ToArray(), bytes.Concat(new byte[] { 10 }).ToArray(), new byte[MarketplaceJson.MaximumManifestBytes + 1] })
            {
                using var client = new MarketplaceClient(directory, new FakeHandler(_ => Bytes(bad)));
                True((await client.GetCatalogueAsync()).FromCache);
                var result = await client.GetManifestAsync(entry);
                True(result.FromCache && result.Warning is not null);
                Equal(result.Value.Mods[2].WorkshopId, "123");
            }
            True(!Directory.EnumerateFiles(directory, "*.tmp").Any());
        });
        yield return ("immutable catalogue version cannot change hash or metadata", async () =>
        {
            var directory = Fresh(output);
            var original = Entry();
            using (var client = new MarketplaceClient(directory, new FakeHandler(_ => Bytes(MarketplaceJson.SerializeCatalogue(new(1, [original]))))))
                await client.GetCatalogueAsync();
            var modified = original with { Sha256 = new string('b', 64), Title = "changed" };
            using var changed = new MarketplaceClient(directory, new FakeHandler(_ => Bytes(MarketplaceJson.SerializeCatalogue(new(1, [modified])))));
            var result = await changed.GetCatalogueAsync();
            True(result.FromCache);
            Equal(result.Value.Entries[0].Sha256, original.Sha256);
        });
        yield return ("hash mismatch, metadata mismatch, and corrupt cache fail closed", async () =>
        {
            var directory = Fresh(output);
            var entry = Entry();
            using (var client = new MarketplaceClient(directory, new FakeHandler(_ => Bytes(MarketplaceJson.SerializeManifest(Sample() with { Title = "wrong" })))))
                await Throws<MarketplaceException>(async () => await client.GetManifestAsync(entry));
            var wrongMetadata = entry with { Title = "wrong" };
            using (var client = new MarketplaceClient(directory, new FakeHandler(_ => Bytes(MarketplaceJson.SerializeManifest(Sample())))))
                await Throws<MarketplaceException>(async () => await client.GetManifestAsync(wrongMetadata));
            await File.WriteAllTextAsync(Path.Combine(directory, "rw-marketplace-index.json"), "{");
            using var offline = new MarketplaceClient(directory, Offline());
            await Throws<MarketplaceException>(async () => await offline.GetCatalogueAsync());
        });
        yield return ("oversized declared and streaming data refused", async () =>
        {
            foreach (var declared in new[] { true, false })
            {
                var bytes = new byte[MarketplaceJson.MaximumCatalogueBytes + 1];
                using var client = new MarketplaceClient(handler: new FakeHandler(_ => declared ? Bytes(bytes) :
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(bytes) }));
                await Throws<MarketplaceException>(async () => await client.GetCatalogueAsync());
            }
        });
        yield return ("HTTP redirect is refused and never followed", async () =>
        {
            var handler = new FakeHandler(_ =>
            {
                var response = Status(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://evil.example/steal");
                return response;
            });
            using var client = new MarketplaceClient(handler: handler);
            await Throws<MarketplaceException>(async () => await client.GetCatalogueAsync());
            Equal(handler.Requests.Count, 1);
        });
        yield return ("cache write failure warns without losing verified network result", async () =>
        {
            var file = Path.Combine(Fresh(output), "not-a-directory");
            await File.WriteAllTextAsync(file, "test");
            using var client = new MarketplaceClient(file, new FakeHandler(_ => Bytes(MarketplaceJson.SerializeCatalogue(new(1, [])))));
            var result = await client.GetCatalogueAsync();
            True(!result.FromCache && result.Warning is not null);
        });
        yield return ("pre-cancelled browse and download make no requests", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var handler = Offline();
            using var client = new MarketplaceClient(handler: handler);
            await Throws<OperationCanceledException>(async () => await client.GetCatalogueAsync(cancellation.Token));
            await Throws<OperationCanceledException>(async () => await client.GetManifestAsync(Entry(), cancellation.Token));
            Equal(handler.Requests.Count, 0);
        });
        yield return ("inflight cancellation propagates without cache fallback", async () =>
        {
            var directory = Fresh(output);
            using (var good = new MarketplaceClient(directory, new FakeHandler(_ => Bytes(MarketplaceJson.SerializeCatalogue(new(1, []))))))
                await good.GetCatalogueAsync();
            using var cancellation = new CancellationTokenSource();
            using var client = new MarketplaceClient(directory, new FakeHandler(async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return Status(HttpStatusCode.OK);
            }));
            await Throws<OperationCanceledException>(async () => await client.GetCatalogueAsync(cancellation.Token));
        });
        yield return ("manifest cache is bounded and preserves Unicode and space directory", async () =>
        {
            var directory = Path.Combine(Fresh(output), "中文 cache & space");
            MarketplaceManifest next = Sample();
            using var client = new MarketplaceClient(directory, new FakeHandler(_ => Bytes(MarketplaceJson.SerializeManifest(next))));
            for (var i = 0; i < 130; i++)
            {
                next = Sample() with { Version = $"1.0.{i}" };
                var entry = MarketplaceJson.CreateCatalogue([next]).Entries[0];
                await client.GetManifestAsync(entry);
            }
            Equal(Directory.EnumerateFiles(directory, "rw-marketplace-manifest-*.json").Count(), 128);
            True(!Directory.EnumerateFiles(directory, "*.tmp").Any());
        });
        yield return ("OAuth requires a configured public client id", () => Throws<MarketplaceException>(() => new GitHubDeviceAuthorization("")));
        yield return ("OAuth browser flow validates callback, state and PKCE before exchanging code", async () =>
        {
            const string clientId = "syntheticClientId";
            const string clientSecret = "synthetic-client-secret";
            Uri? authorizationUri = null;
            Uri? callbackUri = null;
            var requests = 0;
            var handler = new FakeHandler(request =>
            {
                requests++;
                Equal(request.RequestUri!.AbsoluteUri, "https://github.com/login/oauth/access_token");
                var form = ParseForm(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Equal(form["client_id"], clientId);
                Equal(form["client_secret"], clientSecret);
                Equal(form["code"], "synthetic-auth-code");
                callbackUri = new Uri(form["redirect_uri"]);
                Equal(callbackUri.Host, "127.0.0.1");
                Equal(callbackUri.AbsolutePath, "/callback");
                var verifier = form["code_verifier"];
                var digest = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Equal(ParseForm(authorizationUri!.Query.TrimStart('?'))["code_challenge"], digest);
                return Json(new { access_token = Token, token_type = "bearer", scope = "public_repo" });
            });
            using var auth = new GitHubLoopbackAuthorization(clientId, clientSecret, handler);
            using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var granted = await auth.AuthorizeAsync(async (uri, token) =>
            {
                authorizationUri = uri;
                Equal(uri.Scheme, "https");
                Equal(uri.Host, "github.com");
                Equal(uri.AbsolutePath, "/login/oauth/authorize");
                var parameters = ParseForm(uri.Query.TrimStart('?'));
                Equal(parameters["client_id"], clientId);
                Equal(parameters["scope"], "public_repo");
                Equal(parameters["code_challenge_method"], "S256");
                var redirect = new Uri(parameters["redirect_uri"]);
                Equal(redirect.Host, "127.0.0.1");
                Equal(redirect.AbsolutePath, "/callback");
                callbackUri = redirect;
                var state = parameters["state"];
                using var browser = new HttpClient();
                using var rejected = await browser.GetAsync(new Uri(redirect + "?code=wrong&state=invalid"), token);
                Equal(rejected.StatusCode, HttpStatusCode.BadRequest);
                using var returned = await browser.GetAsync(new Uri(redirect + "?code=synthetic-auth-code&state=" + Uri.EscapeDataString(state)), token);
                Equal(returned.StatusCode, HttpStatusCode.OK);
                var html = await returned.Content.ReadAsStringAsync(token);
                True(html.Contains("RimWise", StringComparison.Ordinal) && html.Contains("prefers-color-scheme", StringComparison.Ordinal));
                True(!html.Contains("synthetic-auth-code", StringComparison.Ordinal) && !html.Contains(state, StringComparison.Ordinal));
                Equal(returned.Headers.GetValues("Referrer-Policy").Single(), "no-referrer");
                True(returned.Headers.Contains("Content-Security-Policy"));
                await File.WriteAllTextAsync(Path.Combine(output, "oauth-callback.html"), html, token);
            }, testTimeout.Token);
            Equal(granted.AccessToken, Token);
            Equal(requests, 1);
            True(callbackUri!.Port is > 0 and <= 65535);
            var invalidRedirectRejected = false;
            try { GitHubLoopbackAuthorization.BuildAuthorizationUri(clientId, new Uri("http://127.0.0.1/callback"), new string('a', 43), new string('b', 43)); }
            catch (MarketplaceException) { invalidRedirectRejected = true; }
            True(invalidRedirectRejected);
        });
        yield return ("OAuth browser flow refuses missing client secret and expired callback", async () =>
        {
            await Throws<MarketplaceException>(() => Sync(() => _ = new GitHubLoopbackAuthorization("client", "")));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            using var auth = new GitHubLoopbackAuthorization("client", "secret", new FakeHandler(_ => throw new InvalidOperationException("No token request is expected")));
            await Throws<OperationCanceledException>(() => auth.AuthorizeAsync((_, _) => Task.CompletedTask, cancellation.Token));
        });
        yield return ("OAuth pending, slow_down and success preserve public_repo scope", async () =>
        {
            var clock = new FastClock();
            var polls = 0;
            var handler = new FakeHandler(request =>
            {
                True(request.Headers.Authorization is null);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                True(!body.Contains("client_secret", StringComparison.Ordinal));
                if (request.RequestUri!.AbsolutePath == "/login/device/code")
                {
                    True(body.Contains("scope=public_repo", StringComparison.Ordinal));
                    return Device();
                }
                True(body.Contains("device_code=synthetic-device", StringComparison.Ordinal));
                return ++polls switch
                {
                    1 => Json(new { error = "authorization_pending" }),
                    2 => Json(new { error = "slow_down" }),
                    _ => Json(new { access_token = Token, token_type = "bearer", scope = "public_repo" })
                };
            });
            using var auth = new GitHubDeviceAuthorization("syntheticClientId", handler, clock);
            var code = await auth.StartAsync();
            Equal(code.VerificationUri.AbsoluteUri, "https://github.com/login/device");
            var token = await auth.PollAsync(code);
            Equal(token.AccessToken, Token);
            True(!token.ToString().Contains(Token, StringComparison.Ordinal));
            True(clock.Delays.SequenceEqual(new[] { 1d, 1d, 6d }));
        });
        foreach (var error in new[] { "access_denied", "expired_token", "incorrect_client_credentials", "device_flow_disabled", "unknown_error" })
        {
            var failure = error;
            yield return ("OAuth terminal error " + failure, async () =>
            {
                using var auth = new GitHubDeviceAuthorization("test", new FakeHandler(request =>
                    request.RequestUri!.AbsolutePath == "/login/device/code" ? Device() : Json(new { error = failure })), new FastClock());
                var code = await auth.StartAsync();
                await Throws<MarketplaceException>(async () => await auth.PollAsync(code));
            });
        }
        yield return ("OAuth local expiry and unsafe verification URL fail closed", async () =>
        {
            var polls = 0;
            using (var auth = new GitHubDeviceAuthorization("test", new FakeHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/login/device/code") return Device(expires: 2);
                polls++; return Json(new { error = "authorization_pending" });
            }), new FastClock()))
            {
                var code = await auth.StartAsync();
                await Throws<MarketplaceException>(async () => await auth.PollAsync(code));
                Equal(polls, 1);
            }
            using var unsafeAuth = new GitHubDeviceAuthorization("test", new FakeHandler(_ =>
                Json(new { device_code = "d", user_code = "u", verification_uri = "https://evil.example", expires_in = 300, interval = 1 })));
            await Throws<MarketplaceException>(async () => await unsafeAuth.StartAsync());
        });
        yield return ("OAuth cancellation stops polling immediately", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var handler = new FakeHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/login/device/code") return Device();
                cancellation.Cancel(); return Json(new { error = "authorization_pending" });
            });
            using var auth = new GitHubDeviceAuthorization("test", handler, new FastClock());
            var code = await auth.StartAsync();
            await Throws<OperationCanceledException>(async () => await auth.PollAsync(code, cancellation.Token));
            Equal(handler.Requests.Count, 2);
        });
        yield return ("PAT is passed only per method and empty GitHub display name is allowed", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal(request.RequestUri!.AbsoluteUri, "https://api.github.com/user");
                Equal(request.Headers.Authorization!.Parameter, Token);
                return Json(new { login = "contributor", name = "" });
            });
            using var publisher = new GitHubMarketplacePublisher(handler);
            Equal((await publisher.GetAuthenticatedUserAsync(Token)).Name, "");
            await Throws<MarketplaceException>(async () => await publisher.GetAuthenticatedUserAsync("bad\r\ntoken"));
            Equal(handler.Requests.Count, 1);
        });
        foreach (var mode in new[] { "existing-fork", "new-fork", "owner" })
        {
            var scenario = mode;
            yield return ("pending PR create-only protocol " + scenario, async () =>
            {
                var server = new PublishingServer(scenario);
                using var publisher = new GitHubMarketplacePublisher(server.Handler, new FastClock());
                var manifest = Sample(scenario == "owner" ? "Manhattanca" : "contributor") with { ReleaseNotes = "测试更新" };
                var submission = await publisher.SubmitAsync(manifest, Token);
                Equal(submission.PullRequestNumber, 17);
                Equal(submission.Sha256, MarketplaceJson.ComputeSha256(MarketplaceJson.SerializeManifest(manifest)));
                Equal(submission.ManifestPath, MarketplaceJson.GetManifestPath(manifest.Author, manifest.Id, manifest.Version));
                True(server.UploadedBytes!.SequenceEqual(MarketplaceJson.SerializeManifest(manifest)));
                Equal(server.Writes.Count, scenario == "new-fork" ? 4 : 3);
                True(server.Writes.All(r => !r.Contains("index.json", StringComparison.Ordinal) && !r.Contains("/merge", StringComparison.Ordinal) && !r.Contains("/reviews", StringComparison.Ordinal)));
                if (scenario == "owner") True(server.Handler.Requests.All(r => !r.Contains("/forks", StringComparison.Ordinal) && !r.Contains("contributor", StringComparison.Ordinal)));
                var first = submission.Branch;
                var second = await publisher.SubmitAsync(manifest, Token);
                True(first != second.Branch);
            });
        }
        foreach (var mode in new[] { "wrong-author", "published", "unrelated-fork", "failed-content", "foreign-pr", "merged-pr" })
        {
            var scenario = mode;
            yield return ("publication refuses unsafe state " + scenario, async () =>
            {
                var server = new PublishingServer(scenario);
                using var publisher = new GitHubMarketplacePublisher(server.Handler, new FastClock());
                var manifest = Sample(scenario == "wrong-author" ? "someone-else" : "contributor");
                await Throws<MarketplaceException>(async () => await publisher.SubmitAsync(manifest, Token));
                if (scenario is "wrong-author" or "published" or "unrelated-fork") Equal(server.Writes.Count, 0);
                if (scenario == "failed-content") True(!server.Writes.Any(r => r.EndsWith("/pulls", StringComparison.Ordinal)));
            });
        }
        foreach (var mode in new[] { "existing-fork", "new-fork", "owner", "wrong-author", "truncated", "missing", "bad-path", "symlink", "unrelated-fork", "foreign-pr", "merged-pr", "wrong-parent" })
        {
            var scenario = mode;
            yield return ("reviewed order removal protocol " + scenario, async () =>
            {
                var server = new DeletionServer(scenario);
                using var publisher = new GitHubMarketplacePublisher(server.Handler, new FastClock());
                var entry = MarketplaceJson.CreateCatalogue([Sample(scenario == "owner" ? "Manhattanca" : "contributor")]).Entries.Single();
                if (scenario is "existing-fork" or "new-fork" or "owner")
                {
                    var removal = await publisher.RequestDeletionAsync(entry, Token);
                    Equal(removal.PullRequestNumber, 18);
                    Equal(server.Removed.Count, 2);
                    True(server.Removed.All(path => path.StartsWith("marketplace/manifests/" + entry.Author.ToLowerInvariant() + "/synthetic-order/", StringComparison.Ordinal)));
                    True(server.Writes.All(path => !path.Contains("index.json") && !path.Contains("/merge") && !path.Contains("/reviews")));
                    True(server.Writes.All(path => !path.Contains("/git/refs/heads/main")));
                }
                else
                {
                    await Throws<MarketplaceException>(async () => await publisher.RequestDeletionAsync(entry, Token));
                    if (scenario is "wrong-author" or "truncated" or "missing" or "bad-path" or "symlink" or "unrelated-fork") Equal(server.Writes.Count, 0);
                    if (scenario == "wrong-parent") True(!server.Writes.Any(path => path.EndsWith("/pulls")));
                }
            });
        }
        yield return ("cancelled removal makes no request", async () =>
        {
            var server = new DeletionServer("existing-fork");
            using var publisher = new GitHubMarketplacePublisher(server.Handler);
            using var source = new CancellationTokenSource();
            source.Cancel();
            await Throws<OperationCanceledException>(async () => await publisher.RequestDeletionAsync(Entry(), Token, source.Token));
            Equal(server.Handler.Requests.Count, 0);
        });
        foreach (var version in new[] { "1.0.0", "1.0.1" })
        {
            var selectedVersion = version;
            yield return ("selected version removal retains other manifests and images: " + version, async () =>
            {
                var server = new DeletionServer("version-images");
                using var publisher = new GitHubMarketplacePublisher(server.Handler, new FastClock());
                var entry = MarketplaceJson.CreateCatalogue([Sample() with { Version = selectedVersion }]).Entries.Single();
                await publisher.RequestDeletionAsync(entry, MarketplaceDeletionScope.SelectedVersion, Token);
                Equal(server.Removed.Count, 2);
                True(server.Removed.Contains(entry.ManifestPath));
                True(server.Removed.Contains($"marketplace/images/contributor/synthetic-order/{selectedVersion}/0.png"));
                True(!server.Removed.Any(path=>path.Contains(selectedVersion == "1.0.0" ? "1.0.1" : "1.0.0")));
            });
        }
        yield return ("cancelled publication creates no PR", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var server = new PublishingServer("existing-fork") { OnUpload = () => cancellation.Cancel() };
            using var publisher = new GitHubMarketplacePublisher(server.Handler);
            await Throws<OperationCanceledException>(async () => await publisher.SubmitAsync(Sample(), Token, cancellation.Token));
            True(!server.Writes.Any(r => r.EndsWith("/pulls", StringComparison.Ordinal)));
        });
    }

    private static IEnumerable<(string Name, Func<MarketplaceManifest, MarketplaceManifest> Change)> InvalidManifests()
    {
        yield return ("schemaVersion must be one", m => m with { SchemaVersion = 2 });
        yield return ("author is GitHub login not display name", m => m with { Author = "Display Name" });
        yield return ("id traversal rejected", m => m with { Id = "../bad" });
        yield return ("version traversal rejected", m => m with { Version = "1.0.0/evil" });
        yield return ("empty title rejected", m => m with { Title = "" });
        yield return ("invalid game version rejected", m => m with { GameVersion = "future latest" });
        yield return ("game version suffix injection rejected", m => m with { GameVersion = "1.6 trailing junk" });
        yield return ("DLC must be official", m => m with { RequiredDlc = ["synthetic.dlc"] });
        yield return ("DLC duplicates case insensitive", m => m with { RequiredDlc = ["ludeon.rimworld.royalty", "Ludeon.RimWorld.Royalty"] });
        yield return ("required DLC matches ordered DLC", m => m with { RequiredDlc = [] });
        yield return ("required DLC cannot be missing from order", m => m with { RequiredDlc = ["ludeon.rimworld.royalty", "ludeon.rimworld.biotech"] });
        yield return ("collection numeric ASCII only", m => m with { WorkshopCollectionId = "１２３" });
        yield return ("collection unsigned64 overflow rejected", m => m with { WorkshopCollectionId = "99999999999999999999" });
        yield return ("workshop unsigned64 overflow rejected", m => m with { Mods = [new("synthetic.a", "18446744073709551616")], RequiredDlc = [] });
        yield return ("nonofficial null workshop rejected", m => m with { Mods = [new("synthetic.a", null)], RequiredDlc = [] });
        yield return ("official workshop assignment rejected", m => m with { Mods = [new("ludeon.rimworld", "123")], RequiredDlc = [] });
        yield return ("duplicate package case insensitive", m => m with { Mods = [new("synthetic.a", "123"), new("Synthetic.A", "456")], RequiredDlc = [] });
        yield return ("duplicate workshop rejected", m => m with { Mods = [new("synthetic.a", "123"), new("synthetic.b", "123")], RequiredDlc = [] });
        yield return ("empty mods rejected", m => m with { Mods = [] });
        yield return ("package URL rejected", m => m with { Mods = [new("https://evil.example", "123")], RequiredDlc = [] });
        yield return ("release notes bound", m => m with { ReleaseNotes = new string('x', 4001) });
        yield return ("release notes control rejected", m => m with { ReleaseNotes = "bad\0" });
        yield return ("release notes null rejected", m => m with { ReleaseNotes = null! });
    }

    private static MarketplaceEntry Entry() => MarketplaceJson.CreateCatalogue([Sample()]).Entries[0];
    private static string Fresh(string root) { var path = Path.Combine(root, "fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    internal static void True(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
    internal static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
    private static async Task Throws<T>(Action action) where T : Exception => await Throws<T>(() => { action(); return Task.CompletedTask; });
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    internal static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    internal static HttpResponseMessage Json(object value, bool codec = false) => Bytes(codec
        ? MarketplaceJson.SerializeCatalogue((MarketplaceCatalogue)value) : JsonSerializer.SerializeToUtf8Bytes(value));
    internal static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("No private server body should be exposed.") };
    private static Dictionary<string, string> ParseForm(string text) => text.Split('&').Select(part => part.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);
    private static FakeHandler Offline() => new(_ => throw new HttpRequestException("synthetic offline"));
    private static HttpResponseMessage Device(int expires = 300) => Json(new
    {
        device_code = "synthetic-device", user_code = "SYNTHETIC", verification_uri = "https://github.com/login/device", expires_in = expires, interval = 1
    });

    private sealed class DeletionServer
    {
        private readonly string mode;
        private string branch = "";
        private bool forkRequested;
        private static readonly string Tree = new('b', 40), RemovedTree = new('c', 40), RemovedCommit = new('d', 40);
        internal FakeHandler Handler { get; }
        internal List<string> Writes { get; } = [];
        internal List<string> Removed { get; } = [];
        internal DeletionServer(string mode) { this.mode = mode; Handler = new FakeHandler(Handle); }
        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            Equal(request.RequestUri!.Host, "api.github.com");
            Equal(request.Headers.Authorization!.Parameter, Token);
            var owner = mode == "owner" ? "Manhattanca" : "contributor";
            var upstream = "/repos/Manhattanca/RimWise-Marketplace";
            var fork = "/repos/" + owner + "/RimWise-Marketplace";
            var route = request.RequestUri.AbsolutePath;
            if (request.Method != HttpMethod.Get) Writes.Add(request.Method + " " + route);
            if (route == "/user") return Json(new { login = mode == "wrong-author" ? "someone-else" : owner, name = (string?)null });
            if (route == upstream + "/git/ref/heads/main") return Json(new { @object = new { type = "commit", sha = Commit } });
            if (request.Method == HttpMethod.Get && route == upstream + "/git/commits/" + Commit)
                return Json(new { sha = Commit, tree = new { sha = Tree } });
            if (request.Method == HttpMethod.Get && route == upstream + "/git/trees/" + Tree)
            {
                Equal(request.RequestUri.Query, "?recursive=1");
                var prefix = "marketplace/manifests/" + owner.ToLowerInvariant() + "/synthetic-order/";
                var files = new List<object> { new { path = "README.md", type = "blob", mode = "100644", sha = Commit },
                    new { path = "marketplace/manifests/another/other-order/1.0.0.json", type = "blob", mode = "100644", sha = Commit } };
                if (mode != "missing") foreach (var version in new[] { "1.0.0", "1.0.1" })
                    files.Add(new { path = prefix + (mode == "bad-path" ? "nested/" : "") + version + ".json", type = "blob", mode = mode == "symlink" ? "120000" : "100644", sha = Commit });
                if (mode == "version-images") foreach (var version in new[] { "1.0.0", "1.0.1" })
                    files.Add(new {path=$"marketplace/images/{owner}/synthetic-order/{version}/0.png",type="blob",mode="100644",sha=Commit});
                return Json(new { sha = Tree, truncated = mode == "truncated", tree = files });
            }
            if (route == upstream + "/forks")
            {
                Equal(mode, "new-fork"); forkRequested = true;
                return Json(new { fork = true, @private = false, full_name = owner + "/RimWise-Marketplace", owner = new { login = owner } });
            }
            if (route == fork && request.Method == HttpMethod.Get)
            {
                if (mode == "new-fork" && !forkRequested) return Status(HttpStatusCode.NotFound);
                return Json(new { fork = true, @private = false, full_name = owner + "/RimWise-Marketplace", owner = new { login = owner },
                    parent = new { full_name = mode == "unrelated-fork" ? "other/RimWise-Marketplace" : "Manhattanca/RimWise-Marketplace" } });
            }
            using var body = JsonDocument.Parse(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}");
            var root = body.RootElement;
            if (request.Method == HttpMethod.Post && route == fork + "/git/trees")
            {
                Equal(root.GetProperty("base_tree").GetString(), Tree);
                foreach (var item in root.GetProperty("tree").EnumerateArray())
                {
                    True(item.GetProperty("sha").ValueKind == JsonValueKind.Null);
                    Equal(item.GetProperty("type").GetString(), "blob");
                    Equal(item.GetProperty("mode").GetString(), "100644");
                    Removed.Add(item.GetProperty("path").GetString()!);
                }
                return Json(new { sha = RemovedTree });
            }
            if (request.Method == HttpMethod.Post && route == fork + "/git/commits")
            {
                Equal(root.GetProperty("tree").GetString(), RemovedTree);
                Equal(root.GetProperty("parents")[0].GetString(), Commit);
                return Json(new { sha = RemovedCommit, tree = new { sha = RemovedTree }, parents = new[] { new { sha = mode == "wrong-parent" ? RemovedTree : Commit } } });
            }
            if (request.Method == HttpMethod.Post && route == fork + "/git/refs")
            {
                Equal(root.GetProperty("sha").GetString(), RemovedCommit);
                branch = root.GetProperty("ref").GetString()!["refs/heads/".Length..];
                True(branch.StartsWith("marketplace/remove/", StringComparison.Ordinal));
                return Json(new { @ref = "refs/heads/" + branch, @object = new { sha = RemovedCommit } });
            }
            if (route == upstream + "/pulls")
            {
                Equal(root.GetProperty("base").GetString(), "main");
                Equal(root.GetProperty("head").GetString(), mode == "owner" ? branch : owner + ":" + branch);
                return Json(new { number = 18, html_url = mode == "foreign-pr" ? "https://github.com/other/RimWise-Marketplace/pull/18" : "https://github.com/Manhattanca/RimWise-Marketplace/pull/18", state = "open",
                    merged_at = mode == "merged-pr" ? "2026-11-16T00:00:00Z" : null,
                    @base = new { @ref = "main", repo = new { full_name = "Manhattanca/RimWise-Marketplace" } },
                    head = new { @ref = branch, repo = new { full_name = owner + "/RimWise-Marketplace" } } });
            }
            throw new InvalidOperationException("Unexpected removal route: " + request.Method + " " + route);
        }
    }

    private sealed class PublishingServer
    {
        private readonly string mode;
        private string branch = "";
        private string path = "";
        private bool forkRequested;
        internal FakeHandler Handler { get; }
        internal List<string> Writes { get; } = [];
        internal byte[]? UploadedBytes { get; private set; }
        internal Action? OnUpload { get; init; }
        internal PublishingServer(string mode) { this.mode = mode; Handler = new FakeHandler(Handle); }
        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            Equal(request.RequestUri!.Host, "api.github.com");
            Equal(request.Headers.Authorization!.Parameter, Token);
            var owner = mode == "owner" ? "Manhattanca" : "contributor";
            var route = request.RequestUri.AbsolutePath;
            if (request.Method != HttpMethod.Get) Writes.Add(request.Method + " " + route);
            if (route == "/user") return Json(new { login = owner, name = (string?)null });
            if (route.StartsWith("/repos/Manhattanca/RimWise-Marketplace/contents/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return mode == "published" ? Json(new { path = "already-published" }) : Status(HttpStatusCode.NotFound);
            if (route == "/repos/Manhattanca/RimWise-Marketplace/git/ref/heads/main") return Json(new { @object = new { type = "commit", sha = Commit } });
            if (route == "/repos/Manhattanca/RimWise-Marketplace/forks")
            {
                Equal(mode, "new-fork");
                forkRequested = true;
                return Json(new { fork = true, @private = false, full_name = owner + "/RimWise-Marketplace", owner = new { login = owner } });
            }
            if (route == "/repos/contributor/RimWise-Marketplace")
            {
                if (mode == "new-fork" && !forkRequested) return Status(HttpStatusCode.NotFound);
                return Json(new { fork = true, @private = false, full_name = owner + "/RimWise-Marketplace", owner = new { login = owner },
                    parent = new { full_name = mode == "unrelated-fork" ? "other/RimWise-Marketplace" : "Manhattanca/RimWise-Marketplace" } });
            }
            var bodyText = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
            using var body = JsonDocument.Parse(bodyText);
            if (route == $"/repos/{owner}/RimWise-Marketplace/git/refs")
            {
                Equal(request.Method, HttpMethod.Post);
                Equal(body.RootElement.GetProperty("sha").GetString(), Commit);
                branch = body.RootElement.GetProperty("ref").GetString()!["refs/heads/".Length..];
                return Json(new { @ref = "refs/heads/" + branch, @object = new { sha = Commit } });
            }
            if (route.StartsWith($"/repos/{owner}/RimWise-Marketplace/contents/", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                path = route[$"/repos/{owner}/RimWise-Marketplace/contents/".Length..];
                True(!body.RootElement.TryGetProperty("sha", out _));
                Equal(body.RootElement.GetProperty("branch").GetString(), branch);
                UploadedBytes = Convert.FromBase64String(body.RootElement.GetProperty("content").GetString()!);
                Equal(MarketplaceJson.ParseManifest(UploadedBytes).Author, owner);
                OnUpload?.Invoke();
                return mode == "failed-content" ? Status(HttpStatusCode.Conflict) : Json(new { content = new { path } });
            }
            if (route == "/repos/Manhattanca/RimWise-Marketplace/pulls")
            {
                Equal(body.RootElement.GetProperty("base").GetString(), "main");
                Equal(body.RootElement.GetProperty("head").GetString(), mode == "owner" ? branch : owner + ":" + branch);
                return Json(new { number = 17, html_url = mode == "foreign-pr" ? "https://github.com/other/RimWise-Marketplace/pull/17" : "https://github.com/Manhattanca/RimWise-Marketplace/pull/17",
                    state = "open", merged_at = mode == "merged-pr" ? "2026-11-13T00:00:00Z" : null,
                    @base = new { @ref = "main", repo = new { full_name = "Manhattanca/RimWise-Marketplace" } },
                    head = new { @ref = branch, repo = new { full_name = owner + "/RimWise-Marketplace" } } });
            }
            throw new InvalidOperationException("Unexpected fake GitHub route: " + request.Method + " " + route);
        }
    }
}

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
    internal List<string> Requests { get; } = [];
    internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : this((request, _) => Task.FromResult(send(request))) { }
    internal FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.Method + " " + request.RequestUri!.AbsoluteUri);
        var response = await send(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
}

internal sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
}

/// <summary>Advances virtual time when a delay is scheduled; no test waits on OAuth's real polling interval.</summary>
internal sealed class FastClock : TimeProvider
{
    private DateTimeOffset current = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    internal List<double> Delays { get; } = [];
    public override DateTimeOffset GetUtcNow() { lock (Delays) return current; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (Delays) { current += dueTime; Delays.Add(dueTime.TotalSeconds); }
        var timer = new FakeTimer();
        ThreadPool.QueueUserWorkItem(_ => { if (!timer.Disposed) callback(state); });
        return timer;
    }
    private sealed class FakeTimer : ITimer
    {
        internal volatile bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
