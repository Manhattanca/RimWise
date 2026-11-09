using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

internal sealed record IndexedMethod(string Symbol, int Token);
internal sealed record AssemblyMetadataIndex(string Name, string[] References, IndexedMethod[] Methods)
{
    public string[] Types { get; init; } = [];
}
internal sealed record IndexedAssembly(string Id, string PackageId, string Path, string Sha256,
    AssemblyMetadataIndex Metadata);
internal sealed record AssemblyIndexResult(IndexedAssembly[] Assemblies, bool Limited, int CacheHits);

/// <summary>Version-effective metadata only. Hashes are checked on every scan; cache entries never
/// authorize a path. No Assembly.Load, reflection invocation, or mod code execution.</summary>
internal static class LocalAssemblyIndex
{
    internal static AssemblyIndexResult Build(ModCatalogue catalogue, IReadOnlyList<string> order,
        GameVersion version, string cacheRoot, CancellationToken token)
    {
        cacheRoot = ScopedPaths.Ordinary(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        var result = new List<IndexedAssembly>(); var limited = false; var hits = 0;
        var active = order.Select(id => catalogue.Resolve(id, version).UniqueInstallation?.PackageId ?? id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew(); long bytes = 0; var symbols = 0;
        foreach (var id in order)
        {
            token.ThrowIfCancellationRequested();
            if (catalogue.Resolve(id, version).UniqueInstallation is not { } mod) continue;
            try
            {
                var roots = DiagnosticLoadFolders.Select(mod.Directory, version, active, ref limited);
                foreach (var root in roots)
                {
                    var folder = ScopedPaths.Child(root, "Assemblies");
                    if (!Directory.Exists(folder)) continue;
                    foreach (var path in Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly)
                        .Order(StringComparer.OrdinalIgnoreCase))
                    {
                        token.ThrowIfCancellationRequested();
                        if (result.Count >= 1024 || bytes >= 256L * 1024 * 1024 || watch.Elapsed > TimeSpan.FromSeconds(20))
                            return new(result.ToArray(), true, hits);
                        var full = ScopedPaths.Ordinary(path, true);
                        if (result.Any(a => a.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
                        using var parent = PinnedDirectory.Open(Path.GetDirectoryName(full)!);
                        ScopedPaths.Ordinary(full, true);
                        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                        ScopedPaths.Ordinary(full, true);
                        if (stream.Length > 64L * 1024 * 1024) { limited = true; continue; }
                        if (bytes + stream.Length > 256L * 1024 * 1024)
                            return new(result.ToArray(), true, hits);
                        bytes += stream.Length;
                        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                        var cache = ScopedPaths.Child(cacheRoot, "metadata-v2-" + hash + ".json");
                        AssemblyMetadataIndex? metadata = null;
                        if (File.Exists(cache) && new FileInfo(cache).Length <= 4 * 1024 * 1024)
                        {
                            try { metadata = JsonSerializer.Deserialize<AssemblyMetadataIndex>(File.ReadAllText(cache)); }
                            catch (JsonException) { }
                        }
                        if (!Valid(metadata))
                        {
                            stream.Position = 0;
                            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                            if (!pe.HasMetadata) continue;
                            var reader = pe.GetMetadataReader();
                            if (!reader.IsAssembly) continue;
                            var methods = new List<IndexedMethod>();
                            var types = new List<string>();
                            foreach (var handle in reader.TypeDefinitions)
                            {
                                token.ThrowIfCancellationRequested();
                                if (types.Count >= 50000 || watch.Elapsed > TimeSpan.FromSeconds(20))
                                    return new(result.ToArray(), true, hits);
                                var type = reader.GetTypeDefinition(handle);
                                var name = TypeName(reader, handle);
                                if (name != "<Module>") types.Add(name);
                                foreach (var methodHandle in type.GetMethods())
                                {
                                    if (methods.Count >= 50000) { limited = true; break; }
                                    var method = reader.GetMethodDefinition(methodHandle);
                                    methods.Add(new(name + "." + reader.GetString(method.Name), MetadataTokens.GetToken(methodHandle)));
                                }
                                if (methods.Count >= 50000) break;
                            }
                            metadata = new(reader.GetString(reader.GetAssemblyDefinition().Name),
                                reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).ToArray(), methods.ToArray())
                                { Types = types.ToArray() };
                            // Incomplete indexes must not become apparently complete on a later cache hit.
                            if (methods.Count < 50000)
                            {
                                try { WriteCache(cache, metadata); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { limited = true; }
                            }
                        }
                        else hits++;
                        symbols += metadata!.Methods.Length + metadata.Types.Length;
                        if (symbols > 200000) return new(result.ToArray(), true, hits);
                        result.Add(new("assembly-" + result.Count, id, full, hash, metadata!));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or XmlException)
            { limited = true; }
        }
        return new(result.ToArray(), limited, hits);
    }

    private static bool Valid(AssemblyMetadataIndex? item) => item is not null
        && !string.IsNullOrEmpty(item.Name) && item.Name.Length <= 256
        && item.References is { Length: <= 4096 } && item.References.All(r => !string.IsNullOrEmpty(r) && r.Length <= 256)
        && item.Types is { Length: <= 50000 } && item.Types.All(t => !string.IsNullOrEmpty(t) && t.Length <= 512)
        && item.Methods is { Length: < 50000 } && item.Methods.All(m => m is not null
            && !string.IsNullOrEmpty(m.Symbol) && m.Symbol.Length <= 512
            && (m.Token & unchecked((int)0xff000000)) == 0x06000000 && (m.Token & 0xffffff) != 0)
        && item.Methods.Select(m => m.Token).Distinct().Count() == item.Methods.Length;

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle, int depth = 0)
    {
        if (depth > 64) throw new BadImageFormatException("Nested type depth exceeds limit.");
        var type = reader.GetTypeDefinition(handle);
        var parent = type.GetDeclaringType();
        if (!parent.IsNil) return TypeName(reader, parent, depth + 1) + "+" + reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return (ns.Length == 0 ? "" : ns + ".") + reader.GetString(type.Name);
    }

    internal static void WriteCache<T>(string path, T value)
    {
        path = ScopedPaths.Ordinary(path);
        using var parent = PinnedDirectory.Open(Path.GetDirectoryName(path)!);
        var temporary = ScopedPaths.Ordinary(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, value);
            ScopedPaths.Ordinary(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
