using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimWise.Core;

namespace RimWise.Integration.Marketplace;

/// <summary>
/// The schema shared by the desktop and trusted catalogue builder. Canonical output is
/// compact UTF-8 without BOM or trailing newline, in the property order written here.
/// SHA-256 always covers exact file bytes; downloaded JSON is never normalized before hashing.
/// </summary>
public static class MarketplaceJson
{
    public const int MaximumCatalogueBytes = 2 * 1024 * 1024;
    public const int MaximumManifestBytes = 1024 * 1024;
    public const int MaximumEntries = 2000;
    public const int MaximumMods = 10000;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    public static MarketplaceManifest ParseManifest(ReadOnlyMemory<byte> json)
    {
        var manifest = Parse<MarketplaceManifest>(json, MaximumManifestBytes);
        ValidateManifest(manifest);
        return manifest with
        {
            RequiredDlc = Array.AsReadOnly(manifest.RequiredDlc.ToArray()),
            Mods = Array.AsReadOnly(manifest.Mods.ToArray()), Images = Array.AsReadOnly(manifest.Images.ToArray())
        };
    }

    public static MarketplaceCatalogue ParseCatalogue(ReadOnlyMemory<byte> json)
    {
        var catalogue = Parse<MarketplaceCatalogue>(json, MaximumCatalogueBytes);
        ValidateCatalogue(catalogue);
        return catalogue with
        {
            Entries = Array.AsReadOnly(catalogue.Entries.Select(e => e with
            {
                RequiredDlc = Array.AsReadOnly(e.RequiredDlc.ToArray()), Images = Array.AsReadOnly(e.Images.ToArray())
            }).ToArray())
        };
    }

