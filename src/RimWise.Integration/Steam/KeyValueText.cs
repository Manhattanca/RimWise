using System.Text;

namespace RimWise.Integration.Steam;

/// <summary>A limited, read-only parser for Steam text metadata, not a full VDF implementation.</summary>
internal sealed class KeyValueText
{
    private readonly List<(string Key, string? Value, KeyValueText? Children)> entries = [];
    public string? Value(string key) => entries.FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    public KeyValueText? Block(string key) => entries.FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Children;

    public IEnumerable<string> LibraryPaths()
    {
        foreach (var entry in entries)
        {
            if (!int.TryParse(entry.Key, out _)) continue;
            var path = entry.Children?.Value("path") ?? entry.Value;
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }
    }

    public static KeyValueText Parse(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Steam 元数据过大。");
        return new Parser(text).ReadBlock(0, expectClosingBrace: false);
    }

    private sealed class Parser(string text)
    {
        private int offset;

        public KeyValueText ReadBlock(int depth, bool expectClosingBrace)
        {
            if (depth > 24) throw new InvalidDataException("Steam 元数据嵌套过深。");
            var result = new KeyValueText();
            while (Next() is { } key)
            {
                if (key is "}")
                {
                    if (!expectClosingBrace) throw new InvalidDataException("Steam 元数据括号不匹配。");
                    return result;
                }
                if (key is "{") throw new InvalidDataException("Steam 元数据缺少键。");
                var value = Next() ?? throw new InvalidDataException("Steam 元数据缺少值。");
                if (value == "{") result.entries.Add((key, null, ReadBlock(depth + 1, true)));
                else if (value == "}") throw new InvalidDataException("Steam 元数据缺少值。");
                else result.entries.Add((key, value, null));
            }
            if (expectClosingBrace) throw new InvalidDataException("Steam 元数据缺少结束括号。");
            return result;
        }

        private string? Next()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset]) || text[offset] == '\uFEFF') { offset++; continue; }
                if (text[offset] == '/' && offset + 1 < text.Length && text[offset + 1] == '/')
                {
                    while (offset < text.Length && text[offset] != '\n') offset++;
                    continue;
                }
                break;
            }
            if (offset == text.Length) return null;
            var first = text[offset++];
            if (first is '{' or '}') return first.ToString();
            var result = new StringBuilder();
            if (first == '"')
            {
                while (offset < text.Length)
                {
                    var current = text[offset++];
                    if (current == '"') return result.ToString();
                    if (current == '\\' && offset < text.Length && text[offset] is '\\' or '"')
                        current = text[offset++];
                    result.Append(current);
                }
                throw new InvalidDataException("Steam 元数据字符串未闭合。");
            }
            result.Append(first);
            while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] is not '{' and not '}')
                result.Append(text[offset++]);
            return result.ToString();
        }
    }
}
