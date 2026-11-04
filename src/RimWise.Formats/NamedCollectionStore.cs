using System.Security.Cryptography;
using System.Text.Json;

namespace RimWise.Formats;

/// <summary>Stores named load-order snapshots locally without writing the game configuration.</summary>
public sealed class NamedCollectionStore(string directory)
{
    private const int SchemaVersion = 1;
    private const long MaximumStoredBytes = 48 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };

    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public IReadOnlyList<NamedCollectionSummary> List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        EnsureOrdinaryDirectory(DirectoryPath);
        var result = new List<NamedCollectionSummary>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "collection-*.rimcollection", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var stored = ReadStored(path);
                result.Add(new NamedCollectionSummary(Path.GetFileName(path), stored.Name, stored.UpdatedUtc,
                    stored.Payload.Length, stored.ItemCount, stored.GameVersion));
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
            catch (JsonException) { }
        }
        return result.OrderByDescending(item => item.UpdatedUtc).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Save(string? id, string name, OrderFileDocument document, string sourceName)
    {
        name = ValidateName(name);
        var codec = new OrderFileCodec();
        var format = document.SourceFormat == OrderFileFormat.SavegameRws ? OrderFileFormat.RmlXml : document.SourceFormat;
        var payload = codec.Write(document, format);
        if (payload.Length > OrderFileCodec.MaximumBytes) throw new InvalidDataException("合集排序数据过大。");
        using (var check = new MemoryStream(payload)) codec.Read(check);

        EnsureOrdinaryDirectory(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        EnsureOrdinaryDirectory(DirectoryPath);
        var path = ResolvePath(id, allowMissing: true);
        var stored = new StoredCollection(SchemaVersion, name, DateTimeOffset.UtcNow, Path.GetFileName(sourceName), format,
            document.Configuration.GameVersion, document.Configuration.Order.PackageIds.Count,
            Convert.ToHexString(SHA256.HashData(payload)), payload);
        var temporary = Path.Combine(DirectoryPath, ".collection-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, stored, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public (OrderFileDocument Document, NamedCollectionSummary Summary) Read(string id)
    {
        var path = ResolvePath(id, allowMissing: false);
        var stored = ReadStored(path);
        var payload = stored.Payload;
        if (!string.Equals(stored.Sha256, Convert.ToHexString(SHA256.HashData(payload)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("合集校验失败，文件可能已损坏。");
        using var input = new MemoryStream(payload);
        var document = new OrderFileCodec().Read(input);
        if (document.SourceFormat != stored.Format) throw new InvalidDataException("合集格式与内容不符。");
        return (document, new NamedCollectionSummary(Path.GetFileName(path), stored.Name, stored.UpdatedUtc,
            payload.Length, stored.ItemCount, stored.GameVersion));
    }

    public void Delete(string id)
    {
        var path = ResolvePath(id, allowMissing: false);
        File.Delete(path);
    }

    private StoredCollection ReadStored(string path)
    {
        EnsureOrdinaryFile(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumStoredBytes) throw new InvalidDataException("合集文件过大。");
        var stored = JsonSerializer.Deserialize<StoredCollection>(input, JsonOptions)
            ?? throw new InvalidDataException("合集文件为空。");
        if (stored.SchemaVersion != SchemaVersion || stored.Payload is null || stored.Payload.Length == 0
            || stored.Payload.Length > OrderFileCodec.MaximumBytes || stored.ItemCount < 0
            || !Enum.IsDefined(stored.Format)) throw new InvalidDataException("不支持或损坏的合集文件。");
        _ = ValidateName(stored.Name);
        return stored;
    }

    private string ResolvePath(string? id, bool allowMissing)
    {
        if (string.IsNullOrWhiteSpace(id)) return Path.Combine(DirectoryPath, "collection-" + Guid.NewGuid().ToString("N") + ".rimcollection");
        var fileName = Path.GetFileName(id);
        if (!string.Equals(fileName, id, StringComparison.Ordinal) || !fileName.StartsWith("collection-", StringComparison.Ordinal)
            || !fileName.EndsWith(".rimcollection", StringComparison.Ordinal)) throw new InvalidDataException("合集标识无效。");
        var path = Path.Combine(DirectoryPath, fileName);
        EnsureOrdinaryFile(path);
        if (!allowMissing && !File.Exists(path)) throw new FileNotFoundException("合集不存在。", path);
        return path;
    }

    private static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("合集名称需为 1 至 80 个字符。", nameof(name));
        return name;
    }

    private static void EnsureOrdinaryDirectory(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("合集目录包含链接或目录跳转，已拒绝访问。");
    }

    private static void EnsureOrdinaryFile(string path)
    {
        EnsureOrdinaryDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("合集文件是链接或目录跳转，已拒绝访问。");
    }

    private sealed record StoredCollection(int SchemaVersion, string Name, DateTimeOffset UpdatedUtc, string SourceName,
        OrderFileFormat Format, string GameVersion, int ItemCount, string Sha256, byte[] Payload);
}

public sealed record NamedCollectionSummary(string Id, string Name, DateTimeOffset UpdatedUtc, int PayloadBytes,
    int ItemCount, string GameVersion)
{
    public string Details => $"{ItemCount} 项 · {UpdatedUtc.LocalDateTime:yyyy-MM-dd HH:mm}";
}
