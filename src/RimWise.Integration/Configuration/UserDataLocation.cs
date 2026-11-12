using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Configuration;

/// <summary>Version-independent storage locator. Stores still receive a base with a Data child;
/// the executable base is reserved for installed resources such as SteamBridge.</summary>
public sealed class UserDataLocation : IDisposable
{
    private sealed record Location(string Current, string? Pending = null, string? CleanupSource = null, string[]? PreviousRoots = null);
    private readonly string anchor;
    private readonly string installation;
    private readonly string pointer;
    private FileStream? lease;
    private Location location;
    public string DirectoryPath => location.Current;
    public string? StartupNotice { get; private set; }

    public UserDataLocation(string anchorDirectory, string installationDirectory, string? legacyDirectory = null)
    {
        anchor = ScopedPaths.Ordinary(anchorDirectory);
        installation = ScopedPaths.Ordinary(installationDirectory);
        Directory.CreateDirectory(anchor);
        pointer = ScopedPaths.Child(anchor, "user-data-location.json");
        // Serialize locator decisions briefly. Ordinary sessions share a read lease; only
        // migration needs exclusivity. A running editor must not block another version.
        using var control = AppDataFileLock.Acquire(ScopedPaths.Child(anchor, ".location.lock"));
        try
        {
            var established = File.Exists(pointer);
            location = established ? ReadLocation() : new(Path.Combine(anchor, "UserData"));
            ScopedPaths.Ordinary(location.Current);
            if (established && !Directory.Exists(ScopedPaths.Child(location.Current, "Data")))
                throw new IOException("原用户数据目录不可用。请恢复目录或连接对应磁盘后重新启动，避免创建空配置。");
            if (location.Pending is { } target)
            {
                // Acquire outside the rollback handler: a live peer is not a failed move.
                using var moving = ExclusiveSession();
                try
                {
                    ValidateDestination(target);
                    // Keep the independently configured export folder stable when the profile moves.
                    var logs = new Diagnostics.AutomaticSortingLogStore(location.Current);
                    logs.SaveSettings(logs.LoadSettings());
                    CopyData(location.Current, target);
                    var next = new Location(target, CleanupSource: location.Current,
                        PreviousRoots: (location.PreviousRoots ?? []).Append(location.Current).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                    WriteLocation(next);
                    location = next;
                    StartupNotice = "用户数据已迁移。";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    WriteLocation(location with { Pending = null });
                    location = location with { Pending = null };
                    StartupNotice = "用户数据迁移失败，继续使用原目录：" + ex.Message;
                }
            }
            if (location.CleanupSource is { } previous)
            {
                try
                {
                    using var cleaning = ExclusiveSession();
                    if (Overlaps(previous, location.Current)) throw new IOException("迁移清理目录重叠，原文件已保留。");
                    CleanupCopiedTree(ScopedPaths.Child(previous, "Data"), ScopedPaths.Child(location.Current, "Data"));
                    if (Directory.Exists(ScopedPaths.Child(previous, "Data")))
                        throw new IOException("部分原文件被占用或内容发生变化，已保留，下次启动将重试。");
                    // Only remove an empty profile folder; unrelated files beside Data stay intact.
                    if (Directory.Exists(previous) && !Directory.EnumerateFileSystemEntries(previous).Any())
                        Directory.Delete(ScopedPaths.Ordinary(previous));
                    WriteLocation(location with { CleanupSource = null });
                    location = location with { CleanupSource = null };
                    StartupNotice = "用户数据已迁移，原目录中的已迁移数据已清理。";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { StartupNotice = "正在使用新用户数据目录，原目录清理未完成：" + ex.Message; }
            }
            // Never re-import an old version after the stable location has been established.
            if (!File.Exists(pointer))
            {
                using var importing = ExclusiveSession();
                var legacy = legacyDirectory ?? FindLegacyDirectory(installation);
                if (legacy is not null && !Same(legacy, location.Current))
                {
                    EnsureLegacyClosed(legacy);
                    CopyData(legacy, location.Current);
                }
                Directory.CreateDirectory(ScopedPaths.Child(location.Current, "Data"));
                WriteLocation(location);
            }
            Directory.CreateDirectory(ScopedPaths.Child(location.Current, "Data"));
            Diagnostics.StoredEvidence.ConfigureRelocation(location.Current, location.PreviousRoots ?? []);
            lease = SharedSession();
        }
        catch { lease?.Dispose(); throw; }
    }

    public static void ClearOwnedData(string anchorDirectory, string installationDirectory)
    {
        var anchor = ScopedPaths.Ordinary(anchorDirectory);
        var installation = ScopedPaths.Ordinary(installationDirectory);
        Directory.CreateDirectory(anchor);
        var pointer = ScopedPaths.Child(anchor, "user-data-location.json");
        using var control = AppDataFileLock.Acquire(ScopedPaths.Child(anchor, ".location.lock"));
        FileStream session;
        try
        {
            session = new FileStream(ScopedPaths.Child(anchor, ".session.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) { throw new IOException("请先关闭所有 RimWise 窗口，再清除用户数据。", ex); }
        using (session)
        {
            Location location;
            if (File.Exists(pointer))
            {
                if (new FileInfo(pointer).Length > 16384) throw new InvalidDataException("用户数据位置记录过大，未清除任何数据。");
                location = JsonSerializer.Deserialize<Location>(File.ReadAllBytes(pointer))
                    ?? throw new InvalidDataException("用户数据位置记录无效，未清除任何数据。");
                if (string.IsNullOrWhiteSpace(location.Current)) throw new InvalidDataException("用户数据位置记录无效，未清除任何数据。");
            }
            else location = new(Path.Combine(anchor, "UserData"));

            var roots = new[] { location.Current }.Concat(location.PreviousRoots ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var candidate in roots)
            {
                var root = ScopedPaths.Ordinary(candidate);
                if (Same(root, Path.GetPathRoot(root)!) || Overlaps(root, installation)
                    || Overlaps(root, anchor) && !Same(root, Path.Combine(anchor, "UserData")))
                    throw new IOException("用户数据位置与安装或定位目录重叠，已停止清理以保护其他文件。");
            }

            // Delete only the application-owned Data subtree. A custom profile root may
            // contain unrelated files alongside Data, so the root itself is never recursed.
            foreach (var candidate in roots)
            {
                var root = ScopedPaths.Ordinary(candidate);
                var data = ScopedPaths.Child(root, "Data");
                if (Directory.Exists(data)) DeleteOwnedTree(data);
                if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                    Directory.Delete(root);
            }

            if (File.Exists(pointer)) File.Delete(ScopedPaths.Ordinary(pointer, true));
        }
    }

    private static void DeleteOwnedTree(string directory)
    {
        ScopedPaths.Ordinary(directory, true);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            ScopedPaths.Ordinary(entry, true);
            if (Directory.Exists(entry)) DeleteOwnedTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }

    public void PrepareMove(string destination, IEnumerable<string>? protectedDirectories = null)
    {
        using var control = AppDataFileLock.Acquire(ScopedPaths.Child(anchor, ".location.lock"));
        using var moving = PauseForMove();
        location = ReadLocation();
        if (location.CleanupSource is not null)
            throw new IOException("上次迁移的原目录尚未清理完成，请关闭其他窗口并重启后再更换目录。");
        destination = ScopedPaths.Ordinary(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (Same(destination, location.Current)) return;
        ValidateDestination(destination);
        foreach (var protectedDirectory in protectedDirectories ?? [])
            if (!string.IsNullOrWhiteSpace(protectedDirectory) && Overlaps(destination, Path.GetFullPath(protectedDirectory)))
                throw new IOException("用户数据目录不能与游戏、模组或游戏配置目录重叠。请选择独立文件夹。");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("请选择新的或空的文件夹，以免覆盖其他文件。");
        Directory.CreateDirectory(destination);
        var probe = ScopedPaths.Child(destination, ".rimwise-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        File.Delete(probe);
        var next = location with { Pending = destination };
        WriteLocation(next);
        location = next;
    }

    public void CancelMove()
    {
        using var control = AppDataFileLock.Acquire(ScopedPaths.Child(anchor, ".location.lock"));
        location = ReadLocation();
        var next = location with { Pending = null };
        WriteLocation(next);
        location = next;
    }

    private void ValidateDestination(string target)
    {
        target = ScopedPaths.Ordinary(target);
        if (Same(target, Path.GetPathRoot(target)!) || Overlaps(target, location.Current)
            || Overlaps(target, installation) || (Overlaps(target, anchor) && !Same(target, Path.Combine(anchor, "UserData"))))
            throw new IOException("请选择独立的用户数据文件夹，不能使用原目录的上级、下级或软件安装目录。");
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86 })
        {
            var system = Environment.GetFolderPath(folder);
            if (system.Length > 0 && Overlaps(target, system)) throw new IOException("不能将用户数据存入系统或程序目录。");
        }
    }

    private Location ReadLocation()
    {
        if (new FileInfo(pointer).Length > 16384) throw new InvalidDataException("用户数据位置记录过大。");
        var value = JsonSerializer.Deserialize<Location>(File.ReadAllBytes(pointer));
        if (value is null || string.IsNullOrWhiteSpace(value.Current)) throw new InvalidDataException("用户数据位置记录无效。");
        return value;
    }

    private void WriteLocation(Location value)
    {
        var temporary = ScopedPaths.Child(anchor, ".location-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(value)); file.Flush(true); }
            File.Move(temporary, pointer, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CopyData(string sourceBase, string targetBase)
    {
        var source = ScopedPaths.Child(sourceBase, "Data");
        var destination = ScopedPaths.Child(targetBase, "Data");
        if (!Directory.Exists(source)) { Directory.CreateDirectory(destination); return; }
        // Retain the source until the verified destination and locator are committed.
        // The deterministic staging name permits resuming after a power interruption.
        Directory.CreateDirectory(targetBase);
        var staging = ScopedPaths.Child(targetBase, ".rimwise-migration");
        if (Directory.Exists(destination))
        {
            VerifyTree(source, destination); // crash after rename, before pointer commit
            return;
        }
        CopyTree(source, staging);
        VerifyTree(source, staging);
        Directory.Move(staging, destination);
    }

    private static void CopyTree(string source, string target)
    {
        ScopedPaths.Ordinary(source, true);
        ScopedPaths.Ordinary(target);
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ScopedPaths.Ordinary(entry, true);
            var output = ScopedPaths.Child(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, output);
            else
            {
                using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(file);
                file.Flush(true);
            }
        }
    }

    private static void VerifyTree(string source, string target)
    {
        ScopedPaths.Ordinary(source, true);
        ScopedPaths.Ordinary(target, true);
        var sourceNames = Directory.GetFileSystemEntries(source).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var targetNames = Directory.GetFileSystemEntries(target).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!sourceNames.SequenceEqual(targetNames)) throw new IOException("迁移文件核对失败，原数据已保留。");
        foreach (var name in sourceNames)
        {
            var a = ScopedPaths.Child(source, name!);
            var b = ScopedPaths.Child(target, name!);
            if (Directory.Exists(a) && Directory.Exists(b)) VerifyTree(a, b);
            else if (File.Exists(a) && File.Exists(b))
            {
                using var left = File.OpenRead(a);
                using var right = File.OpenRead(b);
                if (!SHA256.HashData(left).SequenceEqual(SHA256.HashData(right)))
                    throw new IOException("迁移文件校验失败，原数据已保留。");
            }
            else throw new IOException("迁移目录结构不一致，原数据已保留。");
        }
    }

    private static void CleanupCopiedTree(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        ScopedPaths.Ordinary(source, true);
        ScopedPaths.Ordinary(target, true);
        using (var pins = new PinnedDirectorySet())
        {
            pins.PinDirectory(source);
            pins.PinDirectory(target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                ScopedPaths.Ordinary(entry, true);
                var copy = ScopedPaths.Child(target, Path.GetFileName(entry));
                if (Directory.Exists(entry)) CleanupCopiedTree(entry, copy);
                else
                {
                    // A restart after partial cleanup is safe. Never delete a changed source
                    // or the only remaining copy, even if the destination has since been edited.
                    ScopedPaths.Ordinary(copy, true);
                    using (var left = File.OpenRead(entry))
                    using (var right = File.OpenRead(copy))
                        if (!SHA256.HashData(left).SequenceEqual(SHA256.HashData(right))) continue;
                    File.Delete(ScopedPaths.Ordinary(entry, true));
                }
            }
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    private static string? FindLegacyDirectory(string installation)
    {
        if (Directory.Exists(Path.Combine(installation, "Data"))) return installation;
        var parent = Path.GetDirectoryName(installation.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null || !Path.GetFileName(installation.TrimEnd(Path.DirectorySeparatorChar)).StartsWith('v')) return null;
        return Directory.EnumerateDirectories(parent, "v*")
            .Select(path => (Path: path, Version: ParseVersion(path)))
            .Where(item => item.Version is not null && item.Version < ParseVersion(installation)
                && File.Exists(Path.Combine(item.Path, "RimWise.exe")) && Directory.Exists(Path.Combine(item.Path, "Data")))
            .OrderByDescending(item => item.Version).Select(item => item.Path).FirstOrDefault();
    }

    private static void EnsureLegacyClosed(string legacy)
    {
        var executable = Path.Combine(legacy, "RimWise.exe");
        if (!File.Exists(executable)) return;
        foreach (var process in Process.GetProcessesByName("RimWise"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (InvalidOperationException) { continue; } // Already exited.
                catch (System.ComponentModel.Win32Exception ex)
                { throw new IOException("请先保存并退出其他版本的 RimWise，再迁移旧数据。", ex); }
                if (path is not null && Same(path, executable))
                    throw new IOException("旧版本 RimWise 仍在运行。请先保存并退出旧版本，再启动新版迁移数据。");
            }
        }
    }

    private static Version? ParseVersion(string path) => Version.TryParse(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))
        .TrimStart('v').Split('-')[0], out var version) ? version : null;
    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static bool Overlaps(string a, string b) => Same(a, b) || Inside(a, b) || Inside(b, a);
    private static bool Inside(string child, string parent) => child.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private FileStream SharedSession()
    {
        var path = ScopedPaths.Child(anchor, ".session.lock");
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
        return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private FileStream ExclusiveSession()
    {
        try { return new FileStream(ScopedPaths.Child(anchor, ".session.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("用户数据迁移需要关闭其他 RimWise 窗口。旧版本独占数据时也需先退出旧版本。", ex); }
    }

    private IDisposable PauseForMove()
    {
        lease?.Dispose();
        try { return new ResumeSession(this, ExclusiveSession()); }
        catch { lease = SharedSession(); throw; }
    }

    private sealed class ResumeSession(UserDataLocation owner, FileStream exclusive) : IDisposable
    {
        public void Dispose() { exclusive.Dispose(); owner.lease = owner.SharedSession(); }
    }

    public void Dispose() => lease?.Dispose();
}
