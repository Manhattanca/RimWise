using System.Text.Json;
using RimWise.Integration.AI;

namespace RimWise.Integration.Safety;

public sealed record ExecutionRecord(int Schema, string Id, DateTimeOffset StartedUtc, string Operation,
    AiPermissionLevel Level, string[] Before, string[] After, string[] Notices);
public sealed record RecoveryEntry(ExecutionRecord Record, string State);

/// <summary>Immutable prepared records plus separate completion markers survive interruption.</summary>
public sealed class ExecutionJournal(string applicationDirectory)
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(applicationDirectory), "Data", "Executions");
    public string Prepare(AiOrderPlan plan, AiPermissionLevel level)
    {
        if (!plan.CanApply) throw new InvalidOperationException("计划不能应用。");
        ScopedPaths.Ordinary(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        Write(id + ".json", new ExecutionRecord(1, id, DateTimeOffset.UtcNow, "editor-order", level,
            plan.Before.ToArray(), plan.After.ToArray(), plan.Notices.ToArray()));
        return id;
    }
    public void Complete(string id) => Mark(id, "applied");
    public void Dismiss(string id) => Mark(id, "reviewed");
    private void Mark(string id, string state)
    {
        var path = RecordPath(id);
        if (!File.Exists(path)) throw new FileNotFoundException("执行记录不存在。");
        if (File.Exists(ScopedPaths.Child(DirectoryPath, id + "." + state))) return;
        Write(id + "." + state, new { State = state, TimeUtc = DateTimeOffset.UtcNow });
    }
    public IReadOnlyList<RecoveryEntry> ReadRecent()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        ScopedPaths.Ordinary(DirectoryPath, true);
        var entries = new List<RecoveryEntry>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").OrderDescending().Take(200))
        {
            ScopedPaths.Ordinary(file, true);
            if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new InvalidDataException("执行记录过大。");
            var record = JsonSerializer.Deserialize<ExecutionRecord>(File.ReadAllBytes(file), new JsonSerializerOptions { MaxDepth = 8 });
            if (record is null || record.Schema != 1 || record.Id is null || RecordPath(record.Id) != file
                || record.Operation != "editor-order" || !Enum.IsDefined(record.Level)
                || record.Before is null || record.After is null || record.Notices is null
                || record.Before.Length > 20000 || record.After.Length > 20000
                || record.Before.Concat(record.After).Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)))
                throw new InvalidDataException("执行记录无效。");
            var state = File.Exists(ScopedPaths.Child(DirectoryPath, record.Id + ".reviewed")) ? "reviewed"
                : File.Exists(ScopedPaths.Child(DirectoryPath, record.Id + ".applied")) ? "applied" : "interrupted";
            entries.Add(new(record, state));
        }
        return entries;
    }
    private string RecordPath(string id)
    {
        if (id.Length > 80 || id.Length < 20 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new InvalidDataException("执行记录编号无效。");
        return ScopedPaths.Child(DirectoryPath, id + ".json");
    }
    private void Write<T>(string name, T value)
    {
        var path = ScopedPaths.Child(DirectoryPath, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value);
        stream.Flush(true);
    }
}
