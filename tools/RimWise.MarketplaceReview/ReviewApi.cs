using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Marketplace;

namespace RimWise.MarketplaceReview;

/// <summary>Authenticated review transport for the RimWise market API. Responses are bounded and never execute submitted data.</summary>
internal sealed class ReviewApi : IDisposable
{
    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private readonly Uri origin;
    private readonly HttpClient client;
    private readonly MarketSessionHandler? session;
    internal string? SessionWarning => session?.StorageWarning;
    private string? csrf;

    internal Uri Origin => origin;

    internal ReviewApi(HttpMessageHandler? handler = null, Uri? configuredOrigin = null)
    {
        origin = ValidateOrigin(configuredOrigin ?? ReadConfiguredOrigin());
        client = new HttpClient(handler ?? (session = new MarketSessionHandler(origin, "review")))
        { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(45) };
    }

    internal async Task<string> LoginAsync(string username, string password, CancellationToken token)
    {
        await GetCsrfAsync(token);
        using (var response = await MutateAsync(HttpMethod.Post, "api/login",
            JsonSerializer.Serialize(new { username, password }), token))
            await EnsureSuccessAsync(response, token);

        return await RestoreAsync(token) ?? throw new InvalidOperationException("登录后未能读取审核账号状态。");
    }

    internal async Task<string?> RestoreAsync(CancellationToken token)
    {
        using var identity = await GetJsonAsync("api/me", token);
        var root = identity.RootElement;
        if (root.ValueKind == JsonValueKind.Null) { session?.Clear(); csrf = null; return null; }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("admin", out var admin) || admin.ValueKind != JsonValueKind.True)
        {
            try { await LogoutAsync(token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException) { }
            throw new InvalidOperationException("该账号没有审核权限，请使用排序市场审核员账号登录。");
        }
        return OptionalString(root, "nickname") ?? "审核员";
    }

    internal async Task LogoutAsync(CancellationToken token)
    {
        try
        {
            using var response = await MutateAsync(HttpMethod.Post, "api/logout", "{}", token);
            await EnsureSuccessAsync(response, token);
        }
        finally { session?.Clear(); csrf = null; }
    }

    internal async Task<IReadOnlyList<MarketReviewItem>> ListAsync(CancellationToken token)
    {
        using var json = await GetJsonAsync("api/review", token);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 1000)
            throw new InvalidDataException("服务器返回的待审列表无效或过大。");
        var items = json.RootElement.EnumerateArray().Select(ParseItem).ToArray();
        if (items.Any(item => item.State != "pending") || items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new InvalidDataException("服务器待审列表包含重复或非待审核排序。");
        return items;
    }

