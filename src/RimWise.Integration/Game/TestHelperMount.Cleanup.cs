using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

internal sealed partial class TestHelperMount
{
    private static readonly string[] PayloadFiles = ["About/About.xml", "Assemblies/RimWise.TestHelper.dll"];
    private sealed record OwnedFile(string Path, string Sha256);
    private sealed record MountRecord(int Schema, string GameDirectory, OwnedFile[] Files);

    private static void CreateOwnedPayload(OrdinaryGameTestOptions options, string session)
    {
        var target = ScopedPaths.Child(session, "fixed-helper");
        if (Directory.Exists(target)) throw new IOException("测试辅助组件目录已存在，未覆盖现有内容。");
        using var about = new MemoryStream();
        new XDocument(new XElement("ModMetaData", new XElement("name", "RimWise Test Helper"),
            new XElement("author", "RimWise contributors"), new XElement("packageId", GameProbe.PackageId),
            new XElement("supportedVersions", Enumerable.Range(1, 7).Select(n => new XElement("li", "1." + n)))))
            .Save(about);
        using var assembly = new MemoryStream();
        using (var input = typeof(TestHelperMount).Assembly.GetManifestResourceStream("RimWise.TestHelper.dll")
            ?? throw new IOException("测试辅助组件缺失。")) input.CopyTo(assembly);
        var bytes = new[] { about.ToArray(), assembly.ToArray() };
        var record = new MountRecord(1, ScopedPaths.Ordinary(options.GameDirectory, true),
            PayloadFiles.Select((path, i) => new OwnedFile(path, Convert.ToHexString(SHA256.HashData(bytes[i])))).ToArray());
        var journal = ScopedPaths.Child(session, "helper-mount.json");
        if (File.Exists(journal))
        {
            var previous = ReadRecord(journal);
            if (JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(record))
                throw new IOException("测试辅助组件记录已变化，未覆盖旧记录。");
        }
        else OrdinaryGameRunner.WriteNew(journal, record); // Durable ownership precedes deployment.
        WriteState(session, false, "mounted");
        for (var i = 0; i < PayloadFiles.Length; i++)
        {
            var path = ScopedPaths.Child(target, PayloadFiles[i]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes[i]);
        }
    }

