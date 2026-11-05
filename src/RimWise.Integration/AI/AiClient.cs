using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RimWise.Integration.AI;

public sealed record AiRequest(
    AiSettings Settings,
    string? ApiKey,
    string GameVersion,
    IReadOnlyList<string> CurrentOrder,
    IReadOnlyList<string> InstalledMods,
    IReadOnlyList<string> RetainedMods,
    IReadOnlyList<string> Diagnostics)
{
    public int? OutputTokenLimit { get; init; }
    public bool LogDiagnosisOnly { get; init; }
    // Local provenance only; never sent as model instructions.
    public string DatabaseSnapshotId { get; init; } = "";
    // Local display callback only; never included in a prompt or recovery checkpoint.
    public IProgress<AiResponseUpdate>? ResponseProgress { get; init; }
    // Trusted local billing observer; never serialized into provider messages.
    public Action<string[]>? ProviderIdsObserved { get; init; }
    // Diagnostics is legacy local context. Raw text must never enter a remote sorting prompt.
    public IReadOnlyList<AiDiagnosticFact> DiagnosticFacts { get; init; } = [];
    public AiStartupContext? StartupContext { get; init; }
    public AiRuntimeContext? RuntimeContext { get; init; }
    public AiLogContext? LogContext { get; init; }
    public IReadOnlyList<AiModRequirementFact> ModRequirements { get; init; } = [];
    public IReadOnlyList<RimWise.Core.CustomOrderRule> OrderRules { get; init; } = [];
}

public sealed record AiClientResult(
    bool Succeeded,
    string Message,
    StructuredAiResponse? Response,
    string? RawResponse)
{
    public AiTokenUsage? Usage { get; init; }
    public string? PublicContent { get; init; }
    public string[] ProviderIds { get; init; } = [];
    public AiReplyFailure Failure { get; init; }
    public AiServiceError? ServiceError { get; init; }
}

public sealed record AiConnectionResult(bool Succeeded, string Message)
{
    public AiTokenUsage? Usage { get; init; }
    public IReadOnlyList<string> Models { get; init; } = [];
}

public interface IAiClient
{
    Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? apiKey, CancellationToken cancellationToken);
    Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken cancellationToken);
}

