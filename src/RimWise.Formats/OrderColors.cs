using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace RimWise.Formats;

/// <summary>Optional, portable annotations. Never part of an active-mod identifier.</summary>
public static class OrderColors
{
    public const string JsonProperty = "rimWise";
    private const string CommentPrefix = "RimWise:colors:v1:";
    // Read older exports as well; new exports always use RimWise identifiers.
    internal const string LegacyJsonProperty = "rimSortWorkbench";
    private const string LegacyCommentPrefix = "RimSortWorkbench:colors:v1:";
    public const int MaximumEntries = 20000;
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;

    public static IReadOnlyDictionary<string, string> Normalize(IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            var id = pair.Key?.Trim() ?? "";
            if (id.Length is 0 or > 512 || id.Any(char.IsControl) || !IsColor(pair.Value))
                throw new InvalidDataException("颜色标记的模组标识或颜色无效。");
            if (!result.TryAdd(id, pair.Value.ToUpperInvariant())) throw new InvalidDataException("颜色标记包含重复模组标识。");
            if (result.Count > MaximumEntries) throw new InvalidDataException("颜色标记数量超过上限。");
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    public static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#'
        && value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") == false;

    internal static IReadOnlyDictionary<string, string> ReadXml(XDocument xml)
    {
        var comments = xml.Nodes().OfType<XComment>().Where(c => IsColorComment(c.Value)).ToArray();
        if (comments.Length > 1) throw new InvalidDataException("颜色标记元数据重复。");
        if (comments.Length == 0) return Normalize([]);
        try
        {
            if (comments[0].Value.Length > MaximumMetadataBytes * 2) throw new InvalidDataException("颜色元数据过大。");
            var prefix = comments[0].Value.StartsWith(CommentPrefix, StringComparison.Ordinal) ? CommentPrefix : LegacyCommentPrefix;
            var bytes = Convert.FromBase64String(comments[0].Value[prefix.Length..]);
            if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("颜色元数据过大。");
            return ReadJson(JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("颜色元数据无效。"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new InvalidDataException("颜色标记损坏，原文件未修改。", ex); }
    }

    internal static JsonObject? JsonMetadata(JsonObject root)
    {
        if (root.ContainsKey(JsonProperty) && root.ContainsKey(LegacyJsonProperty))
            throw new InvalidDataException("颜色标记元数据重复。");
        var key = root.ContainsKey(JsonProperty) ? JsonProperty : LegacyJsonProperty;
        if (root.ContainsKey(key) && root[key] is not JsonObject)
            throw new InvalidDataException("颜色标记元数据必须为对象。");
        return root[key] as JsonObject;
    }

    private static bool IsColorComment(string value) => value.StartsWith(CommentPrefix, StringComparison.Ordinal)
        || value.StartsWith(LegacyCommentPrefix, StringComparison.Ordinal);

    internal static IReadOnlyDictionary<string, string> ReadJson(JsonObject metadata)
    {
        try
        {
            if (metadata["colorSchema"]?.GetValue<int>() != 1 || metadata["colors"] is not JsonObject colors)
                throw new InvalidDataException("颜色标记版本或结构不支持。");
            return Normalize(colors.Select(p => new KeyValuePair<string, string>(p.Key, p.Value?.GetValue<string>() ?? "")));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        { throw new InvalidDataException("颜色标记字段类型无效。", ex); }
    }

    internal static JsonObject WriteJson(OrderFileDocument document, JsonObject? original = null)
    {
        var metadata = original is null ? new JsonObject() : (JsonObject)original.DeepClone();
        metadata["colorSchema"] = 1;
        metadata["colors"] = new JsonObject(document.Colors.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value))));
        if (JsonSerializer.SerializeToUtf8Bytes(metadata).Length > MaximumMetadataBytes)
            throw new InvalidDataException("颜色元数据过大。");
        return metadata;
    }

    internal static byte[] WriteXml(byte[] bytes, OrderFileDocument document)
    {
        // A document-level XML comment does not add fields to any game's serialized object.
        // Do not put attributes/elements inside activeMods, ids, or parallel metadata arrays.
        using var input = new MemoryStream(bytes);
        var xml = XDocument.Load(input);
        foreach (var comment in xml.Nodes().OfType<XComment>().Where(c => IsColorComment(c.Value)).ToArray()) comment.Remove();
        if (document.Colors.Count > 0)
            xml.AddFirst(new XComment(CommentPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(WriteJson(document)))));
        using var output = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(output, new() { Encoding = new System.Text.UTF8Encoding(false), Indent = true })) xml.Save(writer);
        return output.ToArray();
    }
}
