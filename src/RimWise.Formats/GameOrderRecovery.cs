using System.Text.Json;

namespace RimWise.Formats;

public enum GameRecoveryState { Unchanged, Applied, Conflict, Invalid }
public sealed record GameRecoveryItem(string Id, DateTimeOffset TimeUtc, GameRecoveryState State, string Description);

public sealed partial class GameOrderStore
{
    /// <summary>Inspects only this application's journal and the currently selected ModsConfig.xml.</summary>
    public IReadOnlyList<GameRecoveryItem> InspectInterrupted()
    {
        ValidateOrdinaryPath(BackupDirectory, false);
        if (!Directory.Exists(BackupDirectory)) return [];
        var result = new List<GameRecoveryItem>();
        foreach (var file in Directory.EnumerateFiles(BackupDirectory, "*.json").Take(10000))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!ValidId(id) || File.Exists(Path.Combine(BackupDirectory, id + "-committed.json"))
                || File.Exists(Path.Combine(BackupDirectory, id + "-resolved.json"))) continue;
            try
            {
                var record = ReadRecoveryRecord(id);
                // Other configured installations must never become recovery targets.
                if (!string.Equals(record.Target, TargetPath, StringComparison.OrdinalIgnoreCase)) continue;
                VerifyBackup(record);
                var hash = Hash(ReadTarget());
                var state = hash == record.BeforeSha256 ? GameRecoveryState.Unchanged
                    : hash == record.AfterSha256 ? GameRecoveryState.Applied : GameRecoveryState.Conflict;
                // A displaced late external write is never replaced by our older backup.
                if (state == GameRecoveryState.Applied && DisplacedPath(record) is { } displaced && File.Exists(displaced)
                    && Hash(ReadSafe(displaced)) != record.BeforeSha256) state = GameRecoveryState.Conflict;
                var durableDisplaced = Path.Combine(BackupDirectory, id + "-displaced.xml");
                if (state == GameRecoveryState.Applied && File.Exists(durableDisplaced)
                    && Hash(ReadSafe(durableDisplaced)) != record.BeforeSha256) state = GameRecoveryState.Conflict;
                result.Add(new(id, record.TimeUtc, state, state switch
                {
                    GameRecoveryState.Unchanged => "写入中断，当前文件仍是原配置",
                    GameRecoveryState.Applied => "写入已发生，但完成记录中断；可保留当前配置或恢复原配置",
                    _ => "当前文件或被替换文件含外部更改；已保留备份，禁止自动覆盖"
                }));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { result.Add(new(id, DateTimeOffset.MinValue, GameRecoveryState.Invalid, "恢复记录无法安全验证：" + ex.Message)); }
        }
        return result.OrderByDescending(item => item.TimeUtc).ToArray();
    }

    public GameOrderPlan PrepareInterruptedRestore(string id)
    {
        var item = InspectInterrupted().SingleOrDefault(item => item.Id == id);
        if (item?.State != GameRecoveryState.Applied) throw new IOException("配置状态不允许恢复；请重新检查，未覆盖文件。");
        var record = ReadRecoveryRecord(id);
        var current = ReadTarget();
        if (Hash(current) != record.AfterSha256) throw new IOException("当前文件再次变化，已拒绝恢复。");
        var bytes = VerifyBackup(record);
        return new(this, current, bytes, format.Read(new MemoryStream(current)), format.Read(new MemoryStream(bytes)));
    }

    public void ResolveInterrupted(string id)
    {
        using var writeLock = new GameWriteLock(TargetPath);
        var item = InspectInterrupted().SingleOrDefault(item => item.Id == id)
            ?? throw new IOException("恢复项已处理或不存在。");
        if (item.State is GameRecoveryState.Invalid or GameRecoveryState.Conflict)
            throw new IOException("记录存在冲突，不能自动处理；备份已保留。");
        var record = ReadRecoveryRecord(id);
        // Resolution never deletes backups or changes the game. Leftover staging files stay for inspection.
        OrderFileService.WriteNew(Path.Combine(BackupDirectory, id + "-resolved.json"),
            JsonSerializer.SerializeToUtf8Bytes(record with { State = "resolved" }));
    }

    private GameWriteRecord ReadRecoveryRecord(string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("恢复记录标识无效。");
        var file = Path.Combine(BackupDirectory, id + ".json");
        var record = JsonSerializer.Deserialize<GameWriteRecord>(ReadSafe(file, 65536)) ?? throw new InvalidDataException("恢复记录为空。");
        if (record.Schema is not (1 or 2) || record.State != "prepared" || string.IsNullOrWhiteSpace(record.Target) || record.BackupName != id + "-before.xml"
            || !ValidHash(record.BeforeSha256) || !ValidHash(record.AfterSha256)) throw new InvalidDataException("恢复记录不匹配。");
        return record;
    }
    private byte[] VerifyBackup(GameWriteRecord record)
    {
        var bytes = ReadSafe(Path.Combine(BackupDirectory, record.BackupName));
        if (Hash(bytes) != record.BeforeSha256) throw new InvalidDataException("恢复备份校验失败。");
        return bytes;
    }
    private string? DisplacedPath(GameWriteRecord record)
    {
        if (record.DisplacedName is null) return null;
        if (!record.DisplacedName.StartsWith(".rim-displaced-", StringComparison.Ordinal) || !record.DisplacedName.EndsWith(".bak", StringComparison.Ordinal)
            || Path.GetFileName(record.DisplacedName) != record.DisplacedName) throw new InvalidDataException("被替换文件名无效。");
        return Path.Combine(Path.GetDirectoryName(TargetPath)!, record.DisplacedName);
    }
    private static byte[] ReadSafe(string path, int maximum = OrderFileCodec.MaximumBytes)
    {
        ValidateOrdinaryPath(path, true);
        using var input = File.OpenRead(path);
        return OrderFileCodec.ReadBounded(input, maximum);
    }
    private static bool ValidHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidId(string value) => value.Length == 48 && value[8] == '-' && value[15] == '-'
        && value.Where((_, i) => i is not (8 or 15)).All(Uri.IsHexDigit);
}
