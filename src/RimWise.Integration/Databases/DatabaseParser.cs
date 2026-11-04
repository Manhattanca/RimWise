using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimWise.Core;

namespace RimWise.Integration.Databases;

/// <summary>Bounded parser shared by bundled-data generation and runtime updates.</summary>
internal static class DatabaseParser
{
    internal const int MaximumSnapshotBytes = 24 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 24 };
    internal static DatabaseSnapshot ParseSources(byte[] community, string communityCommit, byte[] workshop, string workshopCommit)
    {
        if (community.Length > 4 * 1024 * 1024 || workshop.Length > 64 * 1024 * 1024) throw new InvalidDataException("数据库超过大小限制。");
        using var rulesJson = JsonDocument.Parse(community, new() { MaxDepth = 24 });
        using var steamJson = JsonDocument.Parse(workshop, new() { MaxDepth = 24 });
        Unique(rulesJson.RootElement); Unique(steamJson.RootElement);
        var rules = Required(rulesJson.RootElement, "rules", JsonValueKind.Object);
        var items = Required(steamJson.RootElement, "database", JsonValueKind.Object);
        if (rules.ValueKind != JsonValueKind.Object || items.ValueKind != JsonValueKind.Object) throw new InvalidDataException("数据库结构不受支持。");
        var communityRules = new List<CommunityRule>();
        foreach (var property in rules.EnumerateObject())
        {
            var value = property.Value;
            Object(value);
            communityRules.Add(new(Package(property.Name), Targets(value, "loadBefore"), Targets(value, "loadAfter"),
                Targets(value, "incompatibleWith"), Flag(value, "loadTop"), Flag(value, "loadBottom")));
            if (communityRules.Count > 20_000) throw new InvalidDataException("社区规则过多。");
        }
        var entries = new List<WorkshopEntry>();
        var count = 0;
        foreach (var property in items.EnumerateObject())
        {
            if (++count > 200_000) throw new InvalidDataException("工坊记录过多。");
            Number(property.Name);
            var value = property.Value;
            Object(value);
            var package = value.TryGetProperty("packageId", out var modern) ? modern
                : value.TryGetProperty("packageid", out var legacy) ? legacy : default;
            if (package.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            if (package.ValueKind != JsonValueKind.String) throw new InvalidDataException("工坊包名结构无效。");
            var id = package.GetString()!;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var versions = SourceVersions(value);
            string[]? dependencies = null;
            if (value.TryGetProperty("dependencies", out var required))
            {
                dependencies = Object(required).EnumerateObject().Select(p => Number(p.Name)).Distinct().Order(StringComparer.Ordinal).ToArray();
                if (dependencies.Length > 1000) throw new InvalidDataException("工坊必要前置过多。");
            }
            entries.Add(new(property.Name, Package(id), versions, Boolean(value, "appid"), Boolean(value, "unpublished"))
                { Dependencies = dependencies });
        }
        var document = new DatabaseDocument(1, communityCommit, Hash(community), workshopCommit, Hash(workshop),
            communityRules.OrderBy(r => r.PackageId, StringComparer.OrdinalIgnoreCase).ToArray(),
            entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray()) { RequiredItemsParsed = true };
        Validate(document);
        return Create(document);
    }

    internal static byte[] Compress(DatabaseSnapshot snapshot)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
            JsonSerializer.Serialize(gzip, snapshot.Document, Json);
        return output.ToArray();
    }
    internal static DatabaseSnapshot Empty() => WithOrigins(ParseSources("{\"rules\":{}}"u8.ToArray(), new('0', 40),
        "{\"database\":{}}"u8.ToArray(), new('0', 40)), "不使用", "不使用");
    internal static DatabaseSnapshot WithOrigins(DatabaseSnapshot snapshot, string community, string workshop)
        => Create(snapshot.Document with { CommunityOrigin = community, WorkshopOrigin = workshop });
    internal static DatabaseSnapshot Combine(DatabaseSnapshot community, DatabaseSnapshot workshop)
        => Create(community.Document with { Workshop = workshop.Document.Workshop,
            RequiredItemsParsed = workshop.Document.RequiredItemsParsed,
            WorkshopCommit = workshop.WorkshopCommit, WorkshopHash = workshop.Document.WorkshopHash,
            CommunityOrigin = community.Document.CommunityOrigin, WorkshopOrigin = workshop.Document.WorkshopOrigin });
    internal static DatabaseSnapshot Read(Stream compressed, string? expectedId = null)
    {
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, true);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = gzip.Read(buffer)) > 0)
        {
            if (output.Length + count > MaximumSnapshotBytes) throw new InvalidDataException("数据库索引解压超限。");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        using (var json = JsonDocument.Parse(bytes, new() { MaxDepth = 24 })) Unique(json.RootElement);
        var document = JsonSerializer.Deserialize<DatabaseDocument>(bytes, Json) ?? throw new InvalidDataException("数据库索引为空。");
        Validate(document);
        var snapshot = Create(document);
        if (expectedId is not null && !snapshot.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("数据库索引哈希不匹配。");
        return snapshot;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static DatabaseSnapshot Create(DatabaseDocument document) => new(document, Hash(JsonSerializer.SerializeToUtf8Bytes(document, Json)));
    private static void Validate(DatabaseDocument d)
    {
        if (d.Schema != 1 || !Hex(d.CommunityCommit, 40) || !Hex(d.WorkshopCommit, 40) || !Hex(d.CommunityHash, 64)
            || !Hex(d.WorkshopHash, 64) || d.Community is null || d.Workshop is null
            || d.Community.Length > 20_000 || d.Workshop.Length > 200_000
            || new[] { d.CommunityOrigin, d.WorkshopOrigin }.Any(s => s is not null && (s.Length > 4096 || s.Any(char.IsControl))))
            throw new InvalidDataException("数据库来源或索引无效。");
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in d.Community)
        {
            if (r is null || !packages.Add(Package(r.PackageId)) || r.Top && r.Bottom) throw new InvalidDataException("社区规则重复或矛盾。");
            foreach (var array in new[] { r.Before, r.After, r.Incompatible })
            {
                if (array is null || array.Length > 20_000) throw new InvalidDataException("社区关系超限。");
                foreach (var item in array) Package(item);
            }
        }
        foreach (var e in d.Workshop)
        {
            if (e is null || !ids.Add(Number(e.Id)) || e.Versions is null || e.Versions.Length > 100) throw new InvalidDataException("工坊索引重复或超限。");
            Package(e.PackageId); foreach (var v in e.Versions) Version(v);
            if (e.Dependencies is { } deps)
            {
                if (deps.Length > 1000 || deps.Distinct(StringComparer.Ordinal).Count() != deps.Length)
                    throw new InvalidDataException("工坊前置编号重复或超限。");
                foreach (var dep in deps) Number(dep);
            }
        }
    }
    internal static bool Hex(string? value, int length) => value?.Length == length && value.All(char.IsAsciiHexDigit);
    private static string Package(string? text) => text is { Length: > 0 and <= 256 } && text == text.Trim() && !text.Any(char.IsControl)
        ? text : throw new InvalidDataException("数据库包名无效。");
    private static string Number(string? text) => text is { Length: > 0 and <= 20 } && text.All(char.IsAsciiDigit) && ulong.TryParse(text, out var id) && id > 0
        ? text : throw new InvalidDataException("数据库工坊编号无效。");
    private static string Version(string? text) => text == "unknown" || text is { Length: <= 16 } && GameVersion.Parse(text) is { } v && v.Series == text
        ? text : throw new InvalidDataException("数据库游戏版本无效。");
    private static string[] SourceVersions(JsonElement value)
    {
        if (!value.TryGetProperty("gameVersions", out var field)) return [];
        if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() > 100)
            throw new InvalidDataException("数据库游戏版本列表无效。");
        return field.EnumerateArray().Select(item =>
        {
            if (item.ValueKind == JsonValueKind.Null) return "unknown";
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 128 } text
                || text.Any(char.IsControl)) throw new InvalidDataException("数据库游戏版本字段无效。");
            // Historical records include build/revision suffixes and a missing-XML sentinel.
            // Unrecognised declarations remain unknown, never a wildcard or executable text.
            return Regex.IsMatch(text, @"\A\d{1,3}\.\d{1,3}(?:\.\d{1,9})?(?: rev\d{1,9})?\z",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) && GameVersion.Parse(text) is { } version
                ? version.Series : "unknown";
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    private static bool Boolean(JsonElement value, string name)
    {
        Object(value);
        return value.TryGetProperty(name, out var field) && field.ValueKind switch
        { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException("数据库布尔字段无效。") };
    }
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var field) && Boolean(field, "value");
    private static string[] Targets(JsonElement value, string name) => value.TryGetProperty(name, out var field)
        ? Object(field).EnumerateObject().Select(p => Package(p.Name)).ToArray() : [];
    private static JsonElement Object(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value
        : throw new InvalidDataException("数据库对象结构无效。");
    private static JsonElement Required(JsonElement value, string name, JsonValueKind kind) => Object(value).TryGetProperty(name, out var field)
        && field.ValueKind == kind ? field : throw new InvalidDataException("数据库缺少有效字段：" + name);
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("数据库 JSON 字段重复。"); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
}
