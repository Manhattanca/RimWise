using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimWise.Integration.Updates;

public sealed record ReleaseDownload(string Version, string FileName, Uri Url, long Size, string Sha256,
    bool IsInstaller);
public sealed record DownloadProgress(long Received, long Total);

/// <summary>Reads public GitHub releases and downloads verified assets. Never installs or restarts.</summary>
public sealed class GitHubReleaseUpdater(HttpClient http)
{
    public const string Repository = "Manhattanca/RimWise";
    public const string ReleasePage = "https://github.com/" + Repository + "/releases/latest";
    private const long MaximumAssetSize = 1024L * 1024 * 1024;
    private static readonly Regex VersionPattern = new(@"^v?(\d+\.\d+\.\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant);

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ReleaseDownload?> CheckAsync(string installedVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(new Uri("https://api.github.com/repos/" + Repository + "/releases/latest"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateDownloadException("暂时无法取得公开发布版本，请稍后重试。");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new UpdateDownloadException("GitHub 暂时限制了请求，请稍后重试。");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (body.Length + count > 2 * 1024 * 1024) throw new UpdateDownloadException("版本信息过大，未采用此响应。");
            body.Write(buffer, 0, count);
        }
        return ParseRelease(body.ToArray(), installedVersion);
    }

    public static ReleaseDownload? ParseRelease(byte[] json, string installedVersion)
    {
        var current = installedVersion.Split('+')[0].Split('-')[0];
        if (!TryVersion(current, out var currentVersion)) throw new UpdateDownloadException("无法识别当前软件版本。");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryVersion(tag, out var next)) throw new UpdateDownloadException("发布版本号无法识别。");
        if (next <= currentVersion) return null;
        var version = tag.TrimStart('v');
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        foreach (var name in new[] { $"RimWise-v{version}-Setup.exe", $"RimWise-v{version}.zip" })
        {
            var matches = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length > 1) throw new UpdateDownloadException("发布附件名称重复，未下载。");
            if (matches.Length == 0) continue;
            var asset = matches[0];
            var url = asset.GetProperty("browser_download_url").GetString();
            var expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (url != expectedUrl || size <= 0 || size > MaximumAssetSize ||
                asset.GetProperty("state").GetString() != "uploaded")
                throw new UpdateDownloadException("发布附件信息不完整，未下载。");
            if (digest is null || !Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
                throw new UpdateDownloadException("发布附件缺少 SHA256 校验信息，请等待维护者补全。");
            return new ReleaseDownload(version, name, new Uri(url), size, digest[7..].ToLowerInvariant(), name.EndsWith(".exe"));
        }
        throw new UpdateDownloadException("新版本尚未提供安装包或程序 ZIP，请稍后重试。");
    }

    public async Task<string> DownloadAsync(ReleaseDownload release, string cacheDirectory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        // Validate again at the write boundary, even when the caller constructs a record itself.
        var expectedName = $"RimWise-v{release.Version}" + (release.IsInstaller ? "-Setup.exe" : ".zip");
        if (!TryVersion(release.Version, out _) || release.Version.StartsWith('v') || release.FileName != expectedName ||
            release.Size <= 0 || release.Size > MaximumAssetSize ||
            !Regex.IsMatch(release.Sha256, @"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant) ||
            !IsReleaseAsset(release.Url, release.Version, expectedName))
            throw new UpdateDownloadException("更新附件信息无效。");
        var directory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(directory);
        EnsureNoLink(directory);
        var destination = Path.Combine(directory, release.FileName);
        var lockPath = Path.Combine(directory, "download.lock");
        EnsureNoLink(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        EnsureNoLink(destination);
        if (await IsVerifiedAsync(destination, release, cancellationToken).ConfigureAwait(false)) return destination;
        var partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            using var response = await GetAssetAsync(release.Url, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new UpdateDownloadException("更新包大小与发布记录不符。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                long received = 0;
                var lastReport = Environment.TickCount64;
                int count;
                while ((count = await ReadWithTimeoutAsync(input, buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > release.Size) throw new UpdateDownloadException("更新包超过发布记录中的大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    if (Environment.TickCount64 - lastReport >= 1000 || received == release.Size)
                    {
                        progress?.Report(new(received, release.Size));
                        lastReport = Environment.TickCount64;
                    }
                }
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateDownloadException("更新包未下载完整或校验失败，请重试。");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            EnsureNoLink(destination);
            File.Move(partial, destination, overwrite: true);
            CleanupPrevious(directory, release.FileName);
            return destination;
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsReleaseAsset(Uri uri, string version, string name) =>
        uri.AbsoluteUri == $"https://github.com/{Repository}/releases/download/v{version}/{name}" ||
        uri.AbsoluteUri == $"https://github.com/{Repository}/releases/download/{version}/{name}";

    public static async Task<bool> IsVerifiedAsync(string path, ReleaseDownload release, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        EnsureNoLink(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length != release.Size) return false;
        var digest = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        return Convert.ToHexString(digest).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> GetAssetAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new UpdateDownloadException("更新下载跳转到非 GitHub 地址，已停止下载。");
            using var request = Request(uri);
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new UpdateDownloadException("更新下载跳转地址缺失。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new UpdateDownloadException("更新下载跳转次数过多。");
    }

    private static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("RimWise-Updater/1.0");
        return request;
    }

    private static bool TryVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var match = VersionPattern.Match(text);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    private static void EnsureNoLink(string path)
    {
        for (var current = new FileInfo(path); current is not null; current = current.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((File.Exists(current.FullName) || Directory.Exists(current.FullName)) &&
                (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new UpdateDownloadException("更新缓存路径包含目录链接，请选择普通用户数据目录。");
    }

    private static void CleanupPrevious(string directory, string currentName)
    {
        // Only files recorded by this updater may be removed, never a directory sweep.
        var receipt = Path.Combine(directory, "latest-download.json");
        try
        {
            EnsureNoLink(receipt);
            if (File.Exists(receipt))
            {
                var previous = JsonSerializer.Deserialize<string>(File.ReadAllText(receipt));
                if (previous != currentName && previous is not null && Regex.IsMatch(previous,
                    @"^RimWise-v\d+\.\d+\.\d+(?:\.\d+)?(?:-Setup\.exe|\.zip)$", RegexOptions.CultureInvariant))
                {
                    var old = Path.Combine(directory, previous);
                    EnsureNoLink(old);
                    File.Delete(old);
                }
            }
            File.WriteAllText(receipt, JsonSerializer.Serialize(currentName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
}

public sealed class UpdateDownloadException(string message) : IOException(message);