    internal async Task<ReviewSnapshot> InspectAsync(MarketReviewItem requested, CancellationToken token)
    {
        var current = (await ListAsync(token)).FirstOrDefault(item => item.Id == requested.Id);
        if (current is null || current.ReviewToken != requested.ReviewToken)
            throw new InvalidOperationException("投稿已被处理或内容已变化，请刷新列表后重新查看。");
        if (current.Images.Count > MarketplaceImages.MaximumCount)
            return new(current, Summary(current), [], false, "图片数量超过审核助手支持范围，已禁用通过。");

        var images = new List<ReviewImage>(current.Images.Count);
        foreach (var reference in current.Images)
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(reference.Id, "N", out _) || reference.Bytes is <= 0 or > MarketplaceImages.MaximumBytes)
                return new(current, Summary(current), images, false, "图片大小或标识不符合市场限制，已禁用通过。");
            try
            {
                var bytes = await GetBytesAsync("api/images/" + reference.Id, MarketplaceImages.MaximumBytes, token);
                var extension = reference.Mime switch { "image/png" => "png", "image/jpeg" => "jpg", _ => "" };
                if (extension.Length == 0) return new(current, Summary(current), images, false, "投稿包含不支持的图片格式，已禁用通过。");
                var info = MarketplaceImages.Inspect(bytes);
                var image = new MarketplaceImage("localmarket/" + reference.Id + "." + extension,
                    reference.Sha256, checked((int)reference.Bytes), info.Width, info.Height);
                MarketplaceImages.Verify(image, bytes);
                images.Add(new(bytes));
            }
            catch (Exception ex) when (ex is MarketplaceException or InvalidDataException or IOException)
            { return new(current, Summary(current), images, false, "投稿图片无法完整读取或校验，已禁用通过；可填写原因退回作者。"); }
        }
        return new(current, Summary(current), images, true, "投稿内容已读取。请人工检查文字与图片后决定是否上架。");
    }

    internal async Task DecideAsync(ReviewSnapshot snapshot, bool approve, string reason, CancellationToken token)
    {
        if (approve && !snapshot.Supported) throw new InvalidOperationException("该投稿内容尚未通过审核助手的完整性检查，不能上架。");
        reason = reason.Trim();
        if (!approve && (reason.Length == 0 || reason.Length > 4000))
            throw new InvalidDataException("退回原因不能为空，且不能超过 4000 个字符。");
        var current = (await ListAsync(token)).FirstOrDefault(item => item.Id == snapshot.Item.Id);
        if (current is null || current.State != "pending" || current.ReviewToken != snapshot.Item.ReviewToken)
            throw new InvalidOperationException("投稿状态或内容已变化，请刷新并重新查看后再审核。");
        var body = JsonSerializer.Serialize(new { approve, reason = approve ? "" : reason, expectedReviewToken = snapshot.Item.ReviewToken });
        using var response = await MutateAsync(HttpMethod.Post, "api/review/" + snapshot.Item.Id, body, token);
        await EnsureSuccessAsync(response, token);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken token)
    {
        var bytes = await GetBytesAsync(path, MaximumJsonBytes, token);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private async Task<byte[]> GetBytesAsync(string path, int maximumBytes, CancellationToken token)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        await EnsureSuccessAsync(response, token);
        if (response.Content.Headers.ContentLength is long length && (length < 0 || length > maximumBytes))
            throw new InvalidDataException("服务器返回内容超过大小限制。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximumBytes) throw new InvalidDataException("服务器返回内容超过大小限制。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> MutateAsync(HttpMethod method, string path, string body, CancellationToken token)
    {
        if (csrf is null) await GetCsrfAsync(token);
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-RimWise-CSRF", csrf);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private async Task GetCsrfAsync(CancellationToken token)
    {
        using var json = await GetJsonAsync("api/csrf", token);
        csrf = RequiredString(json.RootElement, "token");
    }

    private static MarketReviewItem ParseItem(JsonElement row)
    {
        var id = RequiredString(row, "id");
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("投稿标识无效。");
        var metadata = row.GetProperty("metadata");
        var imagesElement = row.GetProperty("images");
        if (imagesElement.ValueKind != JsonValueKind.Array || imagesElement.GetArrayLength() > 32)
            throw new InvalidDataException("投稿图片清单无效。");
        var images = imagesElement.EnumerateArray().Select(image => new ReviewImageReference(
            RequiredString(image, "id"), RequiredString(image, "mime"), image.GetProperty("bytes").GetInt64(),
            RequiredString(image, "sha256"))).ToArray();
        var listing = OptionalString(row, "listingId");
        if (listing is not null && !Guid.TryParseExact(listing, "N", out _)) throw new InvalidDataException("排序标识无效。");
        var sha = RequiredString(row, "sha256");
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("投稿排序校验码无效。");
        var reviewToken = RequiredString(row, "reviewToken");
        if (reviewToken.Length != 64 || !reviewToken.All(Uri.IsHexDigit)) throw new InvalidDataException("投稿内容校验标识无效。");
        return new MarketReviewItem(id, listing, RequiredString(row, "version"), RequiredString(metadata, "title"),
            RequiredString(row, "author"), RequiredString(row, "ownerUsername"), RequiredString(row, "state"), sha,
            OptionalString(row, "reason") ?? "", OptionalString(metadata, "gameVersions") ?? "",
            OptionalString(metadata, "description") ?? "", OptionalString(metadata, "releaseNotes") ?? "",
            OptionalString(metadata, "workshopId"), OptionalString(metadata, "dlc") ?? "",
            metadata.GetProperty("modCount").GetInt32(), images) { ReviewToken = reviewToken };
    }

    private static string Summary(MarketReviewItem item) =>
        $"排序名称：{item.Title}\r\n发布者：{item.Author}\r\n\r\n"
        + $"排序简介：\r\n{(string.IsNullOrWhiteSpace(item.Description) ? "未填写简介（允许提交）" : item.Description)}\r\n\r\n"
        + $"更新日志：\r\n{(string.IsNullOrWhiteSpace(item.ReleaseNotes) ? "未填写更新日志" : item.ReleaseNotes)}\r\n\r\n"
        + $"排序版本：{item.Version}\r\n游戏版本：{item.GameVersions}\r\n模组数：{item.ModCount}\r\n所需 DLC：{DlcLabel(item.Dlc)}\r\n"
        + $"工坊合集：{item.WorkshopCollectionId ?? "未绑定"}\r\n审核状态：{StateLabel(item.State)}\r\n退回原因：{item.Reason}\r\n"
        + $"排序标识：{item.ListingId ?? "审核通过后显示"}\r\n上传图片：{item.Images.Count} 张";

    private static string DlcLabel(string dlc) => string.IsNullOrWhiteSpace(dlc) ? "未声明额外 DLC"
        : string.Join("、", dlc.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(DlcName));

    private static string DlcName(string id) => id.ToLowerInvariant() switch
    {
        "ludeon.rimworld.royalty" => "皇权", "ludeon.rimworld.ideology" => "文化",
        "ludeon.rimworld.biotech" => "生物技术", "ludeon.rimworld.anomaly" => "异象",
        "ludeon.rimworld.odyssey" => "奥德赛", _ => id
    };

    private static string StateLabel(string state) => state switch
    {
        "draft" => "草稿", "pending" => "待审核", "approved" => "已上架",
        "rejected" => "已退回", "withdrawn" => "已下架", _ => "未知状态"
    };

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new InvalidDataException("服务器响应字段无效。")
            : throw new InvalidDataException("服务器响应缺少必要字段。");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        try
        {
            var length = response.Content.Headers.ContentLength;
            if (length is null or <= 64 * 1024)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var output = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) > 0)
                {
                    if (output.Length + count > 64 * 1024) throw new InvalidDataException("市场错误响应过大。");
                    output.Write(buffer, 0, count);
                }
                if (output.Length <= 64 * 1024)
                {
                    using var document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
                    detail = OptionalString(document.RootElement, "error");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
        throw new InvalidOperationException(detail ?? $"排序市场请求失败（HTTP {(int)response.StatusCode}）。");
    }

    private static Uri ReadConfiguredOrigin()
    {
        return MarketApiConnection.ReadOrigin();
    }

    private static Uri ValidateOrigin(Uri value)
    {
        if (!value.IsAbsoluteUri || value.UserInfo.Length != 0 || value.Query.Length != 0 || value.Fragment.Length != 0
            || value.AbsolutePath != "/" || value.Scheme != Uri.UriSchemeHttps
                && !(value.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(value.Host, out var address) && IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("排序市场地址必须使用 HTTPS；仅本机回环联调允许 HTTP。");
        return value;
    }

    public void Dispose() => client.Dispose();
}
