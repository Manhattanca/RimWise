using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Read-only, bounded ownership lookup in active installations. Never loads or executes mod DLLs.
/// An inactive version folder can also contain a symbol: ownership is a candidate, not load/causality proof.</summary>
internal static class DiagnosticSymbolOwners
{
    internal static AiSymbolOwner[] Find(ModCatalogue catalogue, IReadOnlyList<string> order,
        string[] symbols, CancellationToken token, out bool limited, IReadOnlyList<string>? observedAssemblies = null,
        IReadOnlyList<string>? priorityAssemblyNames = null)
    {
        // Shared framework copies are not evidence that their containing mod caused an exception.
        symbols = symbols.Where(s => !DiagnosticLogContext.IsFrameworkSymbol(s)).Distinct(StringComparer.Ordinal).ToArray();
        var result = new HashSet<AiSymbolOwner>();
        var priority = symbols.Select((symbol, index) => (symbol, index))
            .ToDictionary(item => item.symbol, item => item.index, StringComparer.Ordinal);
        AiSymbolOwner[] Ranked() => result.OrderBy(owner => priority[owner.Symbol]).Take(96).ToArray();
        void AddOwner(AiSymbolOwner owner)
        {
            if (result.Contains(owner)) return;
            if (result.Count >= 96)
            {
                var last = result.MaxBy(item => priority[item.Symbol])!;
                if (priority[owner.Symbol] >= priority[last.Symbol]) return;
                result.Remove(last);
            }
            result.Add(owner);
        }
        var active = order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bytes = 0L; var files = 0; limited = false;
        if (symbols.Length == 0) return [];
        var xmlPatterns = symbols.ToDictionary(s => s, s => new Regex(
            @"(?:\bName\s*=\s*[""']|<(?:defName|workerClass|thingClass|className|hediffClass)>\s*)"
            + Regex.Escape(s) + @"(?=[""'\s<])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hints = (priorityAssemblyNames ?? []).Where(name => name == Path.GetFileName(name)
            && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Take(16).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Cover each installation's standard code folders before recursive fallback.
        // Assembly filenames need not match namespaces. Small per-folder batches keep
        // bundled dependency trees from starving later installations of their turn.
        var namedFiles = new List<string>();
        foreach (var mod in catalogue.Installations.Where(m => active.Contains(m.PackageId)))
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalSeconds > 5 || namedFiles.Count >= 4096) { limited=true; break; }
            if (!Directory.Exists(mod.Directory)) continue;
            try
            {
                ScopedPaths.Ordinary(mod.Directory,true);
                var folders = new[] { mod.Directory }.Concat(Directory.EnumerateDirectories(mod.Directory)
                    .Where(path => (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0
                        && (Path.GetFileName(path)=="Common" || GameVersion.Parse(Path.GetFileName(path)) is not null)).Take(32));
                foreach(var folder in folders)
                {
                    var path = ScopedPaths.Child(folder, "Assemblies");
                    if (!Directory.Exists(path)) continue;
                    var entries = Directory.EnumerateFiles(path, "*.dll")
                        .OrderBy(file => hints.Contains(Path.GetFileName(file)) ? 0 : 1).Take(17).ToArray();
                    if (entries.Length > 16) limited = true;
                    namedFiles.AddRange(entries.Take(16));
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) { limited=true; }
        }
        // Runtime assembly inventories are useful hints, not permission to read arbitrary
        // paths. Only exact DLLs inside one active installation pass this first pass.
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The failing assembly gets its metadata inspected first, even in a very large catalogue.
        // Filenames only prioritize reads; actual TypeDefinitions still establish ownership.
        foreach (var observed in (observedAssemblies ?? []).Concat(namedFiles).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => hints.Contains(Path.GetFileName(path)) ? 0 : 1).Take(4096))
        {
            token.ThrowIfCancellationRequested();
            var owners = catalogue.Installations.Where(m => active.Contains(m.PackageId)
                && DiagnosticPatchEvidence.IsWithin(observed,m.Directory)).ToArray();
            if (owners.Length != 1 || !observed.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) || !scanned.Add(observed)) continue;
            try
            {
                using var stream = new FileStream(ScopedPaths.Ordinary(observed,true),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                if (pe.PEHeaders.MetadataSize > 8*1024*1024) { limited=true; continue; }
                bytes += pe.PEHeaders.MetadataSize; files++;
                if (bytes > 64*1024*1024 || clock.Elapsed.TotalSeconds > 20) { limited=true; break; }
                if (!AddAssemblyOwners(pe.GetMetadataReader(),owners[0].PackageId,symbols,AddOwner,token,clock)) { limited=true; return Ranked(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
            { limited=true; }
        }
        // Search all managed code before XML. Asset-heavy early mods must not hide later code owners.
        foreach (var pattern in new[] { "*.dll", "*.xml" })
        foreach (var mod in catalogue.Installations.Where(m => active.Contains(m.PackageId))
            .OrderBy(m => m.Source == ModSource.Game ? 1 : 0))
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(mod.Directory)) continue;
            try
            {
                ScopedPaths.Ordinary(mod.Directory, true);
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 6 };
                foreach (var path in Directory.EnumerateFiles(mod.Directory, pattern, options))
                {
                    token.ThrowIfCancellationRequested();
                    if (scanned.Contains(path)) continue;
                    // Count inspected candidates; textures/audio do not consume the code/XML quota.
                    if (++files > 12000 || bytes >= 64 * 1024 * 1024 || clock.Elapsed.TotalSeconds > 20)
                    { limited = true; return Ranked(); }
                    var extension = Path.GetExtension(path);
                    if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    var size = new FileInfo(path).Length;
                    if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                        && (size > 8 * 1024 * 1024 || bytes + size > 64 * 1024 * 1024))
                    { limited = true; continue; }
                    using var stream = new FileStream(ScopedPaths.Ordinary(path, true), FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        using var pe = new PEReader(stream);
                        if (!pe.HasMetadata) continue;
                        if (pe.PEHeaders.MetadataSize > 8*1024*1024) { limited=true; continue; }
                        // Read metadata only; embedded assets do not consume the lookup budget.
                        bytes += pe.PEHeaders.MetadataSize;
                        if (bytes > 64 * 1024 * 1024) { limited = true; return Ranked(); }
                        if (!AddAssemblyOwners(pe.GetMetadataReader(),mod.PackageId,symbols,AddOwner,token,clock)) { limited=true; return Ranked(); }
                    }
                    else
                    {
                        bytes += size;
                        // Limit even a file concurrently growing during a Workshop update.
                        var buffer = new byte[(int)size];
                        var read = 0;
                        while (read < buffer.Length)
                        {
                            var count = stream.Read(buffer, read, buffer.Length - read);
                            if (count == 0) break;
                            read += count;
                        }
                        using var reader = new StreamReader(new MemoryStream(buffer, 0, read));
                        var content = reader.ReadToEnd();
                        foreach (var symbol in symbols)
                            if (xmlPatterns[symbol].IsMatch(content)) AddOwner(new(symbol, mod.PackageId, "xml-symbol"));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                or InvalidOperationException or RegexMatchTimeoutException) { limited = true; }
        }
        return Ranked();
    }
    private static bool AddAssemblyOwners(MetadataReader metadata,string packageId,string[] symbols,Action<AiSymbolOwner> addOwner,
        CancellationToken token,System.Diagnostics.Stopwatch clock)
    {
        if (metadata.IsAssembly)
        {
            var identity=metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (identity is "Assembly-CSharp" or "mscorlib" or "netstandard" || identity=="System"
                || identity.StartsWith("System.",StringComparison.Ordinal) || identity=="UnityEngine"
                || identity.StartsWith("UnityEngine.",StringComparison.Ordinal)) return true;
        }
        foreach (var handle in metadata.TypeDefinitions)
        {
            token.ThrowIfCancellationRequested();
            if(clock.Elapsed.TotalSeconds>20) return false;
            var definition=metadata.GetTypeDefinition(handle);
            var name=metadata.GetString(definition.Name); var ns=metadata.GetString(definition.Namespace);
            var fullName=ns.Length==0?name:ns+"."+name;
            foreach(var symbol in symbols.Where(s=>s==fullName || s.StartsWith(fullName+".",StringComparison.Ordinal)))
                addOwner(new(symbol,packageId,"assembly-type"));
        }
        return true;
    }

}
