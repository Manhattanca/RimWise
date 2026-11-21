using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Updates;

/// <summary>A release-bound, exact-base binary delta. Reconstruction never writes to the installed copy.</summary>
public static class BinaryUpdatePackage
{
    private const string IndexName = "delta.json";
    private const string SumsName = "SHA256SUMS.txt";
    private const int MaximumFiles = 4096;
    private const long MaximumTotalSize = 1024L * 1024 * 1024;
    private const int FileLimit = WindowsBinaryDelta.MaximumFileSize;
    private static readonly Regex Stable = new(@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
    private sealed record Item(string Path, int Size, string Sha256, string? BaseSha256, string Kind);
    private sealed record Index(int Schema, string From, string To, string FromManifestSha256, int SetupSize, string SetupSha256, Item[] Files);

    public static bool CanUpgrade(string from, string to) => Stable.IsMatch(from) && Stable.IsMatch(to) &&
        Version.TryParse(from, out var basis) && Version.TryParse(to, out var target) &&
        basis.Major == target.Major && basis.Minor == target.Minor && target.Build - basis.Build == 1;

    public static string FileName(string from, string to) => $"RimWise-v{from}-to-v{to}-Delta.zip";
    private static string SetupName(string version) => $"RimWise-v{version}-Setup.exe";

    public static void Create(string source, string target, string from, string to, string setup, string output)
    {
        if (!CanUpgrade(from, to) || Path.GetFileName(setup) != SetupName(to) || Path.GetFileName(output) != FileName(from, to))
            throw new InvalidDataException("差分包仅支持同一主、次版本内的相邻正式补丁版本，且文件名必须与版本匹配。");
        var old = Inventory(source, from).Where(p => p.Path != SumsName).ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var files = Inventory(target, to);
        var setupBytes = ReadFile(setup, 16 * 1024 * 1024);
        var items = new List<Item>();
        using var destination = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create);
        Write(zip, SetupName(to), setupBytes);
        for (var i = 0; i < files.Length; i++)
        {
            var entry = files[i];
            var bytes = ReadFile(ScopedPaths.Child(target, entry.Path), FileLimit, entry.Sha256);
            if (old.TryGetValue(entry.Path, out var basis) && Same(basis.Sha256, entry.Sha256))
            {
                items.Add(new(entry.Path, bytes.Length, entry.Sha256, basis.Sha256, "copy"));
                continue;
            }
            if (basis is not null && bytes.Length > 1024)
            {
                var oldBytes = ReadFile(ScopedPaths.Child(source, basis.Path), FileLimit, basis.Sha256);
                var patch = WindowsBinaryDelta.Create(oldBytes, bytes);
                if (patch.Length < bytes.Length)
                {
                    if (!WindowsBinaryDelta.Apply(oldBytes, patch, bytes.Length).AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException("差异包生成后的回读校验失败。");
                    Write(zip, EntryName(i), patch);
                    items.Add(new(entry.Path, bytes.Length, entry.Sha256, basis.Sha256, "msdelta"));
                    continue;
                }
            }
            items.Add(new(entry.Path, bytes.Length, entry.Sha256, null, "full"));
            Write(zip, EntryName(i), bytes);
        }
        Write(zip, IndexName, JsonSerializer.SerializeToUtf8Bytes(new Index(1, from, to,
            old[PackageManifest.FileName].Sha256, setupBytes.Length, Hash(setupBytes), [.. items])));
    }

    public static PreparedBinaryUpdate Prepare(string archive, string source, ReleaseDownload delta,
        CancellationToken token)
    {
        if (delta.FromVersion is not { } from || !CanUpgrade(from, delta.Version) ||
            Path.GetFileName(archive) != FileName(from, delta.Version)) throw new InvalidDataException("差异包版本不符。");
        using var input = new FileStream(ScopedPaths.Ordinary(archive, true), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != delta.Size || !Same(Convert.ToHexString(SHA256.HashData(input)), delta.Sha256))
            throw new InvalidDataException("差异包校验失败。");
        input.Position = 0;
        var staging = new PreparedBinaryUpdate(Directory.CreateTempSubdirectory("RimWise-update-").FullName);
        try
        {
            // The archive itself is authenticated by the release asset digest; its names never become extraction paths.
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var index = JsonSerializer.Deserialize<Index>(ReadEntry(zip, IndexName, 2 * 1024 * 1024));
            if (index is not { Schema: 1, Files: { Length: > 0 and <= MaximumFiles } } || index.From != from ||
                index.To != delta.Version || !IsHash(index.SetupSha256) || !IsHash(index.FromManifestSha256) || index.Files.Any(item => item is null) ||
                index.Files.Sum(item => (long)item.Size) > MaximumTotalSize || zip.Entries.Count > 2 + MaximumFiles ||
                zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
                throw new InvalidDataException("差异包清单无效。");
            var (_, baseFingerprint) = PackageManifest.Read(source, from);
            if (!Same(baseFingerprint, index.FromManifestSha256)) throw new InvalidDataException("差异包来源版本的文件清单不符。");
            var setup = ReadEntry(zip, SetupName(index.To), 16 * 1024 * 1024, index.SetupSize, index.SetupSha256);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < index.Files.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = index.Files[i];
                if (!IsPayloadPath(item.Path) || !names.Add(item.Path) || !IsHash(item.Sha256) || item.Size is < 0 or > FileLimit)
                    throw new InvalidDataException("差异包含非法文件记录。");
                byte[] bytes;
                if (item.Kind == "full" && item.BaseSha256 is null)
                    bytes = ReadEntry(zip, EntryName(i), item.Size);
                else if ((item.Kind is "copy" or "msdelta") && item.BaseSha256 is { } basis && IsHash(basis))
                {
                    var old = ReadFile(ScopedPaths.Child(source, item.Path), FileLimit, basis);
                    bytes = item.Kind == "copy" ? old : WindowsBinaryDelta.Apply(old, ReadEntry(zip, EntryName(i), FileLimit), item.Size);
                }
                else throw new InvalidDataException("不支持的差异操作。");
                Verify(bytes, item.Size, item.Sha256);
                staging.Write("payload/" + item.Path, bytes, item.Sha256);
            }
            var expected = Inventory(Path.Combine(staging.DirectoryPath, "payload"), index.To);
            if (expected.Length != index.Files.Length || expected.Any(file => !names.Contains(file.Path)))
                throw new InvalidDataException("重建程序与完整发布清单不一致。");
            foreach (var file in expected)
                _ = ReadFile(ScopedPaths.Child(staging.DirectoryPath, "payload/" + file.Path), FileLimit, file.Sha256);
            staging.InstallerPath = staging.Write(SetupName(index.To), setup, index.SetupSha256);
            staging.Installer = delta with { FileName = SetupName(index.To), Size = setup.Length, Sha256 = index.SetupSha256,
                IsInstaller = true, FromVersion = null };
            return staging;
        }
        catch { staging.Dispose(); throw; }
    }

    private static PackageFile[] Inventory(string root, string version)
    {
        var (package, fingerprint) = PackageManifest.Read(root, version);
        var file = ScopedPaths.Child(root, SumsName);
        var bytes = ReadFile(file, 2 * 1024 * 1024);
        var inventory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var lines = new StringReader(System.Text.Encoding.UTF8.GetString(bytes));
        while (lines.ReadLine() is { } line)
        {
            if (line.Length <= 66 || line[64..66] != "  " || !IsHash(line[..64]) || !IsPayloadPath(line[66..]) ||
                !inventory.TryAdd(line[66..], line[..64])) throw new InvalidDataException("发布 SHA-256 清单格式不符。");
        }
        if (inventory.Count != package.Files.Length + 1 || !inventory.TryGetValue(PackageManifest.FileName, out var manifestHash) ||
            !Same(manifestHash, fingerprint) || package.Files.Any(p => !inventory.TryGetValue(p.Path, out var hash) || !Same(hash, p.Sha256)))
            throw new InvalidDataException("发布文件清单不一致。");
        return [.. package.Files, new(PackageManifest.FileName, fingerprint), new(SumsName, Hash(bytes))];
    }

    internal static bool IsPayloadPath(string path) => path is SumsName or PackageManifest.FileName || PackageManifest.AllowedPath(path);
    private static string EntryName(int index) => $"data/{index:D4}";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static void Verify(byte[] bytes, int length, string? hash)
    {
        if (bytes.Length != length || (hash is not null && !Same(Hash(bytes), hash)))
            throw new InvalidDataException("更新文件长度或 SHA-256 不符。");
    }

    private static byte[] ReadFile(string file, int limit, string? hash = null)
    {
        using var input = new FileStream(ScopedPaths.Ordinary(file, true), FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadExact(input, input.Length, limit, hash);
    }

    private static byte[] ReadEntry(ZipArchive zip, string name, int limit, int? length = null, string? hash = null)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("差异包缺少必要文件。");
        if (length is { } size && entry.Length != size) throw new InvalidDataException("差异文件大小不符。");
        using var stream = entry.Open();
        return ReadExact(stream, entry.Length, limit, hash);
    }

    private static byte[] ReadExact(Stream input, long length, int limit, string? hash)
    {
        if (length < 0 || length > limit) throw new InvalidDataException("更新文件超过允许大小。");
        var bytes = new byte[(int)length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("更新文件未按声明大小结束。");
        Verify(bytes, (int)length, hash);
        return bytes;
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(bytes);
    }
}

public sealed class PreparedBinaryUpdate : IDisposable
{
    private const string LedgerName = "rimwise-staged-update.json";
    private const string DirectoryPrefix = "RimWise-update-";
    private sealed record StagedFile(string Path, string Sha256);
    private readonly List<string> files = [];
    private readonly List<StagedFile> verifiedFiles = [];
    private bool handedOff;
    internal string DirectoryPath { get; }
    public string InstallerPath { get; internal set; } = "";
    public ReleaseDownload Installer { get; internal set; } = null!;

    internal PreparedBinaryUpdate(string directory) => DirectoryPath = directory;

    internal string Write(string relative, byte[] bytes, string? sha256 = null)
    {
        var path = ScopedPaths.Child(DirectoryPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.CreateNew);
        files.Add(relative);
        output.Write(bytes);
        if (sha256 is not null) verifiedFiles.Add(new(relative, sha256));
        return path;
    }

    public void VerifyPayload(CancellationToken token)
    {
        foreach (var file in verifiedFiles)
        {
            token.ThrowIfCancellationRequested();
            if (!Matches(DirectoryPath, file)) throw new InvalidDataException("重建的更新文件已变化或丢失。");
        }
    }

    // Write before starting Setup so both normal and failed installer exits are reclaimable.
    public void RecordHandoff()
    {
        if (files.Contains(LedgerName)) return;
        Write(LedgerName, JsonSerializer.SerializeToUtf8Bytes(verifiedFiles));
    }

    public void HandOff() => handedOff = true;

    public static void ReclaimExpired()
    {
        try
        {
            var temp = ScopedPaths.Ordinary(Path.GetTempPath(), true);
            foreach (var stage in Directory.EnumerateDirectories(temp, DirectoryPrefix + "*"))
                ReclaimStage(stage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void ReclaimStage(string stage)
    {
        try
        {
            var ledger = ScopedPaths.Child(stage, LedgerName);
            var info = new FileInfo(ledger);
            if (!info.Exists || info.Length > 2 * 1024 * 1024 || info.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-10)) return;
            StagedFile[]? records;
            using (var stream = new FileStream(ledger, FileMode.Open, FileAccess.Read, FileShare.None))
                records = JsonSerializer.Deserialize<StagedFile[]>(stream);
            if (records is not { Length: > 1 and <= 4097 } || records.Any(file => file is null || file.Path is null ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit))) return;
            var installers = records.Where(file => Regex.IsMatch(file.Path, @"\ARimWise-v[0-9]+\.[0-9]+\.[0-9]+-Setup\.exe\z",
                RegexOptions.CultureInvariant)).ToArray();
            if (installers.Length != 1 || records.Except(installers).Any(file =>
                !file.Path.StartsWith("payload/", StringComparison.Ordinal) || !BinaryUpdatePackage.IsPayloadPath(file.Path[8..]))) return;
            var installer = installers[0];
            // Remove Setup first; a running Windows installer keeps this file in use.
            var setup = ScopedPaths.Child(stage, installer.Path);
            if (File.Exists(setup))
            {
                if (!Matches(stage, installer)) return;
                File.Delete(setup);
            }
            var staged = new PreparedBinaryUpdate(stage);
            foreach (var file in records.Except(installers))
                if (Matches(stage, file)) staged.files.Add(file.Path);
            staged.files.Add(LedgerName);
            staged.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static bool Matches(string root, StagedFile file)
    {
        var path = ScopedPaths.Child(root, file.Path);
        return File.Exists(path) && string.Equals(PackageManifest.HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (handedOff) return;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DirectoryPath };
        foreach (var relative in files)
        {
            try
            {
                var path = ScopedPaths.Child(DirectoryPath, relative);
                File.Delete(path);
                for (var parent = Path.GetDirectoryName(path); parent != DirectoryPath && parent is not null; parent = Path.GetDirectoryName(parent))
                    directories.Add(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var path in directories.OrderByDescending(p => p.Length))
            try { Directory.Delete(ScopedPaths.Ordinary(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
