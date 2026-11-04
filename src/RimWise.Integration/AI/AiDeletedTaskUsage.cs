using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.AI;

/// <summary>A deletion receipt contains billing evidence only, never a resumable order or actions.</summary>
public sealed record AiDeletedTaskUsage(string Id, DateTimeOffset DeletedUtc, int ReportedTokens,
    int ReservedTokens, bool UnknownUsage)
{
    public AiUsageRequest[] UsageRequests { get; init; } = [];
    public AiUsagePeriod[] UsagePeriods { get; init; } = [];
}

internal sealed class AiDeletedTaskUsageStore(string applicationDirectory)
{
    private readonly string root = ScopedPaths.Child(applicationDirectory, "Data/DeletedAiTasks");
    private sealed record Envelope(string Hash, byte[] Payload);

    private string PathFor(string id, string suffix = "")
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("任务编号无效。");
        return ScopedPaths.Child(root, id + suffix + ".json");
    }

    public bool Contains(string id) => File.Exists(PathFor(id));

    public IReadOnlyList<AiDeletedTaskUsage> Inspect()
    {
        if (!Directory.Exists(ScopedPaths.Ordinary(root))) return [];
        var files = Directory.GetFiles(root, "*.json");
        if (files.Length > 10000) throw new InvalidDataException("费用记录过多，请先归档。");
        return files.Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out _))
            .Select(f => Read(Path.GetFileNameWithoutExtension(f))).ToArray();
    }

    public AiDeletedTaskUsage Read(string id)
    {
        var original = ReadFile(PathFor(id), id);
        var reconciled = PathFor(id, ".settled");
        if (!File.Exists(reconciled)) return original;
        var value = ReadFile(reconciled, id);
        if (value.UnknownUsage || value.ReservedTokens != 0 || value.ReportedTokens < original.ReportedTokens
            || value.DeletedUtc != original.DeletedUtc
            || JsonSerializer.Serialize(value.UsageRequests) != JsonSerializer.Serialize(original.UsageRequests)
            || JsonSerializer.Serialize(value.UsagePeriods) != JsonSerializer.Serialize(original.UsagePeriods))
            throw new InvalidDataException("费用核对记录无效。");
        return value;
    }

    public void Write(AiDeletedTaskUsage value) => Publish(PathFor(value.Id), value);

    public void Reconcile(string id, AiTokenUsage usage)
    {
        var old = Read(id);
        if (!old.UnknownUsage) throw new InvalidOperationException("该任务没有待核对用量。");
        Publish(PathFor(id, ".settled"), old with { UnknownUsage = false, ReservedTokens = 0,
            ReportedTokens = checked(old.ReportedTokens + usage.Total) });
    }

    private static AiDeletedTaskUsage ReadFile(string path, string id)
    {
        using var input = File.OpenRead(ScopedPaths.Ordinary(path, true));
        if (input.Length > 262144) throw new InvalidDataException("费用记录过大。");
        var item = JsonSerializer.Deserialize<Envelope>(input) ?? throw new InvalidDataException("费用记录为空。");
        if (item.Payload is null || item.Hash != Convert.ToHexString(SHA256.HashData(item.Payload)))
            throw new InvalidDataException("费用记录校验失败。");
        var value = JsonSerializer.Deserialize<AiDeletedTaskUsage>(item.Payload);
        if (value is null || value.Id != id || value.DeletedUtc == default || value.ReportedTokens < 0
            || value.ReservedTokens < 0) throw new InvalidDataException("费用记录无效。");
        AiTaskCheckpoint.ValidateUsage(value.UsageRequests, value.UsagePeriods);
        return value;
    }

    private void Publish(string target, AiDeletedTaskUsage value)
    {
        Directory.CreateDirectory(ScopedPaths.Ordinary(root));
        var temporary = ScopedPaths.Child(root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, new Envelope(Convert.ToHexString(SHA256.HashData(payload)), payload)); output.Flush(true); }
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
