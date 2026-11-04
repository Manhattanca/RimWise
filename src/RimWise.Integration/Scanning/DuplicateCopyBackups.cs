using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.Configuration;
using RimWise.Integration.Game;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Scanning;

public sealed record DuplicateCopyMove(string Original, string Backup);
public sealed record DuplicateCopyBackup(string Id, string Selected, DuplicateCopyMove[] Moves, bool Restored = false);

/// <summary>Only called following explicit product UI confirmation. Renames whole directories on the same
/// volume without changing file contents. Write-ahead journals survive partial moves and process exit.</summary>
public sealed class DuplicateCopyBackups(string dataDirectory, GamePaths paths)
{
    private string Store => ScopedPaths.Child(dataDirectory, "Data/DuplicateCopies");
    private string[] Roots => new[] { paths.LocalModsDirectory.Length > 0 ? paths.LocalModsDirectory
        : Path.Combine(paths.GameDirectory, "Mods"), paths.WorkshopDirectory }
        .Where(p => p.Length > 0).Select(p => Path.TrimEndingDirectorySeparator(ScopedPaths.Ordinary(p)))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public DuplicateCopyBackup Plan(IReadOnlyList<ModInstallation> copies, ModInstallation selected)
    {
        if (copies.Count < 2 || !copies.Contains(selected) || copies.Any(c => c.Source == ModSource.Game
            || c.Source != selected.Source || !c.PackageId.Equals(selected.PackageId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("副本组已变化，请重新选择。");
        var id = Guid.NewGuid().ToString("N");
        var moves = copies.Where(c => c != selected).Select((c, i) =>
        {
            var source = ValidateSource(c.Directory);
            var scan = Path.GetDirectoryName(source)!;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scan.ToUpperInvariant())))[..16];
            var backup = Path.Combine(Path.GetDirectoryName(scan)!, ".RimWise-DuplicateBackups", key, id, i.ToString());
            ValidateBackup(source, backup);
            return new DuplicateCopyMove(source, backup);
        }).ToArray();
        ValidateSource(selected.Directory);
        if (copies.Select(c => Path.GetFullPath(c.Directory)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copies.Count)
            throw new InvalidDataException("副本路径重复。");
        return new(id, selected.Directory, moves);
    }

    public void Apply(DuplicateCopyBackup plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var directory = PinnedDirectory.Open(Store, create: true);
        using var gate = new FileStream(Path.Combine(Store, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        EnsureStopped(); Validate(plan);
        var occupied = plan.Moves.Select(m => m.Original).Append(plan.Selected).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (List().Any(item => item.Moves.Select(m => m.Original).Append(item.Selected).Any(occupied.Contains)))
            throw new IOException("此副本已有待恢复记录，请先恢复或处理备份。");
        foreach (var move in plan.Moves)
        {
            CheckTree(move.Original, token);
            if (Path.Exists(move.Backup)) throw new IOException("备份目标已存在，未移动文件。");
        }
        CheckTree(plan.Selected, token);
        var journal = Journal(plan.Id);
        if (File.Exists(journal)) throw new IOException("备份记录已存在。");
        Save(plan);
        // Cancellation between moves leaves a truthful journal; restore is explicit, never overwrites a Steam download.
        foreach (var move in plan.Moves)
        {
            token.ThrowIfCancellationRequested(); EnsureStopped();
            Move(move.Original, move.Backup, token);
        }
    }

    public IReadOnlyList<DuplicateCopyBackup> List()
    {
        if (!Directory.Exists(Store)) return [];
        ScopedPaths.Ordinary(Store, true);
        var result = new List<DuplicateCopyBackup>();
        foreach (var file in Directory.EnumerateFiles(Store, "*.json"))
        {
            ScopedPaths.Ordinary(file, true);
            if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("副本备份记录过大。");
            DuplicateCopyBackup record;
            try { record = JsonSerializer.Deserialize<DuplicateCopyBackup>(File.ReadAllText(file))
                ?? throw new InvalidDataException("副本备份记录无法读取。"); }
            catch (JsonException ex) { throw new InvalidDataException("副本备份记录无法读取。", ex); }
            if (string.IsNullOrWhiteSpace(record.Selected)) throw new InvalidDataException("副本备份路径缺失。");
            if (!Roots.Contains(Path.GetDirectoryName(record.Selected), StringComparer.OrdinalIgnoreCase)) continue;
            Validate(record);
            if (Path.GetFileNameWithoutExtension(file) != record.Id) throw new InvalidDataException("副本备份记录不匹配。");
            if (!record.Restored) result.Add(record);
        }
        return result;
    }

    public void Restore(string id, CancellationToken token)
    {
        using var directory = PinnedDirectory.Open(Store);
        using var gate = new FileStream(Path.Combine(Store, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        EnsureStopped();
        var record = List().Single(r => r.Id == id);
        foreach (var move in record.Moves)
        {
            if (Path.Exists(move.Backup) && Path.Exists(move.Original))
                throw new IOException("原位置已被 Steam 或其他文件占用，未覆盖任何文件：" + move.Original);
            if (!Directory.Exists(move.Backup) && !Directory.Exists(move.Original))
                throw new IOException("原位置和备份均不可用：" + move.Original);
        }
        foreach (var move in record.Moves.Reverse())
        {
            token.ThrowIfCancellationRequested(); EnsureStopped();
            if (Directory.Exists(move.Backup)) Move(move.Backup, move.Original, token);
        }
        Save(record with { Restored = true });
    }

    private void Validate(DuplicateCopyBackup record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _) || string.IsNullOrWhiteSpace(record.Selected)
            || record.Moves is null || record.Moves.Length is < 1 or > 256
            || record.Moves.Any(m => m is null || string.IsNullOrWhiteSpace(m.Original) || string.IsNullOrWhiteSpace(m.Backup)))
            throw new InvalidDataException("副本备份记录无效。");
        ValidateSource(record.Selected);
        if (record.Moves.Select(m => m.Original).Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Moves.Length
            || record.Moves.Select(m => m.Backup).Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Moves.Length)
            throw new InvalidDataException("副本备份路径重复。");
        for (var index = 0; index < record.Moves.Length; index++)
        {
            var move = record.Moves[index];
            ValidateSource(move.Original); ValidateBackup(move.Original, move.Backup);
            var source = ValidateSource(move.Original);
            var scan = Path.GetDirectoryName(source)!;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scan.ToUpperInvariant())))[..16];
            var expected = Path.Combine(Path.GetDirectoryName(scan)!, ".RimWise-DuplicateBackups", key, record.Id, index.ToString());
            if (!Path.GetFullPath(move.Backup).Equals(expected, StringComparison.OrdinalIgnoreCase)
                || !Path.GetDirectoryName(source)!.Equals(Path.GetDirectoryName(record.Selected), StringComparison.OrdinalIgnoreCase)
                || move.Original.Equals(record.Selected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("副本备份边界不匹配。");
        }
    }

    private string ValidateSource(string path)
    {
        path = Path.TrimEndingDirectorySeparator(ScopedPaths.Ordinary(path));
        if (!Roots.Contains(Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase))
            throw new IOException("副本不在所选模组目录的直接子目录内。");
        return path;
    }

    private void ValidateBackup(string source, string backup)
    {
        backup = ScopedPaths.Ordinary(backup);
        var scan = Path.GetDirectoryName(source)!;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scan.ToUpperInvariant())))[..16];
        var parent = Path.Combine(Path.GetDirectoryName(scan)!, ".RimWise-DuplicateBackups", key) + Path.DirectorySeparatorChar;
        if (!backup.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || Roots.Any(root => backup.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || !Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(backup), StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份位置必须在模组扫描范围外的同一磁盘。");
    }

    private string Journal(string id) => ScopedPaths.Child(Store, id + ".json");
    private void Save(DuplicateCopyBackup record)
    {
        var path = Journal(record.Id);
        ScopedPaths.Ordinary(path);
        var pending = path + ".pending";
        ScopedPaths.Ordinary(pending);
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, record); stream.Flush(true); }
        File.Move(pending, path, true);
    }

    private static void EnsureStopped()
    { if (GameProcessService.IsAnyRunning()) throw new IOException("请关闭游戏后再切换或恢复模组副本。"); }

    private static void Move(string source, string target, CancellationToken token)
    {
        using var from = PinnedDirectory.Open(Path.GetDirectoryName(source)!);
        using var to = PinnedDirectory.Open(Path.GetDirectoryName(target)!, create: true);
        from.MoveChildTo(Path.GetFileName(source), to, Path.GetFileName(target), path => CheckTree(path, token));
    }

    private static void CheckTree(string root, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested(); ScopedPaths.Ordinary(directory, true);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 200000) throw new IOException("副本文件数量过多，未完成安全检查。");
                ScopedPaths.Ordinary(path, true);
                if (Directory.Exists(path)) pending.Push(path);
            }
        }
    }
}
