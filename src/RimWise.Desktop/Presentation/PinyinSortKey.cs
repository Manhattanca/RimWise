using System.Globalization;
using System.IO;
using System.Text;

namespace RimWise.Desktop.Presentation;

/// <summary>Offline Mandarin keys let Chinese and Latin names share one A–Z list.</summary>
internal static class PinyinSortKey
{
    private static readonly Lazy<Tables> Data = new(Load);

    internal static string Create(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var data = Data.Value;
        var result = new StringBuilder(normalized.Length);
        for (var offset = 0; offset < normalized.Length;)
        {
            if (!Rune.TryGetRuneAt(normalized, offset, out var rune))
            {
                result.Append(normalized[offset++]);
                continue;
            }
            var matchedLength = 0;
            if (data.PhraseLengths.TryGetValue(rune.Value, out var lengths))
                foreach (var length in lengths)
                    if (length <= normalized.Length - offset
                        && data.Phrases.TryGetValue(normalized.Substring(offset, length), out var reading))
                    {
                        result.Append(reading);
                        matchedLength = length;
                        break;
                    }
            if (matchedLength > 0) { offset += matchedLength; continue; }
            result.Append(data.Characters.TryGetValue(rune.Value, out var pinyin) ? pinyin : rune.ToString());
            offset += rune.Utf16SequenceLength;
        }
        return result.ToString();
    }

    private static Tables Load()
    {
        var characters = Lines("Characters").ToDictionary(parts => int.Parse(parts[0], NumberStyles.HexNumber,
            CultureInfo.InvariantCulture), parts => parts[1]);
        var phrases = Lines("Phrases").ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        var lengths = phrases.Keys.GroupBy(phrase => Rune.GetRuneAt(phrase, 0).Value)
            .ToDictionary(group => group.Key, group => group.Select(phrase => phrase.Length).Distinct().OrderDescending().ToArray());
        return new(characters, phrases, lengths);
    }

    private static IEnumerable<string[]> Lines(string name)
    {
        using var input = typeof(PinyinSortKey).Assembly.GetManifestResourceStream("RimWise.Pinyin." + name)
            ?? throw new InvalidDataException("拼音排序数据缺失。");
        using var reader = new StreamReader(input, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && !line.StartsWith('#')) yield return line.Split('\t', 2);
    }

    private sealed record Tables(Dictionary<int, string> Characters, Dictionary<string, string> Phrases,
        Dictionary<int, int[]> PhraseLengths);
}
