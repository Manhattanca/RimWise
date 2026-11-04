using System.Net.Http.Headers;
using System.Text.Json;

namespace RimWise.Integration.AI;

public sealed record AiWalletSnapshot(decimal WalletQuota, decimal AvailableQuota, decimal UsedQuota,
    bool UnlimitedKey, decimal QuotaPerUnit, decimal DisplayRate, string DisplayType)
{
    // RelayRax's public display function divides quota by quota_per_unit, then applies display_rate.
    // A field named token_usage still contains monetary quota, not a count of model tokens.
    public string Describe(decimal quota) => DisplayType is "CNY" or "USD"
        ? $"{checked(quota / QuotaPerUnit * DisplayRate):N4} {DisplayType}"
        : $"{quota:N0} 原始额度单位";
}

public sealed record AiProviderTokenSnapshot(long Input, long Output, int Count,
    DateTimeOffset? Earliest, DateTimeOffset? Latest)
{
    public long Total => checked(Input + Output);
    public AiProviderUsageRow[] Rows { get; init; } = [];
}

public sealed record AiProviderUsageResult(DateTimeOffset QueriedUtc,
    AiWalletSnapshot? Wallet, string? WalletProblem,
    AiProviderTokenSnapshot? Tokens, string? TokenProblem);

/// <summary>Read-only account adapter. No model generation, redirects, raw log persistence, or AI context.</summary>
public sealed class AiProviderUsageClient : IDisposable
{
    private const int MaximumBodyBytes = 4 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly bool ownsClient;

    public AiProviderUsageClient(HttpClient? http = null)
    {
        ownsClient = http is null;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool Supports(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IdnHost.Equals("relayrax.com", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public async Task<AiProviderUsageResult> QueryAsync(string endpoint, string? apiKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Supports(endpoint)) return Failure("此服务暂未适配账户查询；本机实际用量仍会自动累计。");
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 16384 || apiKey.Any(char.IsControl))
            return Failure("请先保存有效的 AI 密钥。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var wallet = ReadAsync("/api/usage/token/", apiKey, ParseWallet, timeout.Token, token);
        var logs = ReadAsync("/api/log/token", apiKey, ParseTokens, timeout.Token, token);
        await Task.WhenAll(wallet, logs).ConfigureAwait(false);
        return new(DateTimeOffset.UtcNow, wallet.Result.Value, wallet.Result.Problem, logs.Result.Value, logs.Result.Problem);
    }

    private static AiProviderUsageResult Failure(string message) => new(DateTimeOffset.UtcNow, null, message, null, message);

    private async Task<(T? Value, string? Problem)> ReadAsync<T>(string path, string key,
        Func<JsonElement, T> parse, CancellationToken token, CancellationToken caller) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://relayrax.com" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, $"查询未成功（HTTP {(int)response.StatusCode}）；请检查密钥或稍后刷新。");
            if (response.RequestMessage?.RequestUri is { } finalUri && !Supports(finalUri.AbsoluteUri))
                return (null, "服务重定向到其他地址，已拒绝读取。");
            if (response.Content.Headers.ContentLength > MaximumBodyBytes) return (null, "返回内容过大，已停止读取。");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[16384];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, token).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > MaximumBodyBytes) return (null, "返回内容过大，已停止读取。");
                buffer.Write(bytes, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            return (parse(json.RootElement), null);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { return (null, "用量查询超时，稍后可刷新。"); }
        catch (HttpRequestException) { return (null, "无法连接用量接口，请检查网络或稍后刷新。"); }
        catch (IOException) { return (null, "读取用量接口失败，稍后可刷新。"); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or OverflowException or InvalidOperationException or FormatException or KeyNotFoundException)
        { return (null, "服务返回的用量格式无效，未将其计入累计。"); }
    }

    private static AiWalletSnapshot ParseWallet(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.True)
            throw new InvalidDataException();
        var data = root.GetProperty("data");
        var unit = Decimal(data, "quota_per_unit");
        var rate = Decimal(data, "display_rate");
        if (unit <= 0 || rate <= 0 || rate > 1000000) throw new InvalidDataException();
        var unlimited = data.GetProperty("unlimited_quota").GetBoolean();
        var type = data.GetProperty("quota_display_type").GetString() ?? "";
        var snapshot = new AiWalletSnapshot(Decimal(data, "wallet_available"), Decimal(data, "total_available"),
            Decimal(data, "total_used"), unlimited, unit, rate, type);
        // Validate conversion before exposing the result, including overflow of server-provided values.
        _ = snapshot.Describe(snapshot.WalletQuota);
        _ = snapshot.Describe(snapshot.AvailableQuota);
        _ = snapshot.Describe(snapshot.UsedQuota);
        return snapshot;
    }

    private static AiProviderTokenSnapshot ParseTokens(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException();
        var logs = root.GetProperty("data");
        if (logs.ValueKind != JsonValueKind.Array || logs.GetArrayLength() > 10000) throw new InvalidDataException();
        long input = 0, output = 0;
        DateTimeOffset? earliest = null, latest = null;
        var rows = new List<AiProviderUsageRow>();
        // Discard content, IPs, user names. Identifiers are hashed before leaving this adapter.
        foreach (var row in logs.EnumerateArray())
        {
            input = checked(input + Integer(row, "prompt_tokens"));
            output = checked(output + Integer(row, "completion_tokens"));
            var time = Integer(row, "created_at");
            if (time > 253402300799) throw new InvalidDataException();
            var utc = DateTimeOffset.FromUnixTimeSeconds(time);
            var model = row.TryGetProperty("model_name", out var modelValue) && modelValue.ValueKind == JsonValueKind.String
                ? modelValue.GetString() ?? "" : "";
            if (model.Length > 512 || model.Any(char.IsControl)) throw new InvalidDataException();
            var requestId = row.TryGetProperty("request_id", out var requestValue) && requestValue.ValueKind == JsonValueKind.String
                ? AiUsageRequest.SafeId(requestValue.GetString()) : null;
            var logId = row.TryGetProperty("id", out var logValue) && logValue.ValueKind == JsonValueKind.Number
                && logValue.TryGetInt64(out var numericId) && numericId >= 0
                ? AiUsageRequest.Hash(numericId.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
            rows.Add(new(logId, requestId, model, utc, Integer(row, "prompt_tokens"), Integer(row, "completion_tokens")));
            if (earliest is null || utc < earliest) earliest = utc;
            if (latest is null || utc > latest) latest = utc;
        }
        _ = checked(input + output);
        return new(input, output, logs.GetArrayLength(), earliest, latest) { Rows = rows.ToArray() };
    }

    private static long Integer(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0)
            throw new InvalidDataException();
        return number;
    }

    private static decimal Decimal(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0)
            throw new InvalidDataException();
        return number;
    }

    public void Dispose() { if (ownsClient) http.Dispose(); }
}
