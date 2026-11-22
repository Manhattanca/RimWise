using System.Text;
using System.Text.Json;
using RimWise.Core;

namespace RimWise.Integration.AI;

/// <summary>Chat Completions wire conventions; never converts reasoning or tool calls into actions.</summary>
internal static class AiChatCompatibility
{
    internal static bool IsDeepSeek(string model) => ModelName(model).StartsWith("deepseek-", StringComparison.Ordinal);
    private static string ModelName(string model) => model[(model.LastIndexOf('/') + 1)..].ToLowerInvariant();
    internal static string Provider(AiSettings settings) => new Uri(settings.Endpoint).IdnHost switch
    {
        "api.siliconflow.cn" or "api.siliconflow.com" => "siliconflow",
        "api.deepseek.com" => "deepseek",
        _ => "compatible"
    };

    internal static void Configure(Dictionary<string, object> payload, AiSettings settings, int? limit)
    {
        var model = ModelName(settings.Model);
        var host = new Uri(settings.Endpoint).IdnHost;
        // Most compatible providers still require max_tokens. Only OpenAI model families
        // use the newer parameter, including when reached through a compatible relay.
        var openAiModel = model.StartsWith("gpt-") || model.StartsWith("chatgpt-")
            || model.Length >= 2 && model[0] == 'o' && char.IsAsciiDigit(model[1]);
        if (limit is { } value)
            payload[host == "api.openai.com" || openAiModel ? "max_completion_tokens" : "max_tokens"] = value;
        // Omit optional sampling settings: several reasoning models reject temperature=0.
        // Do not assume a relay's implicit thinking mode. Request the documented
        // DeepSeek mode explicitly only for the endpoint and exact models exercised
        // in live development acceptance, not arbitrary compatible providers.
        if (host == "relayrax.com" && model is "deepseek-v4-pro" or "deepseek-v4.1-flash")
        {
            payload["thinking"] = new { type = "enabled" };
            payload["reasoning_effort"] = "high";
            payload["response_format"] = new { type = "json_object" };
        }
        // Current official DeepSeek defaults to high reasoning. Keep thinking enabled but
        // reserve more of the bounded output allowance for an actual, structured proposal.
        if (host == "api.deepseek.com" && (model == "deepseek-flash" || model.StartsWith("deepseek-v4")))
        {
            payload["reasoning_effort"] = "low";
            payload["response_format"] = new { type = "json_object" };
        }
        if (Provider(settings) == "siliconflow" && IsDeepSeek(settings.Model))
        {
            // SiliconFlow max_tokens excludes reasoning; V4 low/medium map to high.
            // Keep both portions within our total allowance without a paid retry.
            if (model.StartsWith("deepseek-v4") || model.StartsWith("deepseek-v3.1") || model.StartsWith("deepseek-v3.2"))
            {
                var thinking = limit is null ? 4096 : limit >= 256 ? Math.Min(8192, limit.Value / 2) : 0;
                payload["enable_thinking"] = thinking > 0;
                if (thinking > 0) payload["thinking_budget"] = thinking;
                if (limit is { } total) payload["max_tokens"] = total - thinking;
            }
            // R1/distill endpoints do not all support JSON mode. Unknown relays
            // retain the conservative common Chat Completions request.
            if (model.StartsWith("deepseek-v3") || model.StartsWith("deepseek-v4"))
                payload["response_format"] = new { type = "json_object" };
        }
    }

    internal static string Content(JsonElement message)
    {
        if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null
            && (refusal.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(refusal.GetString())))
            throw new InvalidDataException("AI 服务拒绝生成此回复，未接受排序方案。");
        foreach (var name in new[] { "tool_calls", "function_call" })
            if (message.TryGetProperty(name, out var calls) && calls.ValueKind != JsonValueKind.Null
                && !(calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 0))
                throw new InvalidDataException("AI 返回了工具调用；当前接口只接受最终排序 JSON。");
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) throw new InvalidDataException("AI 正文类型无效。");
        var result = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String) throw new InvalidDataException("AI 正文分段结构无效。");
            if (type.GetString() is "thinking" or "reasoning" or "redacted_thinking") continue;
            if (type.GetString() is not ("text" or "output_text") || !part.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String) throw new InvalidDataException("AI 回复包含不支持的正文分段。");
            result.Append(text.GetString());
        }
        return result.ToString();
    }

    internal static string SafeFinish(string? reason) => reason switch
    {
        null => "missing",
        "stop" or "length" or "content_filter" or "tool_calls" or "function_call"
            or "insufficient_system_resource" or "aborted" or "error" => reason,
        _ => "other"
    };

    internal static IReadOnlyList<AiAction> NormalizePlanActions(string model, IReadOnlyList<AiAction> actions,
        IReadOnlyList<string> order, ModCatalogue catalogue, GameVersion? version)
    {
        if (!IsDeepSeek(model) || actions.Count == 0 || version is null) return actions;
        var active = order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ILookup<ModInstallation, string>? byInstallation = null;
        return actions.Select(action =>
        {
            // DeepSeek may copy a raw metadata PackageId instead of the order identifier.
            // Only reconcile a move if both names resolve to the same unique active copy.
            if (action.NormalizedKind is not ("move" or "reorder") || action.PackageId?.Trim() is not { } id
                || !AiModIdentifier.IsValid(id) || active.Contains(id) || catalogue.FindPackage(id) is not { Count: 1 } matches)
                return action;
            byInstallation ??= order.Select(key => (key, mod: catalogue.Resolve(key, version).UniqueInstallation))
                .Where(match => match.mod is not null).ToLookup(match => match.mod!, match => match.key);
            var aliases = byInstallation[matches[0]].Take(2).ToArray();
            return aliases.Length == 1 ? action with { PackageId = aliases[0] } : action;
        }).ToArray();
    }
}
