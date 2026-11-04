using System.Text;
using System.Text.Json;

namespace RimWise.Integration.AI;

/// <summary>Public model output and provider usage only. Hidden reasoning is never displayed.</summary>
public sealed record AiResponseUpdate(string ContentDelta = "", AiTokenUsage? Usage = null);

/// <summary>Bounded SSE reader. A partial stream can report usage, but cannot authorize actions.</summary>
internal sealed class AiStreamingResponse(IProgress<AiResponseUpdate>? progress)
{
    // Bound retained data, not total wire traffic: SSE repeats envelopes and may
    // carry many reasoning/keepalive events before the final answer and usage.
    private const int RetainedCharacterLimit = 1024 * 1024;
    internal bool SizeLimitExceeded { get; private set; }
    internal bool Started { get; private set; }
    internal bool Incomplete { get; private set; }
    internal string? FinishReason { get; private set; }
    internal AiTokenUsage? Usage { get; private set; }
    internal HashSet<string> ProviderIds { get; } = [];
    private readonly StringBuilder content = new();
    private readonly StringBuilder line = new();
    private readonly StringBuilder frame = new();
    private readonly AiFinalAnswer publicAnswer = new();
    private bool done;
    private bool finished;
    private bool truncated;
    private bool usageUnreliable;
    private bool firstCharacter = true;
    private bool previousCarriageReturn;

    internal async Task<string> ReadAsync(Stream stream, CancellationToken token)
    {
        Started = true;
        var bytes = new byte[8192];
        var chars = new char[8192];
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        int read;
        while (!done && (read = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        {
            var count = decoder.GetChars(bytes, 0, read, chars, 0);
            for (var i = 0; i < count && !done; i++)
            {
                var ch = chars[i];
                if (firstCharacter) { firstCharacter = false; if (ch == '\uFEFF') continue; }
                if (ch == '\n' && previousCarriageReturn) { previousCarriageReturn = false; continue; }
                previousCarriageReturn = ch == '\r';
                if (ch is '\r' or '\n') { ReadLine(line.ToString()); line.Clear(); }
                else
                {
                    CheckSize(line.Length, 1);
                    line.Append(ch);
                }
            }
        }
        if (!done)
        {
            // A clean EOF can follow an explicit stop without [DONE]. Flush the last
            // event as well: relays sometimes omit its final blank line. Transport
            // exceptions and malformed/unfinished JSON still fail before this point.
            decoder.GetChars([], 0, 0, chars, 0, flush: true);
            if (line.Length > 0) { ReadLine(line.ToString()); line.Clear(); }
            if (frame.Length > 0) { ReadFrame(frame.ToString()); frame.Clear(); }
        }
        Incomplete = !finished || truncated;
        Diagnostics.DiagnosticEventLog.Current?.Record("ai-response", "stream-end",
            $"done={done}; finished={finished}; finish={AiChatCompatibility.SafeFinish(FinishReason)}; characters={content.Length}; usageReliable={!usageUnreliable}");
        if (Incomplete) throw new InvalidDataException(FinishReason == "length"
            ? "AI 已达到本次输出额度，最终方案未完成。"
            : "AI 回报缺少正常结束信号或被服务中止；保留已返回用量，不应用部分方案。");
        return JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = content.ToString() } } },
            usage = Usage is null ? null : new { prompt_tokens = Usage.Input, completion_tokens = Usage.Output,
                total_tokens = Usage.Total, prompt_tokens_details = new { cached_tokens = Usage.CachedInput },
                completion_tokens_details = new { reasoning_tokens = Usage.ReasoningOutput } }
        });
    }

    private void ReadLine(string value)
    {
        if (value.Length == 0)
        {
            if (frame.Length > 0) { ReadFrame(frame.ToString()); frame.Clear(); }
        }
        else if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            var data = value.AsSpan(5).TrimStart(' ');
            CheckSize(frame.Length, data.Length + (frame.Length > 0 ? 1 : 0));
            if (frame.Length > 0) frame.Append('\n');
            frame.Append(data);
        }
        // SSE comments and event/id/retry fields do not contain model output.
    }

    private void ReadFrame(string data)
    {
        if (data == "[DONE]") { done = true; return; }
        using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AI 流式响应结构无效。");
        if (AiClient.BodyId(data) is { } responseId && ProviderIds.Count < 2) ProviderIds.Add(responseId);
        if (root.TryGetProperty("usage", out var reported) && reported.ValueKind != JsonValueKind.Null)
        {
            var usage = AiTokenUsage.Read(data);
            // Accounting does not authorize model actions. Missing, malformed or
            // conflicting counters must not discard an otherwise complete final answer.
            if (usage is null || Usage is not null && !CanAdvanceUsage(Usage, usage))
            {
                usageUnreliable = true;
                Usage = null;
            }
            else if (!usageUnreliable)
            {
                Usage = usage;
                progress?.Report(new(Usage: usage));
            }
        }
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException("AI 服务报告生成失败。");
        if (!root.TryGetProperty("choices", out var choices) && root.TryGetProperty("usage", out _)) return;
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 1)
            throw new InvalidDataException("AI 流式响应缺少消息。");
        foreach (var choice in choices.EnumerateArray())
        {
            var alreadyFinished = finished;
            if (choice.ValueKind != JsonValueKind.Object || choice.TryGetProperty("index", out var index)
                && (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var number) || number != 0))
                throw new InvalidDataException("AI 流式响应消息编号无效。");
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                // Empty reasons are emitted by some compatible bridges between chunks.
                if (!string.IsNullOrEmpty(reason.GetString()))
                {
                    if (FinishReason is not null && FinishReason != reason.GetString()) truncated = true;
                    FinishReason = reason.GetString();
                    if (FinishReason != "stop") truncated = true;
                    finished = true;
                }
            }
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
            var part = AiChatCompatibility.Content(delta);
            if (alreadyFinished && part.Length > 0)
                throw new InvalidDataException("AI 在结束信号后继续发送正文，未接受不一致的排序方案。");
            CheckSize(content.Length, part.Length);
            content.Append(part);
            var visible = publicAnswer.Append(part);
            if (visible.Length > 0) progress?.Report(new(visible));
        }
    }

    private static bool CanAdvanceUsage(AiTokenUsage previous, AiTokenUsage next) =>
        previous.Input == next.Input && next.Output >= previous.Output
        && (previous.CachedInput is null || next.CachedInput is null || next.CachedInput == previous.CachedInput)
        && (previous.ReasoningOutput is null || next.ReasoningOutput is null || next.ReasoningOutput >= previous.ReasoningOutput);

    private void CheckSize(int retained, int incoming)
    {
        if (incoming <= RetainedCharacterLimit - retained) return;
        SizeLimitExceeded = true;
        throw new InvalidDataException("AI 单条流式消息或累计回复正文过大；未应用不完整方案。");
    }
}
