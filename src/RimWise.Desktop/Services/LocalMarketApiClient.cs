using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RimWise.Formats;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop.Services;

public sealed class LocalMarketApiClient : IDisposable
{
    private readonly Uri origin;
    private readonly HttpClient http;
    private readonly MarketSessionHandler? session;
    public string? SessionWarning => session?.StorageWarning;
    private string? csrf;
    private GitHubMarketplaceUser? user;

    public GitHubMarketplaceUser? User => user;
    internal long? ActiveSession => session?.ActiveGeneration;
    internal sealed record Notice(Guid Id, string Title, string Version, string Reason);
    internal async Task<Notice[]> ReadNoticesAsync(CancellationToken token)
    {
        using var response = await http.GetAsync("api/notifications", token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized) return [];
        await EnsureSuccess(response, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        return json.RootElement.EnumerateArray().Take(20).Select(e => new Notice(Guid.ParseExact(RequiredString(e,"id"), "N"),
            RequiredString(e,"title"), RequiredString(e,"version"), RequiredString(e,"reason"))).ToArray();
    }
    internal Task AckNoticeAsync(Guid id, CancellationToken token) => MutateAsync(HttpMethod.Post, $"api/notifications/{id:N}/ack", "{}", token);

    public LocalMarketApiClient(Uri? configuredOrigin = null) : this(configuredOrigin, null) { }

    internal LocalMarketApiClient(Uri? configuredOrigin, HttpMessageHandler? transport)
    {
        origin = ValidateOrigin(configuredOrigin ?? ReadConfiguredOrigin());
        http = new HttpClient(transport ?? (session = new MarketSessionHandler(origin, "desktop")))
        { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(45) };
    }

    public async Task<GitHubMarketplaceUser?> RefreshUserAsync(CancellationToken token)
    {
        using var response = await http.GetAsync("api/me", token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        if (json.RootElement.ValueKind == JsonValueKind.Null) { session?.Clear(); csrf = null; return user = null; }
        user = new(RequiredString(json.RootElement, "username"), OptionalString(json.RootElement, "nickname"));
        return user;
    }

    public async Task<(GitHubMarketplaceUser User, string? RecoveryCode)> LoginAsync(string username, string password,
        string? nickname, bool register, CancellationToken token)
    {
        var body = JsonSerializer.Serialize(new { username, password, nickname });
        using var response = await SendMutationAsync(HttpMethod.Post, register ? "api/register" : "api/login", body, token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
        string? recovery = null;
        if (register)
        {
            using var result = await ReadJsonAsync(response, token).ConfigureAwait(false);
            recovery = OptionalString(result.RootElement, "recoveryCode");
        }
        var me = await RefreshUserAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("登录后未能读取账号状态。");
        return (me, recovery);
    }

    public async Task LogoutAsync(CancellationToken token)
    {
        try { await MutateAsync(HttpMethod.Post, "api/logout", "{}", token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or MarketplaceException)
        { throw new MarketplaceException("已退出本机登录，但未能确认服务器会话撤销；请联网后核对。"); }
        finally { session?.Clear(); user = null; csrf = null; }
    }

    public async Task<string> RecoverAsync(string username, string recoveryCode, string nextPassword, CancellationToken token)
    {
        using var response = await SendMutationAsync(HttpMethod.Post, "api/recover",
            JsonSerializer.Serialize(new { username, code = recoveryCode, password = nextPassword }), token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        return RequiredString(json.RootElement, "recoveryCode");
    }

    public async Task ChangeNicknameAsync(string nickname, CancellationToken token)
    {
        await MutateAsync(HttpMethod.Put, "api/nickname", JsonSerializer.Serialize(new { nickname }), token).ConfigureAwait(false);
        await RefreshUserAsync(token).ConfigureAwait(false);
    }

    public Task ChangePasswordAsync(string current, string next, CancellationToken token) =>
        MutateAsync(HttpMethod.Put, "api/password", JsonSerializer.Serialize(new { currentPassword = current, newPassword = next }), token);

    public async Task<MarketplaceResult<MarketplaceCatalogue>> GetCatalogueAsync(CancellationToken token)
    {
        using var response = await http.GetAsync("api/catalog", token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        var entries = json.RootElement.GetProperty("entries").EnumerateArray()
            .SelectMany(listing => listing.GetProperty("versions").EnumerateArray()
                .Select(row => ParseEntry(row, RequiredString(listing, "id")))).ToArray();
        return new(new(1, entries), false, null);
    }

    public async Task<MarketplaceResult<MarketplaceManifest>> GetManifestAsync(MarketplaceEntry entry, CancellationToken token)
    {
        if (!Guid.TryParseExact(entry.ManifestPath, "N", out _)) throw new MarketplaceException("排序版本标识无效。");
        using var response = await http.GetAsync($"api/versions/{entry.ManifestPath}/sort", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
        var bytes = await ReadBytesAsync(response, OrderFileCodec.MaximumBytes, token).ConfigureAwait(false);
        if (MarketplaceJson.ComputeSha256(bytes) != entry.Sha256) throw new MarketplaceException("市场排序文件完整性校验失败。");
        using var stream = new MemoryStream(bytes, false);
        var order = new OrderFileCodec().Read(stream);
        var references = order.WorkshopReferences.ToDictionary(item => item.Identifier, item => item.WorkshopId, StringComparer.OrdinalIgnoreCase);
        var mods = order.Configuration.Order.PackageIds.Select(id => new MarketplaceMod(id,
            references.TryGetValue(id, out var hint) ? RimWise.Integration.Steam.ImportedOrderDownloads.WorkshopIdFromInput(hint) : null)).ToArray();
        var manifest = new MarketplaceManifest(1, entry.Id, entry.Version, entry.Title, entry.Author,
            entry.Description, entry.GameVersion, entry.RequiredDlc, entry.WorkshopCollectionId, mods)
        { ReleaseNotes = entry.ReleaseNotes };
        return new(manifest, false, null);
    }

    public async Task<byte[]> GetImageAsync(MarketplaceImage image, CancellationToken token)
    {
        if (!image.Path.StartsWith("localmarket/", StringComparison.Ordinal) ||
            !Guid.TryParseExact(image.Path[12..], "N", out _)) throw new MarketplaceException("市场图片标识无效。");
        using var response = await http.GetAsync("api/images/" + image.Path[12..], HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
        var bytes = await ReadBytesAsync(response, MarketplaceImages.MaximumBytes, token).ConfigureAwait(false);
        if (bytes.LongLength != image.Bytes || MarketplaceJson.ComputeSha256(bytes) != image.Sha256)
            throw new MarketplaceException("市场图片完整性校验失败。");
        return bytes;
    }

    public Task<byte[]> LoadImageAsync(MarketplaceImage image, CancellationToken token) => GetImageAsync(image, token);

    public async Task<MarketplaceSubmission> SubmitAsync(MarketplaceManifest manifest,
        IReadOnlyList<MarketplaceImageUpload> images, IProgress<string> progress, CancellationToken token, bool update)
    {
        var sort = SerializeSort(manifest);
        var revisionId = manifest.RemoteRevisionId;
        MarketplaceEntry? existing = null;
        if (revisionId is not null)
        {
            if (!Guid.TryParseExact(revisionId, "N", out _)) throw new MarketplaceException("服务器草稿标识无效。");
            existing = (await GetMineAsync(token).ConfigureAwait(false)).SingleOrDefault(entry => entry.ManifestPath == revisionId)
                ?? throw new MarketplaceException("找不到属于你的退回投稿或草稿，请刷新后重试。");
            if (existing.Version != manifest.Version || existing.PublicationState is not ("draft" or "rejected"))
                throw new MarketplaceException("投稿状态或版本已变化，请刷新后重试；待审核内容不能修改。");
            if (existing.PublicationState == "rejected")
                await MutateAsync(HttpMethod.Post, $"api/drafts/{revisionId}/reopen", "{}", token).ConfigureAwait(false);
        }
        var listingId = revisionId is null && update ? ListingGuid(manifest.Id) : null;
        var input = JsonSerializer.Serialize(new
        {
            listingId, version = manifest.Version, title = manifest.Title, description = manifest.Description,
            gameVersions = manifest.GameVersion, dlc = string.Join(", ", manifest.RequiredDlc),
            workshop = manifest.WorkshopCollectionId, releaseNotes = manifest.ReleaseNotes, sort
        });
        progress.Report("正在保存排序草稿…");
        using var created = await SendMutationAsync(revisionId is null ? HttpMethod.Post : HttpMethod.Put,
            revisionId is null ? "api/drafts" : $"api/drafts/{revisionId}", input, token).ConfigureAwait(false);
        await EnsureSuccess(created, token).ConfigureAwait(false);
        using var idJson = await ReadJsonAsync(created, token).ConfigureAwait(false);
        revisionId = RequiredString(idJson.RootElement, "id");
        if (!Guid.TryParseExact(revisionId, "N", out _)) throw new MarketplaceException("服务器返回的草稿标识无效。");
        // Replace an obsolete slot atomically when the server's three-image capacity is full.
        // Failed validation/upload leaves the old image intact.
        var unused = existing?.Images.ToList() ?? [];
        var storedCount = unused.Count;
        bool retainedPrefix = true;
        for (var i = 0; i < images.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var same = retainedPrefix ? unused.FirstOrDefault(image => image.Sha256 == images[i].Image.Sha256 && image.Bytes == images[i].Content.Length) : null;
            if (same is not null)
            {
                // Images are served in insertion order. Retain only an ordered prefix;
                // once a new picture is appended, later pictures must follow it.
                var position = unused.IndexOf(same);
                unused.RemoveAt(position);
                retainedPrefix = position == 0;
                continue;
            }
            retainedPrefix = false;
            progress.Report($"正在上传图片 {i + 1}/{images.Count}…");
            var replacement = storedCount >= MarketplaceImages.MaximumCount ? unused.FirstOrDefault() : null;
            var replaceQuery = replacement is null ? "" : "?replace=" + replacement.Path[12..];
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/drafts/{revisionId}/images" + replaceQuery)
            { Content = new ByteArrayContent(images[i].Content) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(images[i].Image.Path.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
            using var uploaded = await SendMutationAsync(request, token).ConfigureAwait(false);
            await EnsureSuccess(uploaded, token).ConfigureAwait(false);
            if (replacement is not null) unused.Remove(replacement);
            else storedCount++;
        }
        foreach (var image in unused)
            await MutateAsync(HttpMethod.Delete, $"api/drafts/{revisionId}/images/{image.Path[12..]}", "{}", token).ConfigureAwait(false);
        progress.Report("正在提交审核…");
        using var submitted = await SendMutationAsync(HttpMethod.Post, $"api/drafts/{revisionId}/submit", "{}", token).ConfigureAwait(false);
        await EnsureSuccess(submitted, token).ConfigureAwait(false);
        return new(0, new Uri(origin, ""), revisionId, revisionId, MarketplaceJson.ComputeSha256(Encoding.UTF8.GetBytes(sort)))
        { IsAccountMarket = true, Published = submitted.Headers.TryGetValues("X-RimWise-Publication", out var state) && state.Contains("published") };
    }

    public async Task<MarketplaceSubmission> SubmitAsync(MarketplaceManifest manifest,
        IProgress<string> progress, CancellationToken token, bool update) =>
        await SubmitAsync(manifest, [], progress, token, update).ConfigureAwait(false);

    public async Task WithdrawAsync(MarketplaceEntry entry, MarketplaceDeletionScope scope, CancellationToken token)
    {
        var (kind, id) = scope == MarketplaceDeletionScope.AllVersions
            ? ("listings", ListingGuid(entry.Id)) : ("versions", entry.ManifestPath);
        await MutateAsync(HttpMethod.Post, $"api/{kind}/{id}/withdraw", "{}", token).ConfigureAwait(false);
    }

    public async Task<MarketplaceSubmission> WithdrawOrderAsync(MarketplaceEntry entry, MarketplaceDeletionScope scope, CancellationToken token)
    {
        await WithdrawAsync(entry, scope, token).ConfigureAwait(false);
        return new(0, new Uri(origin, ""), "", entry.ManifestPath, entry.Sha256) { IsAccountMarket = true };
    }

    public async Task<IReadOnlyList<MarketplaceEntry>> GetMineAsync(CancellationToken token)
    {
        using var response = await http.GetAsync("api/mine", token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        return json.RootElement.EnumerateArray().Select(row => ParseEntry(row)).ToArray();
    }

    private async Task MutateAsync(HttpMethod method, string path, string body, CancellationToken token)
    {
        using var response = await SendMutationAsync(method, path, body, token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendMutationAsync(HttpMethod method, string path, string body, CancellationToken token)
    {
        await GetCsrfAsync(token).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return await SendMutationAsync(request, token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendMutationAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (csrf is null) await GetCsrfAsync(token).ConfigureAwait(false);
        request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-RimWise-CSRF", csrf);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private async Task GetCsrfAsync(CancellationToken token)
    {
        using var response = await http.GetAsync("api/csrf", token).ConfigureAwait(false);
        await EnsureSuccess(response, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        csrf = RequiredString(json.RootElement, "token");
    }

    private static MarketplaceEntry ParseEntry(JsonElement row, string? listingId = null)
    {
        var metadata = row.GetProperty("metadata");
        var displayName = OptionalString(row, "author") ?? "排序作者";
        var username = OptionalString(row, "ownerUsername") ?? displayName;
        var images = row.TryGetProperty("images", out var imageRows) ? imageRows.EnumerateArray().Select(image =>
        {
            var id = RequiredString(image, "id");
            return new MarketplaceImage("localmarket/" + id, RequiredString(image, "sha256"),
                checked((int)image.GetProperty("bytes").GetInt64()), 0, 0);
        }).ToArray() : [];
        var dlc = (OptionalString(metadata, "dlc") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // New pending submissions deliberately hide their public listing ID until approval.
        // Use the revision ID only as an internal grouping key; the UI keeps it hidden.
        var listing = listingId ?? OptionalString(row, "listingId") ?? RequiredString(row, "id");
        if (string.IsNullOrWhiteSpace(listing)) throw new MarketplaceException("市场响应缺少排序标识。");
        return new MarketplaceEntry("rw-" + listing, RequiredString(row, "version"),
            RequiredString(metadata, "title"), username, OptionalString(metadata, "description") ?? "",
            OptionalString(metadata, "gameVersions") ?? "", dlc, OptionalString(metadata, "workshopId"),
            RequiredString(row, "id"), RequiredString(row, "sha256"))
        {
            ModCount = metadata.GetProperty("modCount").GetInt32(),
            ReleaseNotes = OptionalString(metadata, "releaseNotes") ?? "",
            Images = images,
            AuthorDisplayName = displayName,
            IsAccountMarket = true,
            PublicationState = OptionalString(row, "state"),
            RejectionReason = OptionalString(row, "reason")
        };
    }

    private static string SerializeSort(MarketplaceManifest manifest)
    {
        if (manifest.Mods.Any(mod => mod.WorkshopId is not null))
        {
            var document = MarketplaceOrderCompatibility.ToOrderFile(manifest with { Images = [] });
            return Encoding.UTF8.GetString(new OrderFileCodec().Write(document, OrderFileFormat.RmlXml));
        }
        var version = manifest.GameVersion.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var root = new XElement("ModsConfigData", new XElement("version", version),
            new XElement("activeMods", manifest.Mods.Select(mod => new XElement("li", mod.PackageId))),
            new XElement("knownExpansions", manifest.RequiredDlc.Select(id => new XElement("li", id))));
        return new XDocument(root).ToString(SaveOptions.DisableFormatting);
    }

    private static string ListingGuid(string id)
    {
        if (!id.StartsWith("rw-", StringComparison.Ordinal) || !Guid.TryParseExact(id[3..], "N", out _))
            throw new MarketplaceException("排序标识无效。");
        return id[3..];
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token) =>
        JsonDocument.Parse(await ReadBytesAsync(response, OrderFileCodec.MaximumBytes, token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long length && (length < 0 || length > maximum))
            throw new MarketplaceException("市场响应超过可读取的大小上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > maximum) throw new MarketplaceException("市场响应超过可读取的大小上限。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        try { using var json = await ReadJsonAsync(response, token).ConfigureAwait(false); detail = OptionalString(json.RootElement, "error"); }
        catch (JsonException) { }
        throw new MarketplaceException(!string.IsNullOrEmpty(detail) ? detail : $"市场请求失败（HTTP {(int)response.StatusCode}）。");
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new MarketplaceException("市场响应字段无效。")
            : throw new MarketplaceException("市场响应缺少必要字段。");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri ReadConfiguredOrigin()
    {
        return MarketApiConnection.ReadOrigin();
    }

    private static Uri ValidateOrigin(Uri value)
    {
        if (!value.IsAbsoluteUri || value.UserInfo.Length != 0 || value.Query.Length != 0 || value.Fragment.Length != 0 || value.AbsolutePath != "/" ||
            value.Scheme != Uri.UriSchemeHttps && !(value.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(value.Host, out var address) && IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("排序市场地址必须是 HTTPS；仅允许对 SSH 隧道使用本机回环 HTTP 地址。");
        return value;
    }

    public void Dispose() => http.Dispose();
}
