using System.Text;

namespace RimWise.Integration.AI;

internal sealed class AiFinalAnswer
{
    private const string ThinkingStart = "<think>";
    private const string ThinkingEnd = "</think>";
    private readonly StringBuilder pending = new();
    private bool thinking;
    private bool answering;

    internal string Append(string delta)
    {
        if (answering) return delta;
        pending.Append(delta);
        while (true)
        {
            var text = pending.ToString();
            if (thinking)
            {
                var end = text.IndexOf(ThinkingEnd, StringComparison.Ordinal);
                if (end < 0)
                {
                    if (pending.Length > ThinkingEnd.Length - 1)
                        pending.Remove(0, pending.Length - (ThinkingEnd.Length - 1));
                    return "";
                }
                text = text[(end + ThinkingEnd.Length)..];
                thinking = false;
            }
            text = TrimStart(text);
            pending.Clear();
            pending.Append(text);
            if (text.Length == 0 || ThinkingStart.StartsWith(text, StringComparison.Ordinal)) return "";
            if (text.StartsWith(ThinkingStart, StringComparison.Ordinal))
            {
                pending.Remove(0, ThinkingStart.Length);
                thinking = true;
                continue;
            }
            answering = true;
            pending.Clear();
            return text;
        }
    }

    internal static string Normalize(string content)
    {
        content = TrimStart(content).TrimEnd();
        while (content.StartsWith(ThinkingStart, StringComparison.Ordinal))
        {
            var end = content.IndexOf(ThinkingEnd, ThinkingStart.Length, StringComparison.Ordinal);
            if (end < 0) throw new InvalidDataException("AI 推理段未完成，未接受排序方案。");
            content = TrimStart(content[(end + ThinkingEnd.Length)..]).TrimEnd();
        }
        if (content.Length == 0) throw new InvalidDataException("AI 未返回最终 JSON 答案，未接受排序方案。");
        if (!content.StartsWith("```", StringComparison.Ordinal)) return content;
        var newline = content.IndexOf('\n');
        if (newline < 0) throw new InvalidDataException("AI JSON 代码围栏未完成。");
        var opening = content[..newline].TrimEnd();
        if (opening != "```" && !opening.Equals("```json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AI 最终答案必须是 JSON。");
        var closing = content.LastIndexOf('\n');
        if (closing <= newline || content[(closing + 1)..].Trim() != "```")
            throw new InvalidDataException("AI JSON 代码围栏未正常结束。");
        return content[(newline + 1)..closing].Trim();
    }

    private static string TrimStart(string value) => value.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
}
