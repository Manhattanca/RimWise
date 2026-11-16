using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RimWise.Integration.Marketplace;

internal sealed class MarketplaceHttp : IDisposable
{
    internal const string Repository = MarketplaceRepository.FullName;
    internal const string ApiRoot = "https://api.github.com/";
    internal const string RawRoot = MarketplaceRepository.RawRoot;
    internal const int MaximumApiBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;

    internal MarketplaceHttp(HttpMessageHandler? handler)
    {
        var transport = handler ?? new HttpClientHandler();
        // Configure the actual transport, including a caller's delegating-handler chain.
        for (var current = transport; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            if (current is HttpClientHandler client)
            {
                client.AllowAutoRedirect = false;
                client.UseCookies = false;
                client.Credentials = null;
                client.UseDefaultCredentials = false;
                client.PreAuthenticate = false;
            }
            if (current is SocketsHttpHandler sockets)
            {
                sockets.AllowAutoRedirect = false;
                sockets.UseCookies = false;
                sockets.Credentials = null;
                sockets.PreAuthenticate = false;
            }
        }
        http = new HttpClient(transport, disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<(HttpStatusCode Status, byte[] Bytes)> SendAsync(HttpMethod method, string url,
        string? accessToken, HttpContent? content, int maximumBytes, CancellationToken cancellationToken,
        bool allowNotFound = false, IProgress<MarketplaceTransferProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, url) { Content = content };
        var uri = request.RequestUri!;
        MarketplaceJson.Require(uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            (uri.Host == "api.github.com" || uri.Host == "github.com" || uri.Host == "raw.githubusercontent.com"),
            "Unexpected marketplace HTTP destination.");
        request.Headers.UserAgent.ParseAdd("RimWise-Marketplace/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        if (accessToken is not null)
        {
            ValidateToken(accessToken);
            MarketplaceJson.Require(uri.Host == "api.github.com", "Credentials may only be sent to api.github.com.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        if (uri.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            MarketplaceJson.Require(response.RequestMessage?.RequestUri == uri,
                "Marketplace HTTP redirect was refused.");
            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound) return (response.StatusCode, []);
            MarketplaceJson.Require((int)response.StatusCode is >= 200 and <= 299,
                response.StatusCode == HttpStatusCode.NotFound ? "市场目录或排序文件尚未发布（HTTP 404）。" :
                $"GitHub 市场请求失败（HTTP {(int)response.StatusCode}），未接受返回内容。");
            var bytes = await ReadBoundedAsync(response.Content, maximumBytes, timeout.Token, progress).ConfigureAwait(false);
            return (response.StatusCode, bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MarketplaceException("GitHub 市场请求超时，请稍后重试。"); }
        catch (HttpRequestException)
        { throw new MarketplaceException("无法连接 GitHub 市场，请检查网络后重试。"); }
    }

    internal async Task<JsonDocument> JsonAsync(HttpMethod method, string apiPath, string accessToken,
        object? body, CancellationToken cancellationToken)
    {
        var content = body is null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await SendAsync(method, ApiRoot + apiPath, accessToken, content,
            MaximumApiBytes, cancellationToken).ConfigureAwait(false);
        return MarketplaceJson.Document(response.Bytes, MaximumApiBytes);
    }

    internal static void ValidateToken(string token) => MarketplaceJson.Require(
        !string.IsNullOrWhiteSpace(token) && token.Length <= 4096 && token.All(c => c is >= '!' and <= '~'),
        "An OAuth access token or PAT is required; credentials are never stored by this backend.");

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken,
        IProgress<MarketplaceTransferProgress>? progress = null)
    {
        MarketplaceJson.Require(content.Headers.ContentLength is not { } length || length <= maximumBytes,
            "Marketplace response exceeds its byte limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBoundedAsync(stream, maximumBytes, cancellationToken, progress, content.Headers.ContentLength).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken,
        IProgress<MarketplaceTransferProgress>? progress = null, long? total = null)
    {
        using var body = new MemoryStream();
        var buffer = new byte[16384];
        progress?.Report(new(0, total));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)body.Length)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            MarketplaceJson.Require(body.Length + count <= maximumBytes, "Marketplace response exceeds its byte limit.");
            body.Write(buffer, 0, count);
            progress?.Report(new(body.Length, total));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return body.ToArray();
    }

    public void Dispose() => http.Dispose();
}
