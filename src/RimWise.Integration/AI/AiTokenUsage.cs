using System.Text.Json;

namespace RimWise.Integration.AI;

/// <summary>Provider-reported usage. Cached and reasoning tokens are subsets, not extra charges.</summary>
public sealed record AiTokenUsage(int Input, int Output, int Total, int? CachedInput = null, int? ReasoningOutput = null)
{
    public static AiTokenUsage? Read(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            if (!json.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
            var input = Count(usage, "prompt_tokens") ?? Count(usage, "input_tokens");
            var output = Count(usage, "completion_tokens") ?? Count(usage, "output_tokens");
            if (usage.TryGetProperty("prompt_tokens", out _) && Count(usage, "prompt_tokens") != input
                || usage.TryGetProperty("completion_tokens", out _) && Count(usage, "completion_tokens") != output) return null;
            var total = Count(usage, "total_tokens");
            // Both counters are reported by the provider; deriving their sum is not
            // a tokenizer estimate. An explicit invalid total must remain unknown.
            if (!usage.TryGetProperty("total_tokens", out _) && input is not null && output is not null
                && (long)input + output <= 10_000_000) total = input + output;
            if (input is null || output is null || total is null || total != input + output) return null;
            if (usage.TryGetProperty("input_tokens", out _) && Count(usage, "input_tokens") != input
                || usage.TryGetProperty("output_tokens", out _) && Count(usage, "output_tokens") != output) return null;
            var cached = Detail(usage, "prompt_tokens_details", "cached_tokens") ?? Detail(usage, "input_tokens_details", "cached_tokens")
                ?? Count(usage, "prompt_cache_hit_tokens") ?? Count(usage, "cached_tokens");
            var reasoning = Detail(usage, "completion_tokens_details", "reasoning_tokens") ?? Detail(usage, "output_tokens_details", "reasoning_tokens");
            if (cached > input || reasoning > output) return null;
            return new(input.Value, output.Value, total.Value, cached, reasoning);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public bool Valid => Input >= 0 && Output >= 0 && Total == (long)Input + Output && Total <= 10_000_000
        && (CachedInput is null || CachedInput >= 0 && CachedInput <= Input)
        && (ReasoningOutput is null || ReasoningOutput >= 0 && ReasoningOutput <= Output);

    private static int? Count(JsonElement parent, string name) => parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count is >= 0 and <= 10_000_000 ? count : null;

    private static int? Detail(JsonElement parent, string name, string field) =>
        parent.TryGetProperty(name, out var detail) && detail.ValueKind == JsonValueKind.Object ? Count(detail, field) : null;
}
