namespace RimWise.Core;

/// <summary>Game definitions precede ordinary mods even when About.xml omits that implicit rule.
/// Explicit early loaders and their prerequisites remain before the content they prepare.</summary>
internal static class GameContentOrder
{
    private static readonly string[] Content = ["ludeon.rimworld", "ludeon.rimworld.royalty",
        "ludeon.rimworld.ideology", "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey"];

    internal static IReadOnlyList<(string Before, string After)> Pairs(ModCatalogue catalogue,
        IReadOnlyList<string> order, GameVersion? version)
    {
        var entries = order.Distinct(StringComparer.OrdinalIgnoreCase).Select(id =>
            (Id: id, Mod: catalogue.Resolve(id, version).UniqueInstallation)).ToArray();
        var official = entries.Where(e => e.Mod?.Source == ModSource.Game
            && (Content.Contains(e.Mod.PackageId, StringComparer.OrdinalIgnoreCase)
                || version?.UsesLegacyIdentifiers == true && e.Mod.FolderName.Equals("Core", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Mod!.FolderName.Equals("Core", StringComparison.OrdinalIgnoreCase) ? 0
                : Array.FindIndex(Content, id => id.Equals(e.Mod!.PackageId, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (official.Length == 0) return [];
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            aliases[e.Id] = e.Id;
            if (e.Mod?.PackageId.Length > 0) aliases[e.Mod.PackageId] = e.Id;
        }
        var parents = entries.ToDictionary(e => e.Id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        void Add(string before, string after)
        {
            if (aliases.TryGetValue(before, out var a) && aliases.TryGetValue(after, out var b)) parents[b].Add(a);
        }
        foreach (var e in entries)
        {
            if (e.Mod?.Metadata?.Requirements is not { } r) continue;
            foreach (var dependency in r.Dependencies) Add(dependency.PackageId, e.Id);
            foreach (var before in r.LoadBefore) Add(e.Id, before);
            foreach (var after in r.LoadAfter) Add(after, e.Id);
        }
        var result = new List<(string, string)>();
        for (int i = 1; i < official.Length; i++)
        {
            Add(official[i - 1].Id, official[i].Id);
            result.Add((official[i - 1].Id, official[i].Id));
        }
        foreach (var content in official)
        {
            var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(parents[content.Id]);
            while (pending.TryPop(out var id))
                if (ancestors.Add(id)) foreach (var parent in parents[id]) pending.Push(parent);
            foreach (var e in entries)
                if (e.Mod is not null && e.Mod.Source != ModSource.Game && !ancestors.Contains(e.Id))
                    result.Add((content.Id, e.Id));
        }
        return result;
    }
}
