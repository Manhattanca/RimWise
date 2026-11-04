using System.Text.Json;

namespace RimWise.Integration.Safety;

public sealed record ArchivedMod(string Id, string Name, DateTimeOffset TimeUtc, bool CanRestore, string Status);

/// <summary>Moves one local installation into a journalled archive. Never deletes mod contents.</summary>
public sealed class LocalModArchive(string applicationDirectory, string localModsDirectory)
{
    private readonly string archiveRoot = ScopedPaths.Child(applicationDirectory, "Data/DeletedModsV2");
    private readonly string modsRoot = ScopedPaths.Ordinary(localModsDirectory);

    public string Archive(string directory)
    {
        directory = ScopedPaths.Ordinary(directory, true).TrimEnd('\\', '/');
        if (!Directory.Exists(directory) || !string.Equals(Path.GetDirectoryName(directory.TrimEnd('\\', '/')), modsRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("只允许移除设置的本地 Mods 目录下的独立模组。");
        // A same-volume directory rename avoids a partially copied installation on power loss.
        if (!string.Equals(Path.GetPathRoot(directory), Path.GetPathRoot(archiveRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("模组与软件备份目录不在同一磁盘，暂不支持安全移除；未改动文件。");
        if (archiveRoot.Equals(directory, StringComparison.OrdinalIgnoreCase)
            || archiveRoot.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份目录位于待移除的模组内部，未改动文件。");
        using var source = PinnedDirectory.Open(modsRoot);
        var id = Guid.NewGuid().ToString("N");
        var entry = ScopedPaths.Child(archiveRoot, id);
        using var destination = PinnedDirectory.Open(entry, create: true);
        var record = new Record(1, id, modsRoot, Path.GetFileName(directory), DateTimeOffset.UtcNow);
        WriteNew(ScopedPaths.Child(entry, "record.json"), record);
        source.MoveChildTo(Path.GetFileName(directory.TrimEnd('\\', '/')), destination, "content", ValidateTree);
        // Existence of the prepared record + content is sufficient after an interrupted move.
        return id;
    }

    public IReadOnlyList<ArchivedMod> Read()
    {
        ScopedPaths.Ordinary(archiveRoot);
        if (!Directory.Exists(archiveRoot)) return [];
        var result = new List<ArchivedMod>();
        foreach (var entry in Directory.EnumerateDirectories(archiveRoot).Take(1000))
        {
            var id = Path.GetFileName(entry);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                var record = ReadRecord(id);
                if (!string.Equals(record.ModsRoot.TrimEnd('\\', '/'), modsRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(ScopedPaths.Child(entry, "restored.json"))) continue;
                var content = ScopedPaths.Child(entry, "content");
                var target = ScopedPaths.Child(modsRoot, record.Name);
                var archived = Directory.Exists(content);
                var available = !Path.Exists(target);
                result.Add(new(id, record.Name, record.TimeUtc, archived && available,
                    !archived ? "移除未发生，或恢复已完成；记录保留" : available ? "可恢复到本地 Mods" : "原位置已被占用，禁止覆盖"));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { result.Add(new(id, "无法验证的备份", DateTimeOffset.MinValue, false, ex.Message)); }
        }
        return result.OrderByDescending(x => x.TimeUtc).ToArray();
    }

    public void Restore(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("模组备份编号无效。");
        using var source = PinnedDirectory.Open(ScopedPaths.Child(archiveRoot, id));
        var record = ReadRecord(id);
        if (!string.Equals(record.ModsRoot.TrimEnd('\\', '/'), modsRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份属于其他本地 Mods 目录。");
        var entry = ScopedPaths.Child(archiveRoot, id);
        var target = ScopedPaths.Child(modsRoot, record.Name);
        if (Path.Exists(target)) throw new IOException("原位置已被占用，未覆盖任何文件。");
        using var destination = PinnedDirectory.Open(modsRoot);
        source.MoveChildTo("content", destination, record.Name, ValidateTree);
        WriteNew(ScopedPaths.Child(entry, "restored.json"), new { TimeUtc = DateTimeOffset.UtcNow });
    }

    private Record ReadRecord(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("模组备份编号无效。");
        var path = ScopedPaths.Child(archiveRoot, id + "/record.json");
        using var stream = File.OpenRead(path);
        if (stream.Length > 65536) throw new InvalidDataException("模组备份记录过大。");
        var record = JsonSerializer.Deserialize<Record>(stream) ?? throw new InvalidDataException("模组备份记录为空。");
        if (record.Schema != 1 || record.Id != id || string.IsNullOrWhiteSpace(record.ModsRoot)
            || string.IsNullOrWhiteSpace(record.Name) || record.Name is "." or ".." || record.Name.Contains(':')
            || record.Name != Path.GetFileName(record.Name) || record.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("模组备份记录无效。");
        return record;
    }
    private static void ValidateTree(string directory)
    {
        ScopedPaths.Ordinary(directory, true);
        var pending = new Stack<string>(); pending.Push(directory);
        var count = 0;
        while (pending.TryPop(out var parent))
            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                if (++count > 500000) throw new IOException("模组文件数量超过本次安全检查上限。");
                ScopedPaths.Ordinary(path, true);
                if (Directory.Exists(path)) pending.Push(path);
            }
    }
    private static void WriteNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value); stream.Flush(true);
    }
    private sealed record Record(int Schema, string Id, string ModsRoot, string Name, DateTimeOffset TimeUtc);
}
