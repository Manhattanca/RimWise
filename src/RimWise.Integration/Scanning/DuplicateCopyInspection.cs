using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using RimWise.Core;
using RimWise.Integration.Game;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Scanning;

public sealed record DuplicateCopyInspection(ModInstallation Installation, string Version, string Fingerprint,
    string Status, bool HasFileErrors, bool Limited);

/// <summary>Recommendations only; neither metadata nor a syntactically valid DLL proves runtime compatibility.</summary>
public static class DuplicateCopyInspector
{
    public static DuplicateCopyInspection Inspect(ModInstallation mod, GameVersion? gameVersion,
        IReadOnlySet<string> active, CancellationToken token)
    {
        var issues = new List<string>();
        var metadataIssues = new List<string>();
        var limited = false;
        var version = "";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            ScopedPaths.Ordinary(mod.Directory, true);
            var about = Path.Combine(mod.Directory, "About", "About.xml");
            ModMetadata? metadata = null;
            RecoveredIdentity? identity = null;
            try { metadata = new ModMetadataReader().Read(about, mod.Directory, gameVersion); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException)
            {
                limited = true;
                metadataIssues.Add("About/About.xml 元数据未完整读取，保留副本待测试：" + ex.Message);
                try { identity = new ModMetadataReader().ReadIdentity(about, mod.Directory); }
                catch (Exception identityError) when (identityError is IOException or InvalidDataException or UnauthorizedAccessException or XmlException) { }
            }
            var packageId = metadata?.PackageId ?? identity?.PackageId;
            if (packageId is not null && !packageId.Equals(mod.PackageId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("包名已变化，请刷新后重试。");
            if (!ModMetadataReader.IsValidRecoveredPackageId(mod.PackageId))
                throw new InvalidDataException("副本组标识无效，请刷新后重试。");
            version = metadata?.ReleaseVersion ?? "";
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { Path.Combine(mod.Directory, "About", "About.xml") };
            var load = Path.Combine(mod.Directory, "LoadFolders.xml");
            if (File.Exists(load)) files.Add(load);
            foreach (var root in DiagnosticLoadFolders.Select(mod.Directory, gameVersion, active, ref limited))
                foreach (var name in new[] { "Defs", "Patches", "Assemblies" })
                {
                    var folder = Path.Combine(root, name);
                    if (!Directory.Exists(folder)) continue;
                    var pending = new Stack<string>(); pending.Push(folder);
                    var count = 0;
                    while (pending.TryPop(out var directory))
                    {
                        token.ThrowIfCancellationRequested();
                        ScopedPaths.Ordinary(directory, true);
                        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                        {
                            if (++count > 20000) { limited = true; pending.Clear(); break; }
                            ScopedPaths.Ordinary(path, true);
                            if (Directory.Exists(path)) pending.Push(path);
                            else if (Path.GetExtension(path).ToLowerInvariant() is ".xml" or ".dll") files.Add(path);
                        }
                    }
                }
            long bytes = 0;
            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var isMetadata = file.Equals(about, StringComparison.OrdinalIgnoreCase);
                try
                {
                    using var parent = PinnedDirectory.Open(Path.GetDirectoryName(file)!);
                    ScopedPaths.Ordinary(file, true);
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > 64L * 1024 * 1024 || (bytes += stream.Length) > 256L * 1024 * 1024)
                    { limited = true; continue; }
                    hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(mod.Directory, file) + "\0"));
                    hash.AppendData(SHA256.HashData(stream));
                    // About.xml was already parsed through the stricter metadata reader.
                    // Its syntax failure must not mask genuine errors in Defs/Patches/DLLs.
                    if (isMetadata) continue;
                    stream.Position = 0;
                    try
                    {
                        if (Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                        {
                            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                                XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 });
                            while (reader.Read()) token.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                            if (pe.HasMetadata) _ = pe.GetMetadataReader().GetAssemblyDefinition();
                            else limited = true; // Native libraries are not managed assemblies, not necessarily damaged.
                        }
                    }
                    catch (Exception ex) when (ex is XmlException or BadImageFormatException)
                    { issues.Add(Path.GetRelativePath(mod.Directory, file) + "：" + ex.Message); }
                }
                catch (Exception ex) when (isMetadata && ex is (IOException or InvalidDataException or UnauthorizedAccessException or XmlException))
                { limited = true; metadataIssues.Add("About/About.xml 读取受限，保留副本待测试：" + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException)
        { issues.Add(ex.Message); }
        var status = issues.Count > 0 ? string.Join("\n", issues.Take(4)) : limited
            ? "检查不完整，运行情况待测试" : "文件结构检查通过，运行情况待测试";
        if (metadataIssues.Count > 0) status += "\n" + string.Join("\n", metadataIssues.Take(2));
        return new(mod, version, Convert.ToHexString(hash.GetHashAndReset()), status, issues.Count > 0, limited);
    }

    public static IReadOnlyList<DuplicateCopyInspection> Rank(IEnumerable<DuplicateCopyInspection> copies) => copies
        .OrderBy(c => c.HasFileErrors)
        .ThenBy(c => c.Installation.Source == ModSource.Workshop ? 0 : 1)
        .ThenByDescending(c => NumericVersion(c.Version))
        .ThenBy(c => c.Limited)
        .ThenBy(c => ulong.TryParse(c.Installation.WorkshopId, out var id) ? id : ulong.MaxValue)
        .ThenBy(c => c.Installation.FolderName, StringComparer.Ordinal)
        .ThenBy(c => c.Installation.Directory, StringComparer.Ordinal).ToArray();

    private static Version? NumericVersion(string value)
    {
        var parts = value.Trim().TrimStart('v', 'V').Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;
        return System.Version.TryParse(string.Join('.', parts.Concat(Enumerable.Repeat("0", 4 - parts.Length))), out var v) ? v : null;
    }
}
