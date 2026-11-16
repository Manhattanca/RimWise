using System.Text.Json;

namespace RimWise.Integration.Marketplace;

/// <summary>Public OAuth device flow. Register a real GitHub OAuth app and enable device flow first.</summary>
public sealed class GitHubDeviceAuthorization : IDisposable
{
    public const string Scope = "public_repo";
    private readonly MarketplaceHttp http;
    private readonly string clientId;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private GitHubDeviceCode? issued;
    private DateTimeOffset expires;

    public GitHubDeviceAuthorization(string clientId, HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        MarketplaceJson.Require(!string.IsNullOrWhiteSpace(clientId) && clientId.Length <= 200 &&
            clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'),
            "GitHub login is unavailable: configure a registered OAuth public client ID with device flow enabled. Draft/export does not require login.");
        this.clientId = clientId;
        http = new MarketplaceHttp(handler);
        time = timeProvider ?? TimeProvider.System;
    }

    public async Task<GitHubDeviceCode> StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            issued = null;
            var start = time.GetUtcNow();
            using var response = await PostAsync("https://github.com/login/device/code", new()
            {
                ["client_id"] = clientId, ["scope"] = Scope
            }, cancellationToken).ConfigureAwait(false);
            var root = response.RootElement;
            RejectError(root);
            var deviceCode = GitHubJson.String(root, "device_code", 1024);
            var userCode = GitHubJson.String(root, "user_code", 128);
            var verification = GitHubJson.String(root, "verification_uri", 200);
            MarketplaceJson.Require(verification == "https://github.com/login/device", "Unexpected GitHub device verification destination.");
            var lifetime = GitHubJson.Integer(root, "expires_in", 1, 3600);
            var interval = root.TryGetProperty("interval", out _) ? GitHubJson.Integer(root, "interval", 1, 60) : 5;
            issued = new GitHubDeviceCode(deviceCode, userCode, new Uri(verification), lifetime, interval);
            expires = start.AddSeconds(lifetime);
            return issued;
        }
        finally { gate.Release(); }
    }

    public async Task<GitHubAccessToken> PollAsync(GitHubDeviceCode deviceCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MarketplaceJson.Require(issued is not null && issued == deviceCode,
                "Start a device authorization with this client before polling.");
            var interval = deviceCode.Interval;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = expires - time.GetUtcNow();
                MarketplaceJson.Require(remaining > TimeSpan.Zero, "GitHub device authorization expired; start login again.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(interval, remaining.TotalSeconds)), time, cancellationToken).ConfigureAwait(false);
                MarketplaceJson.Require(time.GetUtcNow() < expires, "GitHub device authorization expired; start login again.");
                using var response = await PostAsync("https://github.com/login/oauth/access_token", new()
                {
                    ["client_id"] = clientId,
                    ["device_code"] = deviceCode.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }, cancellationToken).ConfigureAwait(false);
                var root = response.RootElement;
                MarketplaceJson.Require(time.GetUtcNow() < expires, "GitHub device authorization expired; start login again.");
                if (root.TryGetProperty("error", out _))
                {
                    var error = GitHubJson.String(root, "error", 100);
                    if (error == "authorization_pending") continue;
                    if (error == "slow_down") { interval += 5; continue; }
                    issued = null;
                    RejectError(root);
                }
                var token = GitHubJson.String(root, "access_token", 4096);
                MarketplaceHttp.ValidateToken(token);
                var type = GitHubJson.String(root, "token_type", 32);
                MarketplaceJson.Require(type.Equals("bearer", StringComparison.OrdinalIgnoreCase), "Unsupported GitHub access token type.");
                var scope = GitHubJson.String(root, "scope", 1024);
                MarketplaceJson.Require(scope.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Any(s => s is Scope or "repo"),
                    "GitHub did not grant public_repo publishing permission.");
                issued = null;
                return new(token, type, scope);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<JsonDocument> PostAsync(string url, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(HttpMethod.Post, url, null, new FormUrlEncodedContent(fields),
            16384, cancellationToken).ConfigureAwait(false);
        return MarketplaceJson.Document(response.Bytes, 16384);
    }

    private static void RejectError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out _)) return;
        throw new MarketplaceException(GitHubJson.String(root, "error", 100) switch
        {
            "access_denied" => "GitHub device authorization was denied.",
            "expired_token" => "GitHub device authorization expired; start login again.",
            "incorrect_client_credentials" or "device_flow_disabled" => "GitHub OAuth registration is unavailable or device flow is disabled; check the configured public client ID.",
            _ => "GitHub device authorization failed; start login again."
        });
    }

    public void Dispose() { http.Dispose(); gate.Dispose(); }
}

internal static class GitHubJson
{
    internal static string String(JsonElement element, string property, int maximumLength)
    {
        MarketplaceJson.Require(element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out _), "Incomplete GitHub response.");
        var value = element.GetProperty(property);
        MarketplaceJson.Require(value.ValueKind == JsonValueKind.String, "Unexpected GitHub response field type.");
        var result = value.GetString();
        MarketplaceJson.Require(result is { Length: > 0 } && result.Length <= maximumLength && !result.Any(char.IsControl), "Invalid GitHub response field.");
        return result!;
    }

    internal static int Integer(JsonElement element, string property, int minimum, int maximum)
    {
        MarketplaceJson.Require(element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out _), "Incomplete GitHub response.");
        var value = element.GetProperty(property);
        MarketplaceJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _), "Unexpected GitHub integer response.");
        var result = value.GetInt32();
        MarketplaceJson.Require(result >= minimum && result <= maximum, "GitHub response integer is out of range.");
        return result;
    }

    internal static JsonElement Object(JsonElement element, string property)
    {
        MarketplaceJson.Require(element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object,
            "Incomplete GitHub object response.");
        return element.GetProperty(property);
    }

    internal static string Sha(JsonElement element, string property)
    {
        var sha = String(element, property, 40);
        MarketplaceJson.Require(sha.Length == 40 && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "Invalid GitHub commit SHA.");
        return sha;
    }
}
