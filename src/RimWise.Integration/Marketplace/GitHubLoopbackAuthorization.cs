using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimWise.Integration.Marketplace;

/// <summary>GitHub authorization-code flow for a desktop app using PKCE and a one-use loopback callback.</summary>
public sealed class GitHubLoopbackAuthorization : IDisposable
{
    public const string Scope = "public_repo";
    private const int MaximumRequestBytes = 16 * 1024;
    private readonly string clientId;
    private readonly string clientSecret;
    private readonly MarketplaceHttp http;

    public GitHubLoopbackAuthorization(string clientId, string clientSecret, HttpMessageHandler? handler = null)
    {
        MarketplaceJson.Require(IsClientId(clientId), "GitHub 登录配置无效，请检查 Client ID。");
        MarketplaceJson.Require(!string.IsNullOrWhiteSpace(clientSecret) && clientSecret.Length <= 512 &&
            clientSecret.All(c => c is >= '!' and <= '~'), "GitHub Client Secret 尚未配置或格式无效。");
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        http = new MarketplaceHttp(handler);
    }

    public async Task<GitHubAccessToken> AuthorizeAsync(Func<Uri, CancellationToken, Task> openBrowser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var redirectUri = new Uri($"http://127.0.0.1:{endpoint.Port}/callback");
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizationUri = BuildAuthorizationUri(clientId, redirectUri, state, challenge);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var callback = ReceiveCallbackAsync(listener, endpoint.Port, state, timeout.Token);
        try { await openBrowser(authorizationUri, timeout.Token).ConfigureAwait(false); }
        catch
        {
            timeout.Cancel();
            try { await callback.ConfigureAwait(false); } catch (OperationCanceledException) { }
            throw;
        }
        var result = await callback.ConfigureAwait(false);
        if (result.Error is not null)
            throw new MarketplaceException(result.Error == "access_denied" ? "你已取消 GitHub 授权。" : "GitHub 未能完成授权，请重试。");

        using var response = await PostTokenAsync(new()
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = result.Code!,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = verifier
        }, timeout.Token).ConfigureAwait(false);
        var root = response.RootElement;
        RejectError(root);
        var token = GitHubJson.String(root, "access_token", 4096);
        MarketplaceHttp.ValidateToken(token);
        var type = GitHubJson.String(root, "token_type", 32);
        MarketplaceJson.Require(type.Equals("bearer", StringComparison.OrdinalIgnoreCase), "GitHub 返回了不支持的令牌类型。");
        var scope = GitHubJson.String(root, "scope", 1024);
        MarketplaceJson.Require(scope.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Any(s => s is Scope or "repo"),
            "GitHub 未授予 public_repo 提交权限。");
        return new(token, type, scope);
    }

    public static Uri BuildAuthorizationUri(string clientId, Uri redirectUri, string state, string challenge)
    {
        MarketplaceJson.Require(IsClientId(clientId), "GitHub Client ID 无效。");
        MarketplaceJson.Require(redirectUri.Scheme == "http" && redirectUri.Host == "127.0.0.1" &&
            redirectUri.IsLoopback && redirectUri.Port is >= 1024 and <= 65535 && redirectUri.AbsolutePath == "/callback" &&
            redirectUri.Query.Length == 0 && redirectUri.Fragment.Length == 0,
            "GitHub 回调必须使用 127.0.0.1 上的动态端口和 /callback 路径。");
        MarketplaceJson.Require(state.Length == 43 && state.All(IsBase64Url) && challenge.Length == 43 && challenge.All(IsBase64Url),
            "GitHub 授权安全参数无效。");
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        return new Uri("https://github.com/login/oauth/authorize?" + new FormUrlEncodedContent(query).ReadAsStringAsync().GetAwaiter().GetResult());
    }

    private async Task<CallbackResult> ReceiveCallbackAsync(TcpListener listener, int port, string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            client.NoDelay = true;
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!request.IsValid || request.Host != $"127.0.0.1:{port}" || request.Path != "/callback")
            {
                await WriteResponseAsync(stream, 404, "未找到授权回调。", cancellationToken).ConfigureAwait(false);
                continue;
            }
            var query = ParseQuery(request.Query);
            if (query is null || !query.TryGetValue("state", out var actualState) || !FixedEquals(actualState, expectedState))
            {
                await WriteResponseAsync(stream, 400, "授权校验失败。请关闭此页并在 RimWise 中重试。", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (query.TryGetValue("error", out var error))
            {
                await WriteResponseAsync(stream, 200, "GitHub 授权未完成。可以关闭此页并返回 RimWise。", cancellationToken).ConfigureAwait(false);
                return new(null, error);
            }
            if (!query.TryGetValue("code", out var code) || code.Length is 0 or > 4096 || code.Any(char.IsControl))
            {
                await WriteResponseAsync(stream, 400, "GitHub 返回的授权信息无效。请返回 RimWise 重试。", cancellationToken).ConfigureAwait(false);
                continue;
            }
            await WriteResponseAsync(stream, 200, "GitHub 授权已返回 RimWise。现在可以关闭此页。", cancellationToken).ConfigureAwait(false);
            return new(code, null);
        }
    }

    private static async Task<RequestLine> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        while (bytes.Count <= MaximumRequestBytes)
        {
            var one = new byte[1];
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0) return default;
            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                break;
        }
        if (bytes.Count > MaximumRequestBytes) return default;
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 3) return default;
        var request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" || request[1].Length > 8192) return default;
        var host = lines.Skip(1).TakeWhile(line => line.Length > 0)
            .Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2 && parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1].Trim()).SingleOrDefault();
        if (!Uri.TryCreate("http://127.0.0.1" + request[1], UriKind.Absolute, out var uri)) return default;
        return new(host is not null && uri.Port > 0, host ?? "", uri.AbsolutePath, uri.Query.TrimStart('?'));
    }

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(HttpMethod.Post, "https://github.com/login/oauth/access_token", null,
            new FormUrlEncodedContent(fields), 16384, cancellationToken).ConfigureAwait(false);
        return MarketplaceJson.Document(response.Bytes, 16384);
    }

    private static Dictionary<string, string>? ParseQuery(string query)
    {
        if (query.Length is 0 or > 8192) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) return null;
            string key, value;
            try { key = Uri.UnescapeDataString(pair[0].Replace('+', ' ')); value = Uri.UnescapeDataString(pair[1].Replace('+', ' ')); }
            catch (UriFormatException) { return null; }
            if (key.Length is 0 or > 64 || value.Length > 4096 || key.Any(char.IsControl) || value.Any(char.IsControl) ||
                !result.TryAdd(key, value)) return null;
        }
        return result;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string message, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes($$$"""
            <!doctype html>
            <html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="color-scheme" content="light dark"><title>RimWise · GitHub 授权</title>
            <style>
            :root{color-scheme:light dark;--page:#f4f7f7;--panel:#ffffff;--text:#233636;--muted:#586c6c;--line:#d8e3e1;--accent:#207d71}
            @media(prefers-color-scheme:dark){:root{--page:#172122;--panel:#202d2e;--text:#e0eded;--muted:#a1b8b5;--line:#354849;--accent:#6ad2ba}}
            *{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--page);color:var(--text);font:16px/1.8 "Segoe UI","Microsoft YaHei",sans-serif;padding:24px}
            main{width:min(100%,560px);background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:40px}
            .brand{color:var(--accent);font-size:22px;font-weight:650;letter-spacing:.4px}.label{font-size:13px;color:var(--muted);margin-top:2px}
            h1{font-size:24px;margin:30px 0 12px;font-weight:600}p{margin:0;color:var(--muted)}footer{border-top:1px solid var(--line);margin-top:28px;padding-top:18px;font-size:13px;color:var(--muted)}
            </style></head><body><main><div class="brand">RimWise</div><div class="label">GitHub 账号授权</div>
            <h1>{{{(status == 200 ? "请返回 RimWise" : "授权回调未完成")}}}</h1><p>{{{WebUtility.HtmlEncode(message)}}}</p>
            <footer>登录结果以软件内显示为准。此页面可以安全关闭。</footer></main></body></html>
            """);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Error" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    private static bool FixedEquals(string actual, string expected)
    {
        var left = Encoding.UTF8.GetBytes(actual);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool IsBase64Url(char value) => char.IsAsciiLetterOrDigit(value) || value is '-' or '_';
    private static bool IsClientId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void RejectError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out _)) return;
        throw new MarketplaceException(GitHubJson.String(root, "error", 100) switch
        {
            "bad_verification_code" or "incorrect_client_credentials" => "GitHub 拒绝了授权信息，请检查 OAuth 应用配置后重试。",
            "redirect_uri_mismatch" => "GitHub 回调地址与 OAuth 应用设置不匹配。",
            _ => "GitHub 登录未完成，请重试。"
        });
    }

    public void Dispose() => http.Dispose();
    private sealed record CallbackResult(string? Code, string? Error);
    private readonly record struct RequestLine(bool IsValid, string Host, string Path, string Query);
}
