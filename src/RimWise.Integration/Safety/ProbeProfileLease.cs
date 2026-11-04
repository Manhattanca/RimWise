using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWise.Integration.Safety;

internal interface IProbeProfileStore
{
    string Owner { get; }
    string Sid(string profile);
    void Create(string profile);
    bool Remove(string profile);
}

internal sealed record ProbeProfileIntent(int Version, string Profile, string Sid, string Owner);
public sealed record ProbeProfileRecovery(string Directory, bool Removed, bool Active, string Message);

/// <summary>
/// Development-only profile ownership journal. A flushed, immutable intent precedes registration;
/// the OS releases the exclusive lease when the host dies. Recovery never reads a deletion path
/// or arbitrary profile name from the journal: both are derived from its fixture directory ID.
/// This is crash recovery, not protection against another malicious process of the same user.
/// </summary>
internal sealed class ProbeProfileLease : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly FileStream lease;
    private readonly IProbeProfileStore store;
    internal string Profile { get; }
    internal string Sid { get; }

    private ProbeProfileLease(string directory, IProbeProfileStore store, bool create, bool game = false)
    {
        directory = ScopedPaths.Ordinary(directory, true);
        var name = Path.GetFileName(directory);
        if (!name.StartsWith("isolation-", StringComparison.Ordinal) || name.Length != 42
            || !Guid.TryParseExact(name[10..], "N", out var id))
            throw new InvalidDataException("不是独立探针目录。");
        Profile = (game ? "RimWise.GameTest." : "RimWise.Probe.") + id.ToString("N");
        this.store = store;
        Sid = store.Sid(Profile);
        lease = new FileStream(ScopedPaths.Child(directory, "profile.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = ScopedPaths.Child(directory, "profile-intent.json");
            if (create)
            {
                // Do not replace an existing journal or register if durable ownership fails.
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(output, new ProbeProfileIntent(1, Profile, Sid, store.Owner));
                output.Flush(flushToDisk: true);
            }
            else
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length is 0 or > 4096) throw new InvalidDataException("探针所有权记录大小无效。");
                // Explicitly reject duplicate keys; JsonSerializer otherwise accepts the last value.
                using var json = JsonDocument.Parse(input);
                if (json.RootElement.ValueKind != JsonValueKind.Object
                    || json.RootElement.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() != 1))
                    throw new InvalidDataException("探针所有权记录结构无效。");
                var intent = json.RootElement.Deserialize<ProbeProfileIntent>(Json);
                // Reconstruct the only permitted legacy name from this directory's nonce;
                // never trust a journal-provided arbitrary profile or deletion path.
                var legacy = (game ? "RimSortWorkbench.GameTest." : "RimSortWorkbench.Probe.") + id.ToString("N");
                if (intent?.Profile == legacy)
                {
                    Profile = legacy;
                    Sid = store.Sid(Profile);
                }
                if (intent != new ProbeProfileIntent(1, Profile, Sid, store.Owner))
                    throw new InvalidDataException("探针所有权不匹配，拒绝清理。");
            }
        }
        catch { lease.Dispose(); throw; }
    }

    internal static ProbeProfileLease Prepare(string directory, IProbeProfileStore store) => new(directory, store, true);
    internal static ProbeProfileLease PrepareGame(string directory, IProbeProfileStore store) => new(directory, store, true, true);
    internal static ProbeProfileLease OpenGame(string directory, IProbeProfileStore store) => new(directory, store, false, true);
    internal void Create() => store.Create(Profile);
    // Cleanup may be retried after partial registration or after a crash during cleanup.
    // Never skip inspection merely because a previous cleanup receipt claims success.
    internal bool Remove() => store.Remove(Profile);
    public void Dispose() => lease.Dispose();

    internal static ProbeProfileRecovery Recover(string directory, IProbeProfileStore store)
    {
        try
        {
            using var owner = new ProbeProfileLease(directory, store, false);
            var removed = owner.Remove();
            return new(directory, removed, false, removed ? "临时配置已清理并核实不存在。" : "临时配置清理未确认，保留记录以便重试。");
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { return new(directory, false, true, "探针仍持有独占锁，未清理。"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException
            or System.Security.SecurityException)
        { return new(directory, false, false, "拒绝或无法清理：" + ex.Message); }
    }
}
