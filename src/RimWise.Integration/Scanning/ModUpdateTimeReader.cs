using RimWise.Core;
using RimWise.Integration.Steam;

namespace RimWise.Integration.Scanning;

/// <summary>Reads timestamps during the cancellable background scan, never mod file contents.</summary>
public sealed class ModUpdateTimeReader
{
    private readonly Dictionary<string, KeyValueText?> manifests = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumEntries = 200_000;

    public DateTimeOffset? Read(ModInstallation mod, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mod.Source == ModSource.Game) return null;
        try
        {
            ReadOnlyMetadata.CheckPath(mod.Directory, mod.Directory);
            if (mod.Source == ModSource.Workshop && SteamTime(mod) is { } steamTime) return steamTime;
            // The mod folder/About.xml timestamps miss updates confined to DLLs, Defs or textures.
            // Enumerate metadata only, skip links, and abandon incomplete scans instead of inventing a date.
            DateTimeOffset? latest = null;
            var pending = new Stack<string>();
            pending.Push(mod.Directory);
            var visited = 0;
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlyMetadata.CheckPath(directory, mod.Directory);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > MaximumEntries) return null;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Push(entry.FullName);
                    else
                    {
                        var time = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                        if (time.Year >= 1970 && (latest is null || time > latest)) latest = time;
                    }
                }
            }
            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    internal DateTimeOffset? SteamTime(ModInstallation mod)
    {
        var app = Directory.GetParent(mod.Directory);
        if (app?.Name != "294100" || app.Parent?.Name != "content" || app.Parent.Parent is not { } workshop) return null;
        var path = Path.Combine(workshop.FullName, "appworkshop_294100.acf");
        if (!manifests.TryGetValue(path, out var manifest))
        {
            try
            {
                manifest = KeyValueText.Parse(ReadOnlyMetadata.ReadText(path, workshop.FullName, 4 * 1024 * 1024))
                    .Block("AppWorkshop")?.Block("WorkshopItemsInstalled");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { manifest = null; }
            manifests[path] = manifest;
        }
        var raw = manifest?.Block(mod.WorkshopId)?.Value("timeupdated");
        if (!long.TryParse(raw, out var seconds) || seconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
