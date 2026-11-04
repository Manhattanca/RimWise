using System.Text.Json;
using System.Text.Json.Serialization;
using RimWise.Integration.AI;

namespace RimWise.Integration.Diagnostics;

// Export-only names: keep the existing recovery format unchanged.
internal sealed class DiagnosticTokenUsageConverter : JsonConverter<AiTokenUsage>
{
    public override AiTokenUsage Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, AiTokenUsage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("inputTokens", value.Input);
        writer.WriteNumber("outputTokens", value.Output);
        writer.WriteNumber("totalTokens", value.Total);
        if (value.CachedInput is { } cached) writer.WriteNumber("cachedInputTokens", cached);
        if (value.ReasoningOutput is { } reasoning) writer.WriteNumber("reasoningOutputTokens", reasoning);
        writer.WriteEndObject();
    }
}
