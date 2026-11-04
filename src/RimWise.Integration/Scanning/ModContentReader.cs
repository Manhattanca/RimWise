using System.Reflection.PortableExecutable;
using RimWise.Core;

namespace RimWise.Integration.Scanning;

public sealed record ModContentInfo(DateTimeOffset? LastUpdatedUtc, ModCodeKind CodeKind, ModContentKinds Kinds);

/// <summary>One bounded traversal for timestamps and content badges; never loads mod assemblies.</summary>
public static class ModContentReader
{
    public static ModContentInfo Read(string root, CancellationToken cancellationToken = default, DateTimeOffset? steamTime = null)
    {
        var incomplete = false;
        var managed = false;
        var kinds = ModContentKinds.None;
        DateTimeOffset? latest = null;
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        try
        {
            ReadOnlyMetadata.CheckPath(root, root);
            kinds = ReadShallowKinds(root, cancellationToken);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlyMetadata.CheckPath(directory, root);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++count > 200_000) { incomplete = true; pending.Clear(); break; }
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { incomplete = true; continue; }
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry.FullName);
                        continue;
                    }
                    if (steamTime is null)
                    {
                        var time = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                        if (time.Year >= 1970 && (latest is null || time > latest)) latest = time;
                    }
                    if (!managed && entry.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            ReadOnlyMetadata.CheckPath(entry.FullName, root);
                            using var stream = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using var pe = new PEReader(stream);
                            if (pe.HasMetadata)
                            {
                                managed = true; kinds |= ModContentKinds.ManagedCode;
                                // Steam already supplied update time: a confirmed managed DLL needs no further type search.
                                if (steamTime is not null) return new(steamTime, ModCodeKind.CSharp, kinds);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
                        { incomplete = true; }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { incomplete = true; }
        return new(steamTime ?? (incomplete ? null : latest), managed ? ModCodeKind.CSharp : incomplete ? ModCodeKind.Unknown : ModCodeKind.Xml, kinds);
    }

    private static ModContentKinds ReadShallowKinds(string root, CancellationToken cancellationToken)
    {
        var kinds = ModContentKinds.None;
        var count = 0;
        foreach (var entry in new DirectoryInfo(root).EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 20_000) throw new IOException("Content folder limit reached.");
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var kind = FolderKind(entry.Name, ModContentKinds.None);
            kinds |= kind;
            // Content layouts commonly put these folders in the root or one version/load folder below it.
            if (kind != ModContentKinds.None || entry.Name.Equals("About", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("Source", StringComparison.OrdinalIgnoreCase)) continue;
            ReadOnlyMetadata.CheckPath(entry.FullName, root);
            foreach (var child in entry.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 20_000) throw new IOException("Content folder limit reached.");
                if ((child.Attributes & FileAttributes.ReparsePoint) == 0) kinds |= FolderKind(child.Name, ModContentKinds.None);
            }
        }
        return kinds;
    }

    private static ModContentKinds FolderKind(string folder, ModContentKinds inherited)
    {
        if (folder.Equals("Defs", StringComparison.OrdinalIgnoreCase)) return ModContentKinds.Definitions;
        if (folder.Equals("Patches", StringComparison.OrdinalIgnoreCase)) return ModContentKinds.Patches;
        if (folder.Equals("Languages", StringComparison.OrdinalIgnoreCase)) return ModContentKinds.Translation;
        if (folder.Equals("Textures", StringComparison.OrdinalIgnoreCase)) return ModContentKinds.Textures;
        if (folder.Equals("Sounds", StringComparison.OrdinalIgnoreCase)) return ModContentKinds.Audio;
        if (folder.Equals("AssetBundles", StringComparison.OrdinalIgnoreCase)) return ModContentKinds.AssetBundles;
        return inherited;
    }
}
