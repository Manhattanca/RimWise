using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Formats;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Configuration;

public sealed record EditorRecoveryItem(string Id, DateTimeOffset TimeUtc, string Source, OrderFileDocument Document);

/// <summary>Crash snapshots of the editor, kept separate from the game's configuration and saves.</summary>
public sealed class EditorRecoveryStore
{
    private readonly string root;
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly long processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    public EditorRecoveryStore(string applicationDirectory) => root = ScopedPaths.Child(applicationDirectory, "Data/Recovery");

    public void Save(OrderFileDocument document, string source)
    {
        ScopedPaths.Ordinary(root); Directory.CreateDirectory(root);
        var snapshotFormat = document.SourceFormat == OrderFileFormat.SavegameRws ? OrderFileFormat.RmlXml : document.SourceFormat;
        var bytes = new OrderFileCodec().Write(document, snapshotFormat);
        var snapshot = new Snapshot(1, Environment.ProcessId, processStart, DateTimeOffset.UtcNow, source,
            Convert.ToHexString(SHA256.HashData(bytes)), bytes);
        var temporary = ScopedPaths.Child(root, id + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        var target = ScopedPaths.Child(root, id + ".json");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, snapshot); output.Flush(true); }
            ScopedPaths.Ordinary(target);
            // Same-volume rename: interruption leaves the old complete snapshot or the new one.
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<EditorRecoveryItem> ReadAbandoned()
    {
        ScopedPaths.Ordinary(root);
        if (!Directory.Exists(root)) return [];
        var result = new List<EditorRecoveryItem>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json").Take(100))
        {
            var fileId = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(fileId, "N", out _) || fileId == id) continue;
            ScopedPaths.Ordinary(path, true);
            using var input = File.OpenRead(path);
            if (input.Length > 48 * 1024 * 1024) throw new InvalidDataException("恢复快照过大。");
            var snapshot = JsonSerializer.Deserialize<Snapshot>(input) ?? throw new InvalidDataException("恢复快照为空。");
            if (snapshot.Schema != 1 || snapshot.Bytes is null || snapshot.Bytes.Length > OrderFileCodec.MaximumBytes
                || Convert.ToHexString(SHA256.HashData(snapshot.Bytes)) != snapshot.Hash) throw new InvalidDataException("恢复快照校验失败，已保留原文件。");
            if (IsLive(snapshot)) continue;
            result.Add(new(fileId, snapshot.TimeUtc, snapshot.Source, new OrderFileCodec().Read(new MemoryStream(snapshot.Bytes))));
        }
        return result.OrderByDescending(x => x.TimeUtc).ToArray();
    }

    public void ClearCurrent() => Remove(id);
    public void Remove(string snapshotId)
    {
        if (!Guid.TryParseExact(snapshotId, "N", out _)) throw new ArgumentException("快照标识无效。");
        var path = ScopedPaths.Child(root, snapshotId + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
    private static bool IsLive(Snapshot snapshot)
    {
        try { using var process = Process.GetProcessById(snapshot.ProcessId); return process.StartTime.ToUniversalTime().Ticks == snapshot.ProcessStart && !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
    }
    private sealed record Snapshot(int Schema, int ProcessId, long ProcessStart, DateTimeOffset TimeUtc, string Source, string Hash, byte[] Bytes);
}
