using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWise.Integration.AI;

namespace RimWise.Integration.Marketplace;

/// <summary>Version-independent, per-origin and per-client session storage. Never stores a password or CSRF token.</summary>
public sealed class MarketSessionHandler : DelegatingHandler
{
    public const string CookieName = "RimWise.LocalMarket.Session";
    private readonly Uri origin;
    private readonly CookieContainer cookies;
    private readonly ISecretStore store;
    private readonly string key;
    private readonly object gate = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private long generation;
    public string? StorageWarning { get; private set; }
    public long? ActiveGeneration { get { lock (gate) return cookies.GetCookies(origin)[CookieName] is { Expired: false } ? generation : null; } }
    private sealed record Saved(string Origin, string Token, DateTimeOffset Expires);

    public MarketSessionHandler(Uri origin, string scope, ISecretStore? store = null,
        HttpMessageHandler? transport = null, CookieContainer? cookieContainer = null)
    {
        this.origin = origin;
        this.store = store ?? new WindowsCredentialSecretStore();
        key = "RimWise/MarketplaceSession/" + scope + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri)));
        if (transport is null)
        {
            var handler = MarketApiConnection.CreateHandler(origin);
            cookies = handler.CookieContainer;
            InnerHandler = handler;
        }
        else { cookies = cookieContainer ?? new(); InnerHandler = transport; }
        try
        {
            if (this.store.Read(key) is { } data)
            {
                var saved = JsonSerializer.Deserialize<Saved>(data);
                if (saved is not null && saved.Origin == origin.AbsoluteUri && Valid(saved.Token)
                    && saved.Expires > DateTimeOffset.UtcNow && saved.Expires <= DateTimeOffset.UtcNow.AddDays(1))
                    cookies.Add(origin, new Cookie(CookieName, saved.Token, "/") { HttpOnly = true, Secure = origin.Scheme == "https", Expires = saved.Expires.UtcDateTime });
                else this.store.Delete(key);
            }
        }
        catch (Exception ex) when (ex is JsonException or System.ComponentModel.Win32Exception or ArgumentException)
        { StorageWarning = "未能恢复本机登录记录，请重新登录。"; }
    }

    public void Clear()
    {
        lock (gate)
        {
            generation++;
            ExpireCookie();
            try { store.Delete(key); StorageWarning = null; }
            catch (System.ComponentModel.Win32Exception) { StorageWarning = "未能清除 Windows 登录凭据，请检查凭据管理器。"; }
        }
    }

    private void ExpireCookie()
    {
        if (cookies.GetCookies(origin)[CookieName] is { } cookie) cookie.Expired = true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || uri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("登录凭据不能发送到其他服务器。");
        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long before;
            lock (gate) before = generation;
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && uri.AbsolutePath is not ("/api/login" or "/api/register" or "/api/recover" or "/api/password"))
                    Clear();
                if (generation != before) { ExpireCookie(); return response; }
                if (response.Headers.TryGetValues("Set-Cookie", out var headers)
                    && headers.Any(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal)))
                {
                    generation++;
                    var cookie = cookies.GetCookies(origin)[CookieName];
                    try
                    {
                        if (cookie is not null && !cookie.Expired && Valid(cookie.Value))
                            store.Write(key, JsonSerializer.Serialize(new Saved(origin.AbsoluteUri, cookie.Value, DateTimeOffset.UtcNow.AddDays(1))));
                        else store.Delete(key);
                        StorageWarning = null;
                    }
                    catch (System.ComponentModel.Win32Exception) { StorageWarning = "本次登录有效，但未能保存登录记录；重启后可能需要重新登录。"; }
                }
            }
            return response;
        }
        finally { requests.Release(); }
    }

    private static bool Valid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
