using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Core;

namespace RimWise.Formats;

/// <summary>Writes only ModsConfig.xml in one explicitly selected Config directory.</summary>
public sealed partial class GameOrderStore(string configDirectory, string backupDirectory)
{
    public string TargetPath { get; } = Path.Combine(Path.GetFullPath(configDirectory), "ModsConfig.xml");
    public string BackupDirectory { get; } = Path.GetFullPath(backupDirectory);
    private readonly ModsConfigXmlFormat format = new();

    public GameOrderPlan Prepare(ModsConfigDocument proposed, GameVersion? runtimeVersion = null) => PrepareExpected(proposed, null, runtimeVersion);

    internal GameOrderPlan PrepareExpected(ModsConfigDocument proposed, byte[]? expected, GameVersion? runtimeVersion = null)
    {
        var original = ReadTarget();
        if (expected is not null && !original.AsSpan().SequenceEqual(expected))
            throw new IOException("游戏配置在自动测试期间已变化，未覆盖外部改动。");
        using var input = new MemoryStream(original);
        var existing = format.Read(input);
        var existingVersion = GameVersion.Parse(existing.GameVersion);
        var sourceVersion = GameVersion.Parse(proposed.GameVersion);
        var targetVersion = runtimeVersion ?? existingVersion ?? sourceVersion
            ?? throw new InvalidDataException("游戏及配置版本均未识别，请在路径设置中指定目标版本。");
        GameVersionCompatibility.EnsureCompatibleIdentifiers(existingVersion, targetVersion);
        GameVersionCompatibility.EnsureCompatibleIdentifiers(sourceVersion, targetVersion);
        // The game caches active IDs case-sensitively. Metadata spelling (including DLC)
        // must not leak into the saved file while test launches use canonical IDs.
        var order = proposed.Order.ForGame(targetVersion);
        var after = existing.WithOrder(order);
        if (runtimeVersion is not null && (existingVersion is null || existingVersion.Series != runtimeVersion.Series))
            after = after.WithGameVersion(runtimeVersion);
        var replacement = after.GameVersion == existing.GameVersion && existing.Order.PackageIds.SequenceEqual(order.PackageIds, StringComparer.Ordinal)
            ? original : format.Write(after);
        return new(this, original, replacement, existing, after) { RuntimeVersion = runtimeVersion };
    }

    internal GameOrderCommit Commit(GameOrderPlan plan)
    {
        if (!ReferenceEquals(plan.Store, this)) throw new ArgumentException("保存计划不属于此配置目录。");
        if (plan.Original.AsSpan().SequenceEqual(plan.Replacement))
        {
            if (!ReadTarget().AsSpan().SequenceEqual(plan.Original))
                throw new IOException("游戏配置已被游戏或其他软件修改，请重新读取后再操作；未覆盖外部改动。");
            return new(this, plan.Original, plan.Replacement, "", changed: false);
        }
        var backup = ReplaceExpected(plan.Original, plan.Replacement, "save");
        return new(this, plan.Original, plan.Replacement, backup, changed: true);
    }