    internal static void CleanInterrupted(OrdinaryGameTestOptions options)
    {
        if (!Directory.Exists(options.TestRoot)) return;
        using var lease = new FileStream(ScopedPaths.Child(options.TestRoot, "ordinary-game-test.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CleanInterruptedOwnedRoot(options.GameDirectory, options.TestRoot);
    }

    // Caller holds the same lease used while launching/running tests. Startup never stops a game.
    internal static void CleanInterruptedOwnedRoot(string gameDirectory, string testRoot)
    {
        if (GameProcessService.IsAnyRunning()) return;
        gameDirectory = ScopedPaths.Ordinary(gameDirectory, true);
        if (!Directory.Exists(ScopedPaths.Child(gameDirectory, "Mods"))) return;
        foreach (var session in Directory.EnumerateDirectories(ScopedPaths.Ordinary(testRoot, true), "game-*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(session)[5..], "N", out _)) continue;
            TryClean(gameDirectory, session);
        }
    }

    private static void CleanAfterUse(OrdinaryGameTestOptions options, string session)
    {
        if (GameProcessService.IsAnyRunning())
        {
            RecordPending(session, "游戏仍在运行，辅助组件清理延后。");
            return;
        }
        TryClean(options.GameDirectory, session);
    }

    private static void TryClean(string gameDirectory, string session)
    {
        try
        {
            ScopedPaths.Ordinary(session, true);
            var journal = ScopedPaths.Child(session, "helper-mount.json");
            var target = ScopedPaths.Child(session, "fixed-helper");
            var mods = ScopedPaths.Child(gameDirectory, "Mods");
            var id = Path.GetFileName(session)[5..];
            MountRecord? record;
            if (File.Exists(journal))
            {
                record = ReadRecord(journal);
                if (!string.Equals(record.GameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase)) return;
            }
            else
            {
                // Older releases have no helper manifest. Require their installation journal
                // and exact helper metadata before adopting the two fixed payload files.
                record = AdoptLegacy(gameDirectory, session, target);
                if (record is null) return;
                OrdinaryGameRunner.WriteNew(journal, record);
            }
            Remove(Path.Combine(mods, "RimWise-Test-" + id), target);
            Remove(Path.Combine(mods, "RimSortWorkbench-Test-" + id), target);
            if (Directory.Exists(target))
            {
                // No recursive delete: verify every existing owned file before deleting any.
                using (var pins = new PinnedDirectorySet())
                {
                    foreach (var file in record.Files)
                    {
                        var path = ScopedPaths.Child(target, file.Path);
                        if (File.Exists(path))
                        {
                            pins.PinParent(path);
                            if (Hash(path) != file.Sha256) throw new IOException("测试辅助组件内容已变化，已保留供检查。");
                        }
                    }
                    foreach (var file in record.Files)
                    {
                        var path = ScopedPaths.Child(target, file.Path);
                        if (File.Exists(path)) File.Delete(path);
                    }
                }
                foreach (var name in new[] { "About", "Assemblies", "" })
                {
                    var directory = name.Length == 0 ? target : ScopedPaths.Child(target, name);
                    if (!Directory.Exists(directory)) continue;
                    using var pins = new PinnedDirectorySet();
                    pins.PinParent(directory);
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, recursive: false);
                }
                if (Directory.Exists(target)) throw new IOException("辅助组件目录含未登记内容，已保留，未删除玩家文件。");
            }
            WriteState(session, true, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or JsonException
            or ArgumentException or XmlException)
        { RecordPending(session, ex.Message); }
    }

    private static MountRecord ReadRecord(string path)
    {
        using var input = File.OpenRead(ScopedPaths.Ordinary(path, true));
        if (input.Length is 0 or > 16384) throw new InvalidDataException("辅助组件归属记录大小无效。");
        var record = JsonSerializer.Deserialize<MountRecord>(input, new JsonSerializerOptions { MaxDepth = 8 });
        if (record is not { Schema: 1, Files.Length: 2 } || record.Files.Any(f => f is null)
            || string.IsNullOrEmpty(record.GameDirectory)
            || !record.Files.Select(f => f.Path).SequenceEqual(PayloadFiles, StringComparer.Ordinal)
            || record.Files.Any(f => f.Sha256 is null || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("辅助组件归属记录无效，未删除内容。");
        return record;
    }

    private static MountRecord? AdoptLegacy(string gameDirectory, string session, string target)
    {
        var journal = ScopedPaths.Child(session, "session.json");
        if (!File.Exists(journal)) return null;
        using (var input = File.OpenRead(journal))
        {
            if (input.Length is 0 or > 4 * 1024 * 1024) return null;
            using var json = JsonDocument.Parse(input);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var version) || version != 1
                || !json.RootElement.TryGetProperty("Executable", out var executable)
                || executable.ValueKind != JsonValueKind.String
                || !string.Equals(executable.GetString(), GameProcessService.FindExecutable(gameDirectory), StringComparison.OrdinalIgnoreCase))
                return null;
        }
        var mods = ScopedPaths.Child(gameDirectory, "Mods");
        var id = Path.GetFileName(session)[5..];
        // The verified link can be detached even if its old payload has disappeared
        // or changed. Adopting/deleting payload files requires the extra checks below.
        Remove(Path.Combine(mods, "RimWise-Test-" + id), target);
        Remove(Path.Combine(mods, "RimSortWorkbench-Test-" + id), target);
        if (!Directory.Exists(target))
        {
            return null;
        }
        var about = ScopedPaths.Child(target, PayloadFiles[0]);
        if (!File.Exists(about)) throw new InvalidDataException("旧辅助组件缺少归属信息，目录链接已移除，本体已保留。");
        using (var input = File.OpenRead(about))
        using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 16384 }))
        {
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "ModMetaData" || (string?)root.Element("packageId") != GameProbe.PackageId
                || (string?)root.Element("name") != "RimWise Test Helper")
                throw new InvalidDataException("旧辅助组件归属不匹配，目录链接已移除，本体已保留。");
        }
        return new(1, gameDirectory, PayloadFiles.Select(path => new OwnedFile(path,
            File.Exists(ScopedPaths.Child(target, path)) ? Hash(ScopedPaths.Child(target, path)) : new string('0', 64))).ToArray());
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(ScopedPaths.Ordinary(path, true));
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void WriteState(string session, bool complete, string? reason)
    {
        var path = ScopedPaths.Child(session, "helper-cleanup.json");
        var temporary = ScopedPaths.Child(session, "helper-cleanup.json.pending");
        using var pins = new PinnedDirectorySet();
        pins.PinDirectory(session);
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { Schema = 1, Complete = complete, Reason = reason,
            UpdatedUtc = DateTime.UtcNow }));
        File.Move(temporary, path, overwrite: true);
    }

    private static void RecordPending(string session, string reason)
    {
        DiagnosticEventLog.Current?.Record("test-helper", "cleanup-pending", Path.GetFileName(session) + ": " + reason);
        try { WriteState(session, false, reason); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { DiagnosticEventLog.Current?.Record("test-helper", "cleanup-record-failed", ex.Message); }
    }
}
