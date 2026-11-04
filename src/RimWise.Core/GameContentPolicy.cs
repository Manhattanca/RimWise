namespace RimWise.Core;

/// <summary>Protects game content already present in an order when removing other entries.</summary>
public static class GameContentPolicy
{
    // Exact identifiers also protect missing/unscanned game content. A publisher prefix alone
    // is insufficient: ordinary mods may use a similar identifier.
    private static readonly HashSet<string> KnownPackageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology",
        "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey"
    };

    public static bool KeepWhenClearing(string identifier, GameVersion? version)
    {
        if (version?.UsesLegacyIdentifiers != true && KnownPackageIds.Contains(identifier)) return true;
        if (version?.UsesLegacyIdentifiers != false && identifier.Equals("Core", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
