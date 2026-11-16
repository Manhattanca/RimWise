using System.Net;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.Databases;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Steam;

/// <summary>Metadata is the authority for required IDs; neither URLs nor IDs supplied by AI are used.</summary>
public static class WorkshopDependencyDownloads
{
    public static async Task<string> GetCollectionDescriptionAsync(string input, CancellationToken token = default)
    {
        var id = ImportedOrderDownloads.WorkshopIdFromInput(input);
        if (id is null) throw new InvalidDataException("请输入有效的 Steam 创意工坊合集编号或链接。");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await GetCollectionDescriptionAsync(client, id, token).ConfigureAwait(false);
    }

    internal static async Task<string> GetCollectionDescriptionAsync(HttpClient client, string id, CancellationToken token)
    {
        if (ImportedOrderDownloads.WorkshopIdFromInput(id) != id)
            throw new InvalidDataException("Steam 创意工坊编号无效。");
        using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["itemcount"] = "1", ["publishedfileids[0]"] = id });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/") { Content = body };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Steam 未能返回该合集的简介。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, 256 * 1024, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (!json.RootElement.TryGetProperty("response", out var root)
            || !root.TryGetProperty("publishedfiledetails", out var details)
            || details.ValueKind != JsonValueKind.Array || details.GetArrayLength() != 1)
            throw new InvalidDataException("Steam 返回的合集资料格式无效。");
        var item = details[0];
        if (!item.TryGetProperty("result", out var result) || !result.TryGetInt32(out var resultCode) || resultCode != 1
            || !item.TryGetProperty("consumer_app_id", out var app) || !app.TryGetInt32(out var appId) || appId != 294100
            || !item.TryGetProperty("publishedfileid", out var publishedId) || publishedId.GetString() != id
            || !item.TryGetProperty("file_type", out var type) || !type.TryGetInt32(out var fileType) || fileType != 2
            || !item.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("该链接不是可读取的 RimWorld 创意工坊合集。");
        var text = description.GetString() ?? "";
        if (text.Length > 8000) throw new InvalidDataException("Steam 合集简介超过 8000 字，未自动填入。");
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximumBytes + 1 - (int)buffer.Length)), token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Steam 返回的合集资料过大。");
            buffer.Write(chunk, 0, read);
        }
        token.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    // Historical download intents are an audit trail, not authority to re-enable dependencies.
    internal static IEnumerable<RequiredModDownload> ActiveInstalledDownloads(ModCatalogue catalogue,
        IReadOnlyList<string> order, GameVersion version, IEnumerable<RequiredModDownload> downloads)
    {
        var active = order.Select(id => catalogue.Resolve(id, version).UniqueInstallation)
            .OfType<ModInstallation>().Select(m => m.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return downloads.Where(item => item.DiscoverPackageId
            ? catalogue.Installations.Any(m => m.Source == ModSource.Workshop && m.WorkshopId == item.WorkshopId && active.Contains(m.Directory))
            : catalogue.Resolve(item.PackageId, version).UniqueInstallation is { } mod && active.Contains(mod.Directory));
    }

    internal static void VerifyInstalled(ModCatalogue catalogue,string workshop,GameVersion version,IEnumerable<RequiredModDownload> downloads)
    {
        foreach(var item in downloads)
        {
            var expected=ScopedPaths.Child(ScopedPaths.Ordinary(workshop,true),item.WorkshopId);
            var matches = item.DiscoverPackageId ? catalogue.Installations.Where(m => m.Source == ModSource.Workshop && m.WorkshopId == item.WorkshopId && m.Metadata is not null).ToArray() : [];
            var mod=item.DiscoverPackageId ? matches.Length == 1 ? catalogue.Resolve(matches[0].PackageId,version).UniqueInstallation : null
                : catalogue.Resolve(item.PackageId,version).UniqueInstallation;
            if(mod is null || mod.Source!=ModSource.Workshop || mod.WorkshopId!=item.WorkshopId
                || !string.Equals(ScopedPaths.Ordinary(mod.Directory,true),expected,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载结果的包名或工坊目录与声明不符，未启用。");
        }
    }
    public static IReadOnlyList<RequiredModDownload> Missing(ModCatalogue catalogue, IReadOnlyList<string> order, GameVersion version,
        DatabaseSnapshot? database = null)
    {
        var missing = new Dictionary<string,RequiredModDownload>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(order);
        while (pending.TryDequeue(out var id))
        {
            if (!visited.Add(id)) continue;
            var mod = catalogue.Resolve(id,version).UniqueInstallation;
            if (mod?.Metadata is null) throw new InvalidDataException($"模组未安装、重复或资料不可读：{id}");
            foreach (var dependency in mod.Metadata.Requirements.Dependencies)
            {
                var matches = catalogue.Installations.Where(m => m.PackageId.Equals(dependency.PackageId,StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1) throw new InvalidDataException($"前置存在多份安装：{dependency.PackageId}");
                if (matches.Length == 1) { pending.Enqueue(dependency.PackageId); continue; }
                var workshopId = dependency.WorkshopId.Length > 0 ? dependency.WorkshopId : database?.RequiredWorkshopId(dependency.PackageId, version)
                    ?? throw new InvalidDataException($"必要前置缺少可信工坊链接：{dependency.PackageId}");
                if (dependency.PackageId.StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("必要内容为本体或 DLC，不能自动订阅：" + dependency.PackageId);
                if (missing.TryGetValue(dependency.PackageId,out var old) && old.WorkshopId != workshopId)
                    throw new InvalidDataException($"必要前置的工坊声明有冲突：{dependency.PackageId}");
                missing[dependency.PackageId] = new(dependency.PackageId,workshopId);
            }
        }
        return missing.Values.ToArray();
    }

    internal static async Task VerifyRimWorldItemAsync(string id,CancellationToken token)
    {
        if (id.Length == 0 || !id.All(char.IsAsciiDigit) || !ulong.TryParse(id,out var number) || number == 0)
            throw new InvalidDataException("无效的工坊编号。");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        await VerifyRimWorldItemAsync(client,id,token);
    }

    // Internal transport seam permits verification of malicious/oversized replies without Steam access.
    internal static async Task VerifyRimWorldItemAsync(HttpClient client,string id,CancellationToken token)
    {
        if (id.Length == 0 || !id.All(char.IsAsciiDigit) || !ulong.TryParse(id,out var number) || number == 0)
            throw new InvalidDataException("无效的工坊编号。");
        using var body = new FormUrlEncodedContent(new Dictionary<string,string> { ["itemcount"]="1",["publishedfileids[0]"]=id });
        using var request = new HttpRequestMessage(HttpMethod.Post,"https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/") { Content=body };
        using var response = await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Steam 工坊资料核对失败，未订阅。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer,token)) > 0)
        { if (bounded.Length + count > 128*1024) throw new InvalidDataException("工坊资料过大。"); bounded.Write(buffer,0,count); }
        using var json = JsonDocument.Parse(bounded.ToArray(),new JsonDocumentOptions { MaxDepth=16 });
        var details = json.RootElement.GetProperty("response").GetProperty("publishedfiledetails");
        if (details.GetArrayLength() != 1) throw new InvalidDataException("工坊资料数量无效。");
        var item = details[0];
        if (item.GetProperty("result").GetInt32()!=1 || item.GetProperty("consumer_app_id").GetInt32()!=294100
            || item.GetProperty("publishedfileid").GetString()!=id)
            throw new InvalidDataException("该工坊项目无法验证为环世界模组，未订阅。");
    }
}