public sealed class AiClient : IAiClient, IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public AiClient(HttpClient? httpClient = null)
    {
        ownsClient = httpClient is null;
        this.httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? apiKey, CancellationToken cancellationToken)
    {
        Diagnostics.DiagnosticRedactor.RegisterSecret(apiKey);
        settings = settings.Normalize();
        var problem = ConfigurationProblem(settings, requireModel: false);
        if (problem is not null) return new(false, problem);
        var endpoint = new UriBuilder(settings.Endpoint);
        var path = endpoint.Path.TrimEnd('/');
        const string suffix = "/chat/completions";
        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return new(false, "连接检查需要标准的 /v1/ 或 /chat/completions 地址；不会尝试调用模型。");
        endpoint.Path = path[..^suffix.Length] + "/models";
        var result = await SendAsync(settings, apiKey, null, cancellationToken, endpoint.Uri, HttpMethod.Get).ConfigureAwait(false);
        if (result.Error is not null) return new(false, result.Error + " 连接检查不会回退为模型生成请求。");
        try
        {
            using var document = JsonDocument.Parse(result.Body!, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array
                || models.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.Object
                    || !m.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String))
                return new(false, "服务返回格式不兼容，未获得模型列表；请检查 API 地址。");
            var ids = models.EnumerateArray().Select(m => m.GetProperty("id").GetString()!)
                .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 200 && !id.Any(char.IsControl))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var visible = ids.Contains(settings.Model, StringComparer.Ordinal);
            return new(true, "连接与鉴权检查通过。未调用模型生成。" +
                (settings.Model.Length > 0 && !visible ? " 当前模型未出现在列表中，请核对模型名称。" : "")) { Models = ids };
        }
        catch (JsonException) { return new(false, "服务返回格式不兼容，请检查 API 地址。"); }
    }

    public async Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken cancellationToken)
    {
        Diagnostics.DiagnosticRedactor.RegisterSecret(request.ApiKey);
        var settings = request.Settings.Normalize();
        if (!settings.Enabled) return Refused("AI 未启用。");
        var problem = ConfigurationProblem(settings);
        if (problem is not null) return Refused(problem);

        if (request.OutputTokenLimit is < 1 or > 32768) return Refused("单次输出 token 上限无效。");
        IReadOnlyList<AiMessage> messages;
        try { messages = AiPrompt.Messages(request); }
        catch (ArgumentException) { return Refused("AI 上下文未通过信息边界检查，未发送请求。请检查模组标识及元数据。"); }
        var payload = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };
        AiChatCompatibility.Configure(payload, settings, request.OutputTokenLimit);
        if (request.ResponseProgress is not null && settings.StreamResponses)
        {
            payload["stream"] = true;
            payload["stream_options"] = new { include_usage = true };
        }
        var result = await SendAsync(settings, request.ApiKey, payload, cancellationToken,
            progress: request.ResponseProgress, idsObserved: request.ProviderIdsObserved).ConfigureAwait(false);
        var usage = result.Usage ?? (result.Body is null ? null : AiTokenUsage.Read(result.Body));
        Diagnostics.DiagnosticEventLog.Current?.Record("ai-response", "completion",
            $"stream={result.Streamed}; finish={AiChatCompatibility.SafeFinish(result.FinishReason)}; input={usage?.Input}; output={usage?.Output}; reasoning={usage?.ReasoningOutput}; requestedOutput={request.OutputTokenLimit}; failure={result.ServiceError?.Kind ?? "none"}");
        if (result.Error is not null) return Refused(result.Error) with
            { Usage = usage, ProviderIds = result.ProviderIds, Failure = result.FinishReason == "length"
                ? AiReplyFailure.OutputLimit : result.ServiceError?.Kind == "incomplete"
                    ? AiReplyFailure.IncompleteResponse : AiReplyFailure.ServiceFailure,
                ServiceError = result.ServiceError };
        string? publicContent = null;
        var incomplete = false;
        try
        {
            var content = AiFinalAnswer.Normalize(ExtractContent(result.Body!, out incomplete));
            publicContent = Trim(content);
            if (!result.Streamed) request.ResponseProgress?.Report(new(content));
            var structured = StructuredAiResponse.Parse(content);
            return new(true, "已收到结构化 AI 建议，尚未应用任何动作。", structured, Trim(content)) { Usage = usage, PublicContent = publicContent, ProviderIds = result.ProviderIds };
        }
        catch (JsonException) { return new(false, "AI 返回不是有效的 JSON。", null, null)
            { Usage = usage, PublicContent = publicContent, ProviderIds = result.ProviderIds, Failure = AiReplyFailure.InvalidJson }; }
        catch (InvalidDataException ex) { return new(false, ex.Message, null, null)
            { Usage = usage, PublicContent = publicContent, ProviderIds = result.ProviderIds,
                Failure = incomplete ? result.FinishReason == "length" ? AiReplyFailure.OutputLimit
                    : AiReplyFailure.IncompleteResponse : AiReplyFailure.InvalidStructure }; }
    }

    private sealed record TransportReply(string? Body, string? Error, AiTokenUsage? Usage = null, bool Streamed = false)
    {
        public string? FinishReason { get; init; }
        public string[] ProviderIds { get; init; } = [];
        public AiServiceError? ServiceError { get; init; }
    }

    private async Task<TransportReply> SendAsync(AiSettings settings, string? apiKey, object? payload,
        CancellationToken token, Uri? endpoint = null, HttpMethod? method = null, IProgress<AiResponseUpdate>? progress = null,
        Action<string[]>? idsObserved = null)
    {
        var streamed = new AiStreamingResponse(progress);
        string[] headerIds = [];
        int? status = null;
        TransportReply Reply(string? body, string? error, AiTokenUsage? usage = null, bool streaming = false,
            string kind = "format") =>
            new(body, error, usage, streaming) { ProviderIds = headerIds.Concat(streamed.ProviderIds)
                .Concat(BodyId(body) is { } id ? [id] : []).Distinct().Take(8).ToArray(),
                FinishReason = streamed.FinishReason ?? ReadFinishReason(body),
                ServiceError = error is null ? null : new(kind, status) };
        try
        {
            using var response = await SendWithRedirectsAsync(endpoint ?? new Uri(settings.Endpoint), apiKey,
                payload is null ? null : JsonSerializer.Serialize(payload), method ?? HttpMethod.Post, token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            headerIds = new[] { "x-oneapi-request-id", "x-request-id", "request-id" }.SelectMany(name => response.Headers.TryGetValues(name, out var values)
                ? values : []).Select(AiUsageRequest.SafeId).OfType<string>().Distinct().Take(6).ToArray();
            if (headerIds.Length > 0) idsObserved?.Invoke(headerIds);
            if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await using var events = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var body = await streamed.ReadAsync(events, token).ConfigureAwait(false);
                return Reply(body, null, streamed.Usage, true);
            }
            const int limit = 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) return Reply(null, "AI 响应超过大小限制。", kind: "size");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit) return Reply(null, "AI 响应超过大小限制。", kind: "size");
                buffer.Write(bytes, 0, read);
            }
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            // Some relays label a complete SSE body as JSON/plain text. Parse the same
            // protocol once, without replaying the paid request or guessing JSON actions.
            var prefix = text.AsSpan().TrimStart("\uFEFF \r\n\t");
            if (response.IsSuccessStatusCode && (prefix.StartsWith("data:") || prefix.StartsWith(":") || prefix.StartsWith("event:")))
            {
                buffer.Position = 0;
                return Reply(await streamed.ReadAsync(buffer, token).ConfigureAwait(false), null, streamed.Usage, true);
            }
            return Reply(text, response.IsSuccessStatusCode ? null : HttpProblem((int)response.StatusCode, method == HttpMethod.Get),
                AiTokenUsage.Read(text), kind: "http");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && streamed.Usage is not null)
        { return Reply(null, "已取消请求；保留服务已回报的用量。", streamed.Usage, kind: "cancelled"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Reply(null, "AI 服务或网络连接中断。", streamed.Usage, kind: "network"); }
        catch (HttpRequestException ex) { return Reply(null, AiTransportError.Describe(ex), streamed.Usage, kind: "network"); }
        catch (IOException) { return Reply(null, "读取 AI 响应失败，请重试。", streamed.Usage, kind: "read"); }
        catch (InvalidDataException ex)
        {
            Diagnostics.DiagnosticEventLog.Current?.Record("ai-response", "validation-failed", ex.Message);
            return Reply(null, ex.Message, streamed.Usage,
                streaming: streamed.Started,
                kind: streamed.SizeLimitExceeded ? "size" : status is null ? "redirect"
                    : streamed.FinishReason == "length" ? "output-limit"
                    : streamed.Incomplete ? "incomplete" : "format");
        }
        catch (JsonException) { return Reply(null, "AI 流式响应格式无效。", streamed.Usage); }
        catch (DecoderFallbackException) { return Reply(null, "AI 流式响应编码无效。", streamed.Usage); }
    }

    private static string HttpProblem(int status, bool listingModels) => $"HTTP {status}：" + (status switch
    {
        401 => "密钥未通过认证，请检查密钥是否正确或已失效。",
        403 => "服务拒绝访问，请检查密钥权限、服务地区或账户限制。",
        404 or 405 when listingModels => "此地址不提供模型列表，请核对 API 地址；若服务不支持列表，可手动填写模型。",
        404 => "接口或模型不存在，请核对 API 地址和模型名称。",
        400 or 422 => "服务不接受请求参数，请核对模型名称与接口兼容性。",
        429 => "服务限流或额度不足，请检查账户额度并稍后重试。",
        >= 500 => "服务端异常，请稍后重试或联系服务商。",
        _ => "服务拒绝请求，请核对服务配置。"
    });

    internal static string? BodyId(string? body)
    {
        if (body is null) return null;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String ? AiUsageRequest.SafeId(id.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(Uri endpoint, string? apiKey, string? payload, HttpMethod method, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { endpoint.AbsoluteUri };
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, endpoint)
            {
                Content = payload is null ? null : new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308)) return response;
            try
            {
                // Only 307/308 preserve POST semantics. Never forward credentials to a different origin.
                if (method != HttpMethod.Get && code is not (307 or 308)) throw new InvalidDataException($"AI 服务返回 HTTP {code} 重定向，请填写完整的聊天接口地址（通常为 /v1/chat/completions）。");
                var location = response.Headers.Location;
                if (location is null || !Uri.TryCreate(endpoint, location, out var next))
                    throw new InvalidDataException($"AI 服务返回 HTTP {code}，但缺少有效的跳转地址。");
                if (next.Scheme != endpoint.Scheme || next.IdnHost != endpoint.IdnHost || next.Port != endpoint.Port || next.UserInfo.Length > 0)
                    throw new InvalidDataException($"AI 服务返回 HTTP {code} 并跳转到其他站点或协议；为保护密钥已停止。请向服务商确认最终聊天接口地址后手动填写。");
                if (hop >= 3 || !visited.Add(next.AbsoluteUri))
                    throw new InvalidDataException("AI 接口重定向循环或次数过多，请检查完整接口地址。");
                endpoint = next;
            }
            finally { response.Dispose(); }
        }
    }

    public static string? ConfigurationProblem(AiSettings settings, bool requireModel = true) => settings.Provider == AiProviderKind.LocalModel
        ? "本地模型接口尚未开放，请选择兼容 OpenAI API 的服务。"
        : string.IsNullOrWhiteSpace(settings.Endpoint) || requireModel && string.IsNullOrWhiteSpace(settings.Model)
            ? "请先填写 AI 地址和模型名称。" : null;

    private static string? ReadFinishReason(string? body)
    {
        if (body is null) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string ExtractContent(string body, out bool incomplete)
    {
        incomplete = false;
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AI 服务返回结构无效。");
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            if (choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AI 服务未返回消息。");
            if (choices[0].TryGetProperty("finish_reason", out var finish)
                && (finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop"))
            {
                incomplete = true;
                throw new InvalidDataException("AI 回复未正常完成，未接受排序方案；请检查输出上限或服务支持情况。");
            }
            return AiChatCompatibility.Content(message);
        }
        if (root.TryGetProperty("actions", out _)) return body;
        throw new InvalidDataException("AI 服务未返回模型内容。");
    }

    public void Dispose() { if (ownsClient) httpClient.Dispose(); }

    private static AiClientResult Refused(string message) => new(false, message, null, null);
    private static string Trim(string value) => value.Length <= 256 * 1024 ? value : value[..(256 * 1024)];
}