    internal string ReplaceExpected(byte[] expected, byte[] replacement, string operation)
    {
        using var writeLock = new GameWriteLock(TargetPath);
        ValidateOrdinaryPath(TargetPath, mustExist: true);
        ValidateOrdinaryPath(BackupDirectory, mustExist: false);
        var current = ReadTarget();
        if (!current.AsSpan().SequenceEqual(expected))
            throw new IOException("游戏配置已被游戏或其他软件修改，请重新读取后再操作；未覆盖外部改动。");
        Directory.CreateDirectory(BackupDirectory);
        ValidateOrdinaryPath(BackupDirectory, mustExist: true);
        var token = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var backup = Path.Combine(BackupDirectory, token + "-before.xml");
        OrderFileService.WriteNew(backup, current);
        if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(expected)) throw new IOException("备份校验失败，未保存游戏配置。");
        var journal = Path.Combine(BackupDirectory, token + ".json");
        var temporary = Path.Combine(Path.GetDirectoryName(TargetPath)!, ".rim-order-" + Guid.NewGuid().ToString("N") + ".tmp");
        // The displaced copy is produced by File.Replace itself, preserving even a late external update.
        var displaced = Path.Combine(BackupDirectory, token + "-displaced.xml");
        // File.Replace requires its backup on the same volume as the target.
        var localDisplaced = Path.Combine(Path.GetDirectoryName(TargetPath)!, ".rim-displaced-" + Guid.NewGuid().ToString("N") + ".bak");
        var record = new GameWriteRecord(2, DateTimeOffset.UtcNow, TargetPath, operation, Path.GetFileName(backup),
            Hash(expected), Hash(replacement), "prepared", Path.GetFileName(temporary), Path.GetFileName(localDisplaced));
        OrderFileService.WriteNew(journal, JsonSerializer.SerializeToUtf8Bytes(record));
        Checkpoint?.Invoke("prepared");
        var didReplace = false;
        try
        {
            OrderFileService.WriteNew(temporary, replacement);
            ValidateOrdinaryPath(TargetPath, mustExist: true);
            using (var guard = new FileStream(TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var guarded = OrderFileCodec.ReadBounded(guard, OrderFileCodec.MaximumBytes);
                if (!guarded.AsSpan().SequenceEqual(expected)) throw new IOException("保存前配置再次发生变化，请重新读取。");
            }
            // ReplaceFile needs write-compatible sharing on Windows. Close the read guard first;
            // validate the atomically displaced bytes below to detect a late replacement.
            try { AtomicConfigReplace.Replace(temporary, TargetPath, localDisplaced); }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException("游戏配置原子替换被拒绝，请检查目录权限或文件占用。备份和恢复记录已保留在："
                    + BackupDirectory, ex);
            }
            didReplace = true;
            Checkpoint?.Invoke("replaced");
            var displacedBytes = File.ReadAllBytes(localDisplaced);
            // Keep any late external update and refuse to accept this save as successful.
            OrderFileService.WriteNew(displaced, displacedBytes);
            Checkpoint?.Invoke("displaced-backed-up");
            if (!displacedBytes.AsSpan().SequenceEqual(expected))
            {
                if (ReadTarget().AsSpan().SequenceEqual(replacement))
                    AtomicConfigReplace.Replace(localDisplaced, TargetPath, null);
                throw new IOException("保存期间检测到外部替换；已保留被替换文件，请检查配置及备份记录。");
            }
            if (!ReadTarget().AsSpan().SequenceEqual(replacement))
                throw new IOException("写入后配置再次变化；原配置已备份，请重新读取当前状态。");
            // A new completion marker avoids truncating the durable prepared record.
            OrderFileService.WriteNew(Path.Combine(BackupDirectory, token + "-committed.json"),
                JsonSerializer.SerializeToUtf8Bytes(record with { State = "committed" }));
            Checkpoint?.Invoke("committed");
            return backup;
        }
        catch (Exception ex) when (didReplace && (ex is IOException or UnauthorizedAccessException))
        {
            // If only our bytes remain, restore the displaced file. Never overwrite a later external edit.
            if (File.Exists(localDisplaced) && ReadTarget().AsSpan().SequenceEqual(replacement))
                AtomicConfigReplace.Replace(localDisplaced, TargetPath, null);
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(localDisplaced) && File.Exists(displaced)) File.Delete(localDisplaced);
        }
    }

    public GameOrderPlan PrepareRestore(string backupFile)
    {
        var backupPath = Path.GetFullPath(backupFile);
        if (!string.Equals(Path.GetDirectoryName(backupPath), BackupDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(backupPath).EndsWith("-before.xml", StringComparison.Ordinal))
            throw new InvalidDataException("请选择当前软件游戏备份目录中的原配置备份。");
        ValidateOrdinaryPath(backupPath, mustExist: true);
        var token = Path.GetFileName(backupPath)[..^"-before.xml".Length];
        var recordPath = Path.Combine(BackupDirectory, token + ".json");
        ValidateOrdinaryPath(recordPath, mustExist: true);
        using var recordInput = File.OpenRead(recordPath);
        var record = JsonSerializer.Deserialize<GameWriteRecord>(OrderFileCodec.ReadBounded(recordInput, 65536))
            ?? throw new InvalidDataException("备份记录无法读取。");
        if (record.Schema is not (1 or 2) || record.BackupName != Path.GetFileName(backupPath)
            || !string.Equals(record.Target, TargetPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份属于其他配置目录或记录不匹配。");
        using var input = File.OpenRead(backupPath);
        var bytes = OrderFileCodec.ReadBounded(input, OrderFileCodec.MaximumBytes);
        if (Hash(bytes) != record.BeforeSha256) throw new InvalidDataException("原配置备份校验失败。");
        var current = ReadTarget();
        return new(this, current, bytes, format.Read(new MemoryStream(current)), format.Read(new MemoryStream(bytes)));
    }

    private byte[] ReadTarget()
    {
        ValidateOrdinaryPath(TargetPath, mustExist: true);
        using var input = new FileStream(TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return OrderFileCodec.ReadBounded(input, OrderFileCodec.MaximumBytes);
    }

    internal static void ValidateOrdinaryPath(string path, bool mustExist)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path[(Path.GetPathRoot(path)?.Length ?? 0)..].Contains(':'))
            throw new IOException("配置和备份必须使用普通本地路径。");
        if (mustExist && !File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("配置或备份不存在，请检查路径。", path);
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("配置或备份路径包含符号链接／目录跳转，本次拒绝写入。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    // Only the check assembly may insert process-kill boundaries; never exposed to AI or the UI.
    internal Action<string>? Checkpoint { get; init; }
    private sealed record GameWriteRecord(int Schema, DateTimeOffset TimeUtc, string Target, string Operation,
        string BackupName, string BeforeSha256, string AfterSha256, string State, string? TemporaryName = null, string? DisplacedName = null);
}

public sealed class GameOrderPlan
{
    internal GameOrderStore Store { get; }
    internal byte[] Original { get; }
    internal byte[] Replacement { get; }
    public ModsConfigDocument Before { get; }
    public ModsConfigDocument After { get; }
    internal GameVersion? RuntimeVersion { get; init; }
    internal GameOrderPlan(GameOrderStore store, byte[] original, byte[] replacement, ModsConfigDocument before, ModsConfigDocument after)
        => (Store, Original, Replacement, Before, After) = (store, original, replacement, before, after);
    public GameOrderCommit Apply() => Store.Commit(this);
    /// <summary>Retarget the original transaction after testing, preserving its expected-file precondition.</summary>
    public GameOrderPlan WithOrder(LoadOrder order) => Store.PrepareExpected(After.WithOrder(order), Original, RuntimeVersion);
}

public sealed class GameOrderCommit
{
    private readonly GameOrderStore store;
    private readonly byte[] before;
    private readonly byte[] after;
    private bool applied = true;
    public string BackupPath { get; }
    public bool Changed { get; }
    internal GameOrderCommit(GameOrderStore store, byte[] before, byte[] after, string backup, bool changed)
        => (this.store, this.before, this.after, BackupPath, Changed) = (store, before, after, backup, changed);
    public void Undo()
    {
        if (!applied) throw new InvalidOperationException("此次保存已经撤销。");
        if (Changed) store.ReplaceExpected(after, before, "undo");
        applied = false;
    }
    public void Redo()
    {
        if (applied) throw new InvalidOperationException("此次保存尚未撤销。");
        if (Changed) store.ReplaceExpected(before, after, "redo");
        applied = true;
    }
}
