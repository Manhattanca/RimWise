using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace RimWise.Integration.Databases;

/// <summary>Downloads data only: no credentials, uploads, archive extraction or executable content.</summary>
internal static class DatabaseSourceReader
{
    internal static (string Owner, string Repository) GitHubRepository(string location)
    {
        if (!location.Contains("://", StringComparison.Ordinal)) location = "https://github.com/" + location;
        var uri = WebUri(location);
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || parts.Length != 2
            || uri.Query.Length != 0 || parts.Any(p => p.Length is < 1 or > 100 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
            throw new InvalidDataException("GitHub 来源请填写仓库地址，如 https://github.com/RimSort/Community-Rules-Database；文件直链请选“网址”。");
        var repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (repository.Length == 0 || parts.Any(p => p is "." or "..")) throw new InvalidDataException("GitHub 仓库地址无效。");
        return (parts[0], repository);
    }

    internal static Uri WebUri(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback
            || uri.HostNameType != UriHostNameType.Dns && uri.HostNameType != UriHostNameType.IPv4 && uri.HostNameType != UriHostNameType.IPv6)
            throw new InvalidDataException("数据库网址须为不含账号密码的 HTTPS 下载地址；本地数据库请选择“本地文件”。");
        return uri;
    }

    internal static async Task<DatabaseSnapshot> ReadAsync(DatabaseSource source, bool community, HttpClient client, CancellationToken token)
    {
        source.Validate(); token.ThrowIfCancellationRequested();
        if (source.Mode == DatabaseSourceMode.Disabled) return DatabaseParser.Empty();
        var name = community ? "communityRules.json" : "steamDB.json";
        var limit = community ? 4 * 1024 * 1024 : 64 * 1024 * 1024;
        byte[] bytes; string commit; string origin;
        if (source.Mode == DatabaseSourceMode.GitHub)
        {
            var (owner, repo) = GitHubRepository(source.Location);
            var response = await DownloadAsync(client, new($"https://api.github.com/repos/{owner}/{repo}/commits?per_page=1"), 256 * 1024, token);
            using var json = JsonDocument.Parse(response, new() { MaxDepth = 24 });
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != 1
                || json.RootElement[0].ValueKind != JsonValueKind.Object
                || !json.RootElement[0].TryGetProperty("sha", out var field) || field.ValueKind != JsonValueKind.String
                || !DatabaseParser.Hex(field.GetString(), 40)) throw new InvalidDataException("GitHub 返回的提交编号无效。");
            commit = field.GetString()!.ToLowerInvariant();
            bytes = await DownloadAsync(client, new($"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{name}"), limit, token);
            origin = $"{owner}/{repo}";
        }
        else
        {
            if (source.Mode == DatabaseSourceMode.LocalFile)
            {
                await using var stream = new FileStream(source.Location, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
                bytes = await ReadBoundedAsync(stream, limit + 1024 * 1024, token);
                origin = "本地文件：" + Path.GetFileName(source.Location);
            }
            else
            {
                var uri = WebUri(source.Location);
                bytes = await DownloadAsync(client, uri, limit + 1024 * 1024, token);
                origin = "网址：" + uri.Host;
            }
            bytes = Unpack(bytes, name, limit, token);
            commit = DatabaseParser.Hash(bytes)[..40];
        }
        token.ThrowIfCancellationRequested();
        var parsed = community
            ? DatabaseParser.ParseSources(bytes, commit, Encoding.UTF8.GetBytes("{\"database\":{}}"), new('0', 40))
            : DatabaseParser.ParseSources(Encoding.UTF8.GetBytes("{\"rules\":{}}"), new('0', 40), bytes, commit);
        return DatabaseParser.WithOrigins(parsed, community ? origin : "不使用", community ? "不使用" : origin);
    }

    private static byte[] Unpack(byte[] bytes, string name, int maximum, CancellationToken token)
    {
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            using var input = new MemoryStream(bytes);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count > 4096) throw new InvalidDataException("数据库压缩包条目过多。");
            var matches = zip.Entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Length > maximum)
                throw new InvalidDataException($"压缩包须包含唯一的 {name}，且文件大小不能超限。");
            // Read the one expected entry in memory. Never extract paths supplied by an archive.
            using var stream = matches[0].Open();
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (output.Length + count > maximum) throw new InvalidDataException("数据库解压超限。");
                output.Write(buffer, 0, count);
            }
            bytes = output.ToArray();
        }
        if (bytes.Length > maximum) throw new InvalidDataException("数据库超过大小限制。");
        // UTF-8 BOM is common in local files produced by Windows tools.
        return bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes;
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, Uri uri, int maximum, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            WebUri(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects == 5 || response.Headers.Location is not { } location) throw new IOException("数据库重定向无效或次数过多。");
                uri = location.IsAbsoluteUri ? location : new(uri, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException($"数据库下载失败（HTTP {(int)response.StatusCode}），保留已有数据。");
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("数据库下载超限。");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await ReadBoundedAsync(stream, maximum, token);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("数据库读取超限。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
