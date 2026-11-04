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
        var limited = false;
        var version = "";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var metadata = new ModMetadataReader().Read(Path.Combine(mod.Directory, "About", "About.xml"), mod.Directory, gameVersion);
            if (!metadata.PackageId.Equals(mod.PackageId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("包名已变化，请刷新后重试。");
            version = metadata.ReleaseVersion;
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
                using var parent = PinnedDirectory.Open(Path.GetDirectoryName(file)!);
                ScopedPaths.Ordinary(file, true);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 64L * 1024 * 1024 || (bytes += stream.Length) > 256L * 1024 * 1024)
                { limited = true; continue; }
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(mod.Directory, file) + "\0"));
                hash.AppendData(SHA256.HashData(stream));
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
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException)
        { issues.Add(ex.Message); }
        var status = issues.Count > 0 ? string.Join("\n", issues.Take(4)) : limited
            ? "检查不完整，运行情况待测试" : "文件结构检查通过，运行情况待测试";
        return new(mod, version, Convert.ToHexString(hash.GetHashAndReset()), status, issues.Count > 0, limited);
    }

    public static IReadOnlyList<DuplicateCopyInspection> Rank(IEnumerable<DuplicateCopyInspection> copies) => copies
        .OrderBy(c => c.HasFileErrors).ThenByDescending(c => NumericVersion(c.Version))
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
