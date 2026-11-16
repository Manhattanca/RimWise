namespace RimWise.Integration.Marketplace;

/// <summary>Explicit, anonymous reads from the official repository. Search stays with the caller.</summary>
public sealed class MarketplaceClient : IDisposable
{
    public const string CatalogueUrl = MarketplaceRepository.CatalogueUrl;
    private readonly MarketplaceHttp http;
    private readonly MarketplaceCache? cache;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MarketplaceCatalogue? lastCatalogue;

    public MarketplaceClient(string? cacheDirectory = null, HttpMessageHandler? handler = null)
    {
        http = new MarketplaceHttp(handler);
        cache = cacheDirectory is null ? null : new MarketplaceCache(cacheDirectory);
    }

    public async Task<MarketplaceResult<MarketplaceCatalogue>> GetCatalogueAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MarketplaceCatalogue? previous = lastCatalogue;
            if (previous is null && cache is not null)
            {
                try
                {
                    var saved = await cache.ReadAsync(MarketplaceCache.CatalogueFile, MarketplaceJson.MaximumCatalogueBytes,
                        cancellationToken).ConfigureAwait(false);
                    if (saved is not null) previous = MarketplaceJson.ParseCatalogue(saved);
                }
                catch (Exception exception) when (CacheFailure(exception)) { }
            }
            try
            {
                // A successful raw response can still contain the CDN's previous index.
                // Only explicit catalogue reads get a fresh cache key; immutable manifests
                // retain their stable URLs, and failed reads still use the validated cache.
                var url = CatalogueUrl + "?rimwise-refresh=" + Guid.NewGuid().ToString("N");
                var response = await http.SendAsync(HttpMethod.Get, url, null, null,
                    MarketplaceJson.MaximumCatalogueBytes, cancellationToken).ConfigureAwait(false);
                var catalogue = MarketplaceJson.ParseCatalogue(response.Bytes);
                if (previous is not null) CheckImmutable(previous, catalogue);
                cancellationToken.ThrowIfCancellationRequested();
                var warning = await SaveAsync(MarketplaceCache.CatalogueFile, response.Bytes, cancellationToken).ConfigureAwait(false);
                lastCatalogue = catalogue;
                return new(catalogue, false, warning);
            }
            catch (Exception exception) when (ReadFailure(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous is null) throw new MarketplaceException(SafeMessage(exception) + " 本地没有可用的目录缓存。");
                lastCatalogue = previous;
                return new(previous, true, SafeMessage(exception) + " 当前显示上次有效的目录缓存。");
            }
        }
        finally { gate.Release(); }
    }

    public async Task<MarketplaceResult<MarketplaceManifest>> GetManifestAsync(MarketplaceEntry entry,
        CancellationToken cancellationToken = default, IProgress<MarketplaceTransferProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Snapshot caller-owned lists and validate before making any request or constructing a local path.
        entry = entry with { RequiredDlc = entry.RequiredDlc is null ? null! : Array.AsReadOnly(entry.RequiredDlc.ToArray()) };
        MarketplaceJson.ValidateEntry(entry);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lastCatalogue is not null)
            {
                var known = lastCatalogue.Entries.SingleOrDefault(e => MarketplaceJson.Identity(e) == MarketplaceJson.Identity(entry));
                MarketplaceJson.Require(known is not null && SameEntry(known, entry),
                    "The requested manifest is not pinned to the current catalogue; refresh the catalogue.");
            }
            var file = MarketplaceCache.ManifestFile(entry);
            byte[]? cached = null;
            if (cache is not null)
            {
                try
                {
                    cached = await cache.ReadAsync(file, MarketplaceJson.MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
                    if (cached is not null) MarketplaceJson.VerifyManifest(entry, cached);
                }
                catch (Exception exception) when (CacheFailure(exception)) { cached = null; }
            }
            try
            {
                var response = await http.SendAsync(HttpMethod.Get, MarketplaceHttp.RawRoot + entry.ManifestPath, null,
                    null, MarketplaceJson.MaximumManifestBytes, cancellationToken, progress: progress).ConfigureAwait(false);
                MarketplaceJson.VerifyManifest(entry, response.Bytes);
                cancellationToken.ThrowIfCancellationRequested();
                var warning = await SaveAsync(file, response.Bytes, cancellationToken).ConfigureAwait(false);
                return new(MarketplaceJson.ParseManifest(response.Bytes), false, warning);
            }
            catch (Exception exception) when (ReadFailure(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cached is null) throw new MarketplaceException(SafeMessage(exception) + " 本地没有与目录匹配且通过校验的排序缓存。");
                return new(MarketplaceJson.ParseManifest(cached), true,
                    SafeMessage(exception) + " 使用已通过目录 SHA-256 校验的排序缓存。");
            }
        }
        finally { gate.Release(); }
    }

    public async Task<byte[]> GetImageAsync(MarketplaceImage image, CancellationToken cancellationToken = default)
    {
        // Validate before constructing any URL; public images never receive account credentials.
        if (image.Path is null || !System.Text.RegularExpressions.Regex.IsMatch(image.Path,
            @"^marketplace/images/[a-z0-9-]+/[a-z0-9-]+/[a-z0-9.-]+/[123]-[a-f0-9]{64}\.(png|jpg)$"))
            throw new MarketplaceException("市场图片位置无效。");
        var file = "rw-marketplace-image-" + MarketplaceJson.ComputeSha256(System.Text.Encoding.UTF8.GetBytes(image.Path)) + ".bin";
        if (cache is not null)
        {
            try { var saved = await cache.ReadAsync(file, MarketplaceImages.MaximumBytes, cancellationToken).ConfigureAwait(false);
                if (saved is not null) { MarketplaceImages.Verify(image, saved); return saved; } }
            catch (Exception ex) when (CacheFailure(ex)) { }
        }
        var response = await http.SendAsync(HttpMethod.Get, MarketplaceHttp.RawRoot + image.Path, null, null,
            MarketplaceImages.MaximumBytes, cancellationToken).ConfigureAwait(false);
        MarketplaceImages.Verify(image, response.Bytes);
        _ = await SaveAsync(file, response.Bytes, cancellationToken).ConfigureAwait(false);
        return response.Bytes;
    }

    private async Task<string?> SaveAsync(string name, byte[] bytes, CancellationToken cancellationToken)
    {
        if (cache is null) return null;
        try { await cache.WriteAsync(name, bytes, cancellationToken).ConfigureAwait(false); return null; }
        catch (Exception exception) when (CacheFailure(exception))
        { return "市场数据已通过校验，但无法保存本地缓存。"; }
    }

    private static void CheckImmutable(MarketplaceCatalogue old, MarketplaceCatalogue next)
    {
        var prior = old.Entries.ToDictionary(MarketplaceJson.Identity, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in next.Entries)
            if (prior.TryGetValue(MarketplaceJson.Identity(entry), out var previous))
                MarketplaceJson.Require(SameEntry(previous, entry), "An existing author/id/version changed; immutable marketplace data was refused.");
    }

    private static bool SameEntry(MarketplaceEntry a, MarketplaceEntry b) =>
        a.Id == b.Id && a.Version == b.Version && a.Author == b.Author && a.Title == b.Title &&
        a.Description == b.Description && a.GameVersion == b.GameVersion && a.WorkshopCollectionId == b.WorkshopCollectionId &&
        a.ManifestPath == b.ManifestPath && a.Sha256 == b.Sha256 && a.ModCount == b.ModCount &&
        a.Images.SequenceEqual(b.Images) && a.ReleaseNotes == b.ReleaseNotes && a.RequiredDlc.SequenceEqual(b.RequiredDlc);

    private static bool CacheFailure(Exception e) => e is MarketplaceException or IOException or UnauthorizedAccessException;
    private static bool ReadFailure(Exception e) => e is MarketplaceException or IOException;
    private static string SafeMessage(Exception e) => e is MarketplaceException ? e.Message : "无法读取市场数据。";

    public void Dispose() { http.Dispose(); gate.Dispose(); }
}
