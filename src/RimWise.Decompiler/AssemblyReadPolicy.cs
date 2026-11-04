using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ICSharpCode.Decompiler.Metadata;
using RimWise.Integration.Safety;

namespace RimWise.Decompiler;

public sealed record ApprovedAssembly(string Id, string Path, string Sha256, string Name);

/// <summary>Applies at the actual PE read, including transitive resolution. PDB/source/network
/// resolution is disabled. This is an application read boundary, not an OS sandbox.</summary>
internal static class AssemblyReadPolicy
{
    private static ApprovedAssembly[] approved = [];
    internal static void Configure(ApprovedAssembly[] files)
    {
        if (files is null || files.Length > 1024 || files.Any(f => f is null
            || string.IsNullOrEmpty(f.Id) || f.Id.Length > 128 || string.IsNullOrEmpty(f.Name) || f.Name.Length > 256
            || f.Sha256 is null || f.Sha256.Length != 64 || !f.Sha256.All(char.IsAsciiHexDigit))
            || files.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != files.Length
            || files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new ArgumentException("Invalid assembly allowlist.");
        approved = files.Select(f => f with { Path = ScopedPaths.Ordinary(f.Path, true) }).ToArray();
    }

    internal static ApprovedAssembly Find(string id) => approved.Single(f => f.Id == id);
    internal static string? Resolve(string name)
    {
        var candidates = approved.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0].Path : null;
    }

    internal static PEFile Open(string path)
    {
        path = ScopedPaths.Ordinary(path, true);
        var file = approved.Single(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        using var parent = PinnedDirectory.Open(System.IO.Path.GetDirectoryName(path)!);
        ScopedPaths.Ordinary(path, true);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ScopedPaths.Ordinary(path, true);
        if (input.Length > 64 * 1024 * 1024) throw new IOException("Assembly exceeds read limit.");
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        var bytes = copy.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Assembly changed since inspection.");
        return new PEFile(path, new MemoryStream(bytes, writable: false), PEStreamOptions.PrefetchEntireImage,
            MetadataReaderOptions.Default);
    }
}
