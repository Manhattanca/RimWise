using System.Text.Json;
using RimWise.Integration.Configuration;

namespace RimWise.Integration.AI;

public sealed record AiAuditEntry(
    DateTimeOffset Timestamp,
    string Operation,
    AiPermissionLevel PermissionLevel,
    bool Succeeded,
    string Message,
    int AcceptedActions,
    IReadOnlyList<string> Rejections);

public sealed class AiAuditStore(string applicationDirectory)
{
    private readonly AppJsonFile<Document> file = new(applicationDirectory, "ai-audit", 8 * 1024 * 1024);

    public IReadOnlyList<AiAuditEntry> Load() => file.Load()?.Entries ?? [];

    public void Append(AiAuditEntry entry)
    {
        var entries = Load().Append(entry).TakeLast(1000).ToArray();
        file.Save(new Document(1, entries));
    }

    private sealed record Document(int SchemaVersion, IReadOnlyList<AiAuditEntry> Entries);
}