    public static void ValidateManifest(MarketplaceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        MarketplaceImages.Validate(manifest.Author, manifest.Id, manifest.Version, manifest.Images);
        Require(manifest.SchemaVersion == 1, "Unsupported marketplace manifest schemaVersion; expected 1.");
        Metadata(manifest.Id, manifest.Version, manifest.Title, manifest.Author, manifest.Description,
            manifest.GameVersion, manifest.RequiredDlc, manifest.WorkshopCollectionId);
        Require(manifest.ReleaseNotes is not null && manifest.ReleaseNotes.Length <= 4000 &&
            manifest.ReleaseNotes.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t'), "Invalid releaseNotes (maximum 4000 characters).");
        Require(manifest.Mods is { Count: > 0 and <= MaximumMods }, "Manifest must contain an ordered, bounded mods list.");
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workshops = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in manifest.Mods)
        {
            Require(mod is not null, "Null mod entry.");
            Require(IsPackageId(mod!.PackageId), "Invalid packageId.");
            Require(packages.Add(mod.PackageId), "Duplicate packageId in manifest.");
            var official = GameContentPolicy.KeepWhenClearing(mod.PackageId, new GameVersion(1, 6));
            // Workshop hints are optional. Hosting an order must not require a local scan or an accessible Workshop entry.
            if (mod.WorkshopId is not null)
            {
                Require(!official, "Official game content must not specify a workshopId.");
                Require(IsWorkshopId(mod.WorkshopId), "workshopId must be a positive ASCII decimal string.");
                Require(workshops.Add(mod.WorkshopId), "Duplicate workshopId in manifest.");
            }
        }
        var listedDlc = manifest.Mods.Where(m => IsOfficialDlc(m.PackageId)).Select(m => m.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(listedDlc.SetEquals(manifest.RequiredDlc), "requiredDlc must exactly match the official DLC present in the ordered mods list.");
    }

    public static void ValidateCatalogue(MarketplaceCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        Require(catalogue.SchemaVersion == 1, "Unsupported marketplace catalogue schemaVersion; expected 1.");
        Require(catalogue.Entries is { Count: <= MaximumEntries }, "Missing or oversized catalogue entries.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalogue.Entries)
        {
            Require(entry is not null, "Null catalogue entry.");
            ValidateEntry(entry!);
            if (idOwners.TryGetValue(entry!.Id, out var owner))
                Require(owner.Equals(entry.Author, StringComparison.OrdinalIgnoreCase), "此排序标识已被其他作者使用。");
            else idOwners.Add(entry.Id, entry.Author);
            Require(identities.Add(Identity(entry!)), "Duplicate author/id/version in catalogue.");
            Require(paths.Add(entry!.ManifestPath), "Duplicate manifest path in catalogue.");
            Require(hashes.Add(entry.Sha256), "Duplicate manifest SHA-256 in catalogue.");
        }
    }

    public static byte[] SerializeManifest(MarketplaceManifest manifest)
    {
        ValidateManifest(manifest);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            WriteMetadata(writer, manifest.Id, manifest.Version, manifest.Title, manifest.Author,
                manifest.Description, manifest.GameVersion, manifest.RequiredDlc, manifest.WorkshopCollectionId);
            writer.WriteStartArray("mods");
            foreach (var mod in manifest.Mods)
            {
                writer.WriteStartObject();
                writer.WriteString("packageId", mod.PackageId);
                writer.WriteString("workshopId", mod.WorkshopId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (manifest.ReleaseNotes.Length > 0) writer.WriteString("releaseNotes", manifest.ReleaseNotes);
            WriteImages(writer, manifest.Images);
            writer.WriteEndObject();
        }, MaximumManifestBytes);
    }

    public static byte[] SerializeCatalogue(MarketplaceCatalogue catalogue)
    {
        ValidateCatalogue(catalogue);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("entries");
            foreach (var entry in catalogue.Entries)
            {
                writer.WriteStartObject();
                WriteMetadata(writer, entry.Id, entry.Version, entry.Title, entry.Author,
                    entry.Description, entry.GameVersion, entry.RequiredDlc, entry.WorkshopCollectionId);
                writer.WriteString("manifestPath", entry.ManifestPath);
                writer.WriteString("sha256", entry.Sha256);
                if (entry.ModCount is { } count) writer.WriteNumber("modCount", count);
                if (entry.ReleaseNotes.Length > 0) writer.WriteString("releaseNotes", entry.ReleaseNotes);
                WriteImages(writer, entry.Images);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }, MaximumCatalogueBytes);
    }

    public static MarketplaceCatalogue CreateCatalogue(IEnumerable<MarketplaceManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var entries = new List<MarketplaceEntry>();
        foreach (var input in manifests)
        {
            Require(entries.Count < MaximumEntries, "Too many marketplace entries.");
            var bytes = SerializeManifest(input);
            var manifest = ParseManifest(bytes);
            entries.Add(new MarketplaceEntry(manifest.Id, manifest.Version, manifest.Title, manifest.Author,
                manifest.Description, manifest.GameVersion, manifest.RequiredDlc, manifest.WorkshopCollectionId,
                GetManifestPath(manifest.Author, manifest.Id, manifest.Version), ComputeSha256(bytes))
                { ModCount = manifest.Mods.Count, ReleaseNotes = manifest.ReleaseNotes, Images = manifest.Images });
        }
        var catalogue = new MarketplaceCatalogue(1, Array.AsReadOnly(entries
            .OrderBy(e => e.Author, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal).ThenBy(e => e.Version, StringComparer.Ordinal).ToArray()));
        // Also enforce the serialized byte bound, not just the count bound.
        _ = SerializeCatalogue(catalogue);
        return catalogue;
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string GenerateUniqueId(IEnumerable<string> existingIds, Func<string>? candidateFactory = null)
    {
        ArgumentNullException.ThrowIfNull(existingIds);
        var existing = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        candidateFactory ??= static () => "rw-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var candidate = candidateFactory();
            Require(IsId(candidate), "生成的排序标识格式无效。");
            if (!existing.Contains(candidate)) return candidate;
        }
        throw new MarketplaceException("多次生成的排序标识均与市场目录重复，请重试。");
    }

    public static string GetManifestPath(string author, string id, string version)
    {
        Require(IsLogin(author), "作者账号格式无效。");
        Require(IsId(id), "Invalid immutable marketplace id (use lowercase ASCII letters, digits and hyphens).");
        Require(IsVersion(version), "Invalid immutable manifest version (expected major.minor.patch with optional prerelease).");
        return $"marketplace/manifests/{author.ToLowerInvariant()}/{id}/{version}.json";
    }

    internal static void ValidateEntry(MarketplaceEntry entry)
    {
        MarketplaceImages.Validate(entry.Author, entry.Id, entry.Version, entry.Images);
        Require(entry.ModCount is null or > 0 and <= MaximumMods, "排序目录中的模组数无效。");
        Require(entry.ReleaseNotes is not null && entry.ReleaseNotes.Length <= 4000 &&
            entry.ReleaseNotes.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t'), "排序目录中的更新日志无效。");
        Metadata(entry.Id, entry.Version, entry.Title, entry.Author, entry.Description,
            entry.GameVersion, entry.RequiredDlc, entry.WorkshopCollectionId);
        Require(entry.ManifestPath == GetManifestPath(entry.Author, entry.Id, entry.Version),
            "Manifest path must be the exact relative marketplace/manifests/{author}/{id}/{version}.json path.");
        Require(entry.Sha256 is { Length: 64 } && entry.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "Manifest SHA-256 must be 64 lowercase hexadecimal characters.");
    }

    internal static string Identity(MarketplaceEntry e) => $"{e.Author.ToLowerInvariant()}/{e.Id}/{e.Version}";

    internal static void VerifyManifest(MarketplaceEntry entry, ReadOnlyMemory<byte> bytes)
    {
        ValidateEntry(entry);
        Require(ComputeSha256(bytes.Span) == entry.Sha256, "Manifest SHA-256 mismatch; content was not accepted.");
        var manifest = ParseManifest(bytes);
        Require(manifest.Id == entry.Id && manifest.Version == entry.Version && manifest.Author == entry.Author &&
            manifest.Title == entry.Title && manifest.Description == entry.Description && manifest.GameVersion == entry.GameVersion &&
            manifest.WorkshopCollectionId == entry.WorkshopCollectionId && manifest.RequiredDlc.SequenceEqual(entry.RequiredDlc) &&
            (entry.ModCount is null || entry.ModCount == manifest.Mods.Count) &&
            (entry.ReleaseNotes.Length == 0 || entry.ReleaseNotes == manifest.ReleaseNotes) && entry.Images.SequenceEqual(manifest.Images),
            "Manifest identity or metadata does not match its catalogue entry.");
    }

    internal static bool IsLogin(string? value) => value is { Length: > 0 and <= 39 } &&
        Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new MarketplaceException(message);
    }

    internal static JsonDocument Document(ReadOnlyMemory<byte> json, int maximumBytes)
    {
        Require(json.Length is > 0 && json.Length <= maximumBytes, "Marketplace JSON is empty or exceeds its byte limit.");
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            try { CheckDuplicateProperties(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException) { throw new MarketplaceException("Malformed marketplace JSON."); }
    }

    private static T Parse<T>(ReadOnlyMemory<byte> json, int maximumBytes) where T : class
    {
        using var document = Document(json, maximumBytes);
        try { return document.Deserialize<T>(ReadOptions) ?? throw new MarketplaceException("Null marketplace JSON."); }
        catch (JsonException) { throw new MarketplaceException("Marketplace JSON has missing, unknown or incorrectly typed fields."); }
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                Require(keys.Add(property.Name), "Duplicate JSON property.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
    }

    private static void WriteImages(Utf8JsonWriter writer, IReadOnlyList<MarketplaceImage> images)
    {
        if (images.Count == 0) return;
        writer.WriteStartArray("images");
        foreach (var image in images)
        {
            writer.WriteStartObject();
            writer.WriteString("path", image.Path); writer.WriteString("sha256", image.Sha256);
            writer.WriteNumber("bytes", image.Bytes); writer.WriteNumber("width", image.Width); writer.WriteNumber("height", image.Height);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static bool IsId(string? value) => value is { Length: > 0 and <= 64 } &&
        Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant);

    private static bool IsVersion(string? value) => value is { Length: > 0 and <= 64 } &&
        Regex.IsMatch(value, @"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:\.(0|[1-9][0-9]{0,8}))?(?:-[a-z0-9]+(?:[.-][a-z0-9]+)*)?$", RegexOptions.CultureInvariant);

    private static bool IsPackageId(string? value) => value is { Length: > 0 and <= 256 } &&
        Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

    private static bool IsWorkshopId(string? value) => value is { Length: > 0 and <= 20 } &&
        value[0] != '0' && value.All(c => c is >= '0' and <= '9') &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;

    private static bool IsOfficialDlc(string packageId) =>
        !packageId.Equals("ludeon.rimworld", StringComparison.OrdinalIgnoreCase) &&
        GameContentPolicy.KeepWhenClearing(packageId, new GameVersion(1, 6));

    private static void Text(string? value, int limit, string field, bool multiline = false)
    {
        Require(!string.IsNullOrWhiteSpace(value) && value.Length <= limit && value == value.Trim() &&
            value.All(c => !char.IsControl(c) || multiline && c is '\n' or '\r' or '\t'), $"Missing or invalid {field}.");
    }

    private static void Metadata(string id, string version, string title, string author, string description,
        string gameVersion, IReadOnlyList<string> requiredDlc, string? collection)
    {
        _ = GetManifestPath(author, id, version);
        Text(title, 200, "title");
        Require(description is not null && description.Length <= 8000 && description == description.Trim()
            && description.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t'), "排序简介过长或包含不支持的控制字符。");
        _ = ParseGameVersions(gameVersion);
        Require(requiredDlc is { Count: <= 64 }, "Missing or oversized requiredDlc array.");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dlc in requiredDlc)
        {
            Text(dlc, 128, "requiredDlc");
            Require(IsOfficialDlc(dlc), "requiredDlc entries must be official DLC package IDs.");
            Require(unique.Add(dlc), "Duplicate requiredDlc.");
        }
        Require(collection is null || IsWorkshopId(collection), "workshopCollectionId must be a positive ASCII decimal string.");
    }

    public static IReadOnlyList<string> ParseGameVersions(string? text)
    {
        Require(text is { Length: <= 512 }, "游戏版本列表过长。");
        var entries = text!.Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Require(entries.Length is > 0 and <= 16, "请选择或填写 1 至 16 个游戏版本。");
        var versions = new List<string>(entries.Length);
        var series = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            Require(Regex.IsMatch(entry, @"^[0-9]{1,3}\.[0-9]{1,3}(?:\.[0-9]{1,9}){0,2}$", RegexOptions.CultureInvariant),
                "游戏版本格式无效，请使用例如 1.7 或 1.6.4633。");
            var parsed = GameVersion.Parse(entry)
                ?? throw new MarketplaceException("游戏版本格式无效，请使用例如 1.7 或 1.6.4633。");
            Require(series.Add(parsed.Series), "同一游戏版本系列无需重复选择。");
            versions.Add(entry);
        }
        return versions.AsReadOnly();
    }

    public static GameVersion PrimaryGameVersion(string? text)
        => GameVersion.Parse(ParseGameVersions(text)[0])!;

    private static void WriteMetadata(Utf8JsonWriter writer, string id, string version, string title,
        string author, string description, string gameVersion, IReadOnlyList<string> requiredDlc, string? collection)
    {
        writer.WriteString("id", id);
        writer.WriteString("version", version);
        writer.WriteString("title", title);
        writer.WriteString("author", author);
        writer.WriteString("description", description);
        writer.WriteString("gameVersion", gameVersion);
        writer.WriteStartArray("requiredDlc");
        foreach (var dlc in requiredDlc) writer.WriteStringValue(dlc);
        writer.WriteEndArray();
        writer.WriteString("workshopCollectionId", collection);
    }

    private static byte[] Write(Action<Utf8JsonWriter> action, int maximumBytes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) action(writer);
        Require(stream.Length <= maximumBytes, "Serialized marketplace JSON exceeds its byte limit.");
        return stream.ToArray();
    }
}
