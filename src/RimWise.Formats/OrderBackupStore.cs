using System.Security.Cryptography;
using System.Text.Json;

namespace RimWise.Formats;

/// <summary>Snapshots the editable list, never follows a stored path or writes a game file.</summary>
public sealed class OrderBackupStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private sealed record BackupData(int SchemaVersion, DateTimeOffset CreatedUtc, string SourceName,
        OrderFileFormat Format, string Sha256, byte[] Payload);

    public string Save(OrderFileDocument document, string sourceName)
    {
        var codec = new OrderFileCodec();
        var backupFormat = document.SourceFormat == OrderFileFormat.SavegameRws ? OrderFileFormat.RmlXml : document.SourceFormat;
        var payload = codec.Write(document, backupFormat);
        // Verify the serialized snapshot is readable before publishing a backup file.
        using (var check = new MemoryStream(payload)) codec.Read(check);
        var data = new BackupData(1, DateTimeOffset.UtcNow, Path.GetFileName(sourceName), backupFormat,
            Convert.ToHexString(SHA256.HashData(payload)), payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        EnsureOrdinaryDirectory(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        EnsureOrdinaryDirectory(DirectoryPath);
        var file = Path.Combine(DirectoryPath, $"order-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.rimbackup");
        OrderFileService.WriteNew(file, bytes);
        return file;
    }

    public OrderFileDocument Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = OrderFileCodec.ReadBounded(input, 48 * 1024 * 1024);
        var data = JsonSerializer.Deserialize<BackupData>(bytes)
            ?? throw new InvalidDataException("排序备份为空。");
        if (data.SchemaVersion != 1 || data.Payload is null || data.Payload.Length > OrderFileCodec.MaximumBytes
            || !Enum.IsDefined(data.Format)) throw new InvalidDataException("不支持或损坏的排序备份。");
        if (!string.Equals(data.Sha256, Convert.ToHexString(SHA256.HashData(data.Payload)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("排序备份校验失败，文件可能已损坏。");
        using var payload = new MemoryStream(data.Payload);
        var document = new OrderFileCodec().Read(payload);
        if (document.SourceFormat != data.Format) throw new InvalidDataException("备份格式与内容不符。");
        return document;
    }

    private static void EnsureOrdinaryDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("排序备份目录包含链接或目录跳转，请将软件移到普通目录。");
    }
}
