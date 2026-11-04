using System.Text;
using System.Text.Json;

namespace RimWise.Integration.AI;

/// <summary>Chat Completions wire conventions; never converts reasoning or tool calls into actions.</summary>
internal static class AiChatCompatibility
{
    internal static void Configure(Dictionary<string, object> payload, AiSettings settings, int? limit)
    {
        var model = settings.Model[(settings.Model.LastIndexOf('/') + 1)..].ToLowerInvariant();
        var host = new Uri(settings.Endpoint).IdnHost;
        // Most compatible providers still require max_tokens. Only OpenAI model families
        // use the newer parameter, including when reached through a compatible relay.
        var openAiModel = model.StartsWith("gpt-") || model.StartsWith("chatgpt-")
            || model.Length >= 2 && model[0] == 'o' && char.IsAsciiDigit(model[1]);
        if (limit is { } value)
            payload[host == "api.openai.com" || openAiModel ? "max_completion_tokens" : "max_tokens"] = value;
        // Omit optional sampling settings: several reasoning models reject temperature=0.
        // Current official DeepSeek defaults to high reasoning. Keep thinking enabled but
        // reserve more of the bounded output allowance for an actual, structured proposal.
        if (host == "api.deepseek.com" && (model == "deepseek-flash" || model.StartsWith("deepseek-v4")))
        {
            payload["reasoning_effort"] = "low";
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
}
