namespace RimWise.Core;

/// <summary>A sequence of package identifiers, independent of UI and file formats.</summary>
public sealed class LoadOrder
{
    public IReadOnlyList<string> PackageIds { get; }

    public LoadOrder(IEnumerable<string> packageIds)
    {
        ArgumentNullException.ThrowIfNull(packageIds);
        var entries = packageIds.Select(id =>
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("模组标识不能为空。", nameof(packageIds));
            return id.Trim();
        }).ToArray();

        // Preserve duplicate and unresolved entries; importing must not silently change a list.
        PackageIds = Array.AsReadOnly(entries);
    }

    public LoadOrder Move(int fromIndex, int toIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromIndex, PackageIds.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toIndex, PackageIds.Count);
        var entries = PackageIds.ToList();
        var moved = entries[fromIndex];
        entries.RemoveAt(fromIndex);
        entries.Insert(toIndex, moved);
        return new LoadOrder(entries);
    }

    /// <summary>Use the game's package-ID spelling at the launch/save boundary.
    /// Older versions store folder names, whose spelling must remain unchanged.</summary>
    public LoadOrder ForGame(GameVersion version) => version.UsesLegacyIdentifiers
        ? this : new LoadOrder(PackageIds.Select(id => id.ToLowerInvariant()));
}
