using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Integration.Configuration;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Diagnostics;

public sealed record LogDirectoryMoveResult(int Moved, int Preserved, bool CleanupPending);

public sealed partial class AutomaticSortingLogStore
{
    private sealed record MoveFile(string Name, string DestinationName, string Hash);
    private sealed record LogMove(string Source, string Destination, MoveFile[] Files, int Preserved, bool Committed = false);
    private sealed record MoveDocument(LogMove? Move);
    private AppJsonFile<MoveDocument> MoveStore => new(profile, "automatic-log-migration", 32 * 1024 * 1024, keepPreviousVersions: false);

    // Export, migration and cleanup share this short-lived file-operation lease across windows.
    public IDisposable AcquireExportLease() => AppDataFileLock.Acquire(ScopedPaths.Child(profile, "Data/automatic-log-operation.lock"));

    public LogDirectoryMoveResult ChangeDirectory(string destination)
    {
        using var operation = AcquireExportLease();
        var journal = MoveStore;
        if (journal.Load()?.Move is { } pending)
        {
            var recovery = ExecuteMove(journal, pending);
            if (recovery.CleanupPending) throw new IOException("上次日志迁移尚有原文件待清理，请关闭占用文件的程序后重试。");
        }
        destination = ValidateDirectory(destination);
        var source = ValidateDirectory(LoadSettings().Directory);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return new(0, 0, false);
        var files = new List<MoveFile>();
        var preserved = 0;
        if (Directory.Exists(source))
            foreach (var file in Directory.EnumerateFiles(source, "RimWise-*.zip"))
            {
                // Both sorting and manually exported diagnostics may share this folder.
                var name = Path.GetFileName(file);
                if (!name.StartsWith("RimWise-sorting-", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("RimWise-diagnostics-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsDiagnosticArchive(file)) { preserved++; continue; }
                var hash = Hash(file);
                var outputName = name;
                var output = ScopedPaths.Child(destination, outputName);
                if (Directory.Exists(output) || File.Exists(output) && Hash(output) != hash)
                    outputName = Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N") + ".zip";
                files.Add(new(name, outputName, hash));
            }
        if (files.Count == 0)
        {
            // Preserve the previous lazy creation behavior when there is nothing to move.
            SaveSettings(LoadSettings() with { Directory = destination });
            return new(0, preserved, false);
        }
        var move = new LogMove(source, destination, files.ToArray(), preserved);
        journal.Save(new(move));
        return ExecuteMove(journal, move);
    }

    public LogDirectoryMoveResult? ResumeDirectoryMove()
    {
        using var operation = AcquireExportLease();
        var journal = MoveStore;
        return journal.Load()?.Move is { } move ? ExecuteMove(journal, move) : null;
    }

    private LogDirectoryMoveResult ExecuteMove(AppJsonFile<MoveDocument> journal, LogMove move)
    {
        if (move.Files is null || move.Files.Any(file => file is null || string.IsNullOrEmpty(file.Name)
            || string.IsNullOrEmpty(file.DestinationName) || string.IsNullOrEmpty(file.Hash)))
            throw new IOException("日志迁移记录无效，原文件已保留。");
        var source = ValidateDirectory(move.Source);
        var destination = ValidateDirectory(move.Destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("日志迁移目录不能相同。");
        var current = LoadSettings();
        if (!string.Equals(current.Directory, source, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(current.Directory, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("日志路径已被其他窗口更改，原日志已保留。");
        Directory.CreateDirectory(destination);
        using var pins = new PinnedDirectorySet();
        pins.PinDirectory(destination);
        if (Directory.Exists(source)) pins.PinDirectory(source);
        foreach (var file in move.Files)
        {
            if (Path.GetFileName(file.Name) != file.Name || Path.GetFileName(file.DestinationName) != file.DestinationName
                || file.Hash.Length != 64 || !file.Hash.All(char.IsAsciiHexDigit))
                throw new IOException("日志迁移记录无效，原文件已保留。");
            var input = ScopedPaths.Child(source, file.Name);
            var output = ScopedPaths.Child(destination, file.DestinationName);
            if (File.Exists(output))
            {
                if (Hash(output) != file.Hash) throw new IOException("目标日志已发生变化，原文件已保留。");
                continue;
            }
            if (Hash(input) != file.Hash) throw new IOException("原日志已发生变化，迁移未完成。");
            var staging = ScopedPaths.Child(destination, "." + file.DestinationName + ".rimwise-migration");
            using (var read = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var write = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            { read.CopyTo(write); write.Flush(true); }
            if (Hash(staging) != file.Hash) throw new IOException("日志迁移校验失败，原文件已保留。");
            File.Move(staging, output);
        }
        // All copies are verified before publishing the new destination or deleting any originals.
        SaveSettings(LoadSettings() with { Directory = destination });
        if (!move.Committed) { move = move with { Committed = true }; journal.Save(new(move)); }
        var pending = false;
        foreach (var file in move.Files)
        {
            var input = ScopedPaths.Child(source, file.Name);
            if (!File.Exists(input)) continue;
            try
            {
                if (Hash(input) != file.Hash || Hash(ScopedPaths.Child(destination, file.DestinationName)) != file.Hash)
                { pending = true; continue; }
                File.Delete(ScopedPaths.Ordinary(input, true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { pending = true; }
        }
        if (!pending) journal.Save(new(null));
        return new(move.Files.Length, move.Preserved, pending);
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(ScopedPaths.Ordinary(path, true));
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static bool IsDiagnosticArchive(string path)
    {
        try
        {
            using var input = File.OpenRead(ScopedPaths.Ordinary(path, true));
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var manifest = zip.GetEntry("manifest.json");
            if (manifest is null || manifest.Length > 1024 * 1024) return false;
            using var data = manifest.Open();
            using var json = JsonDocument.Parse(data);
            return json.RootElement.TryGetProperty("format", out var format) && format.GetString() == "RimWiseDiagnostic/1";
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException) { return false; }
    }
}
