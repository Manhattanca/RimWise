// Adapted from pardeike/DecompilerServer, commit 42c3f9106c20e5597cd11bcab70c5937d101f03e.
// Copyright Andreas Pardeike; MIT license in ../LICENSE.
// RimWise changes: explicit allowlist resolution, image ownership and size accounting;
// no unrestricted resolver fallback or symbol/source downloads.
using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Metadata;

namespace DecompilerServer.Services;

internal sealed class OwnedAssemblyResolver : IAssemblyResolver, IDisposable
{
    private const PEStreamOptions StreamOptions = PEStreamOptions.PrefetchEntireImage;


    private readonly ConcurrentDictionary<string, Lazy<MetadataFile?>> _assemblies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<MetadataFile?>> _modules = new(StringComparer.Ordinal);
    private bool _disposed;

    public OwnedAssemblyResolver(string mainAssemblyFileName)
    {
        // RimWise resolves only the broker's exact allowlist.
    }

    internal int ResolvedFileCount => _assemblies.Values.Count(value => value.IsValueCreated && value.Value != null)
        + _modules.Values.Count(value => value.IsValueCreated && value.Value != null);

    internal long ResolvedImageBytes => _assemblies.Values.Concat(_modules.Values).Where(value => value.IsValueCreated)
        .Select(value => value.Value).OfType<PEFile>().Distinct().Sum(file => (long)file.Reader.GetEntireImage().Length);

    internal static PEStreamOptions ResolvedAssemblyStreamOptions => StreamOptions;

    public void AddSearchDirectory(string directory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Search directories are deliberately ignored.
    }

    public MetadataFile? Resolve(IAssemblyReference reference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _assemblies.GetOrAdd(
            reference.FullName,
            _ => new Lazy<MetadataFile?>(
                () => RimWise.Decompiler.AssemblyReadPolicy.Resolve(reference.Name) is { } path ? RimWise.Decompiler.AssemblyReadPolicy.Open(path) : null,
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference)
    {
        return Task.FromResult(Resolve(reference));
    }

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = $"{mainModule.FileName}\0{moduleName}";
        return _modules.GetOrAdd(
            key,
            _ => new Lazy<MetadataFile?>(
                () => null,
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName)
    {
        return Task.FromResult(ResolveModule(mainModule, moduleName));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        var resolvedFiles = _assemblies.Values
            .Concat(_modules.Values)
            .Where(value => value.IsValueCreated)
            .Select(value => value.Value)
            .OfType<IDisposable>()
            .Distinct()
            .ToList();

        _assemblies.Clear();
        _modules.Clear();

        foreach (var resolvedFile in resolvedFiles)
            resolvedFile.Dispose();

        _disposed = true;
    }
}
