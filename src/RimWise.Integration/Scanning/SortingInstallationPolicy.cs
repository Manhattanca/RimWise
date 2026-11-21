using RimWise.Core;

namespace RimWise.Integration.Scanning;

public sealed record SortingInstallationOrder(string[] Order, string[] RemovedIds, IReadOnlyList<string> Messages)
{
    public IReadOnlyDictionary<string, string> IdentifierMap { get; init; } = new Dictionary<string, string>();
}

/// <summary>Opt-in sorting policy. Does not change ordinary imports or the game's identifier resolution.</summary>
public static class SortingInstallationPolicy
{
    public static IReadOnlyList<ModInstallation> Candidates(ModCatalogue catalogue, string id, GameVersion version)
    {
        var match = catalogue.Resolve(id, version);
        if (version.UsesLegacyIdentifiers) return match.Candidates;
        var package = match.Candidates.FirstOrDefault()?.PackageId ?? id;
        if (match.Candidates.Count == 0 && id.EndsWith("_steam", StringComparison.OrdinalIgnoreCase)
            && catalogue.FindPackage(id[..^6]).Count > 0) package = id[..^6];
        return catalogue.FindPackage(package);
    }

    public static SortingInstallationOrder Normalize(ModCatalogue before, ModCatalogue after,
        IReadOnlyList<string> order, GameVersion version, IReadOnlySet<string> unusablePackages)
    {
        var result = new List<string>(); var removed = new List<string>(); var messages = new List<string>();
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // An unavailable dependency must not be silently re-added by the preparation closure.
        var unavailable = new HashSet<string>(unusablePackages, StringComparer.OrdinalIgnoreCase);
        foreach (var id in order)
            if (Candidates(before, id, version).Count == 0) unavailable.Add(id);
        var dependents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in after.Installations.GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            var copies = group.ToArray();
            if (copies.Length != 1) continue;
            foreach (var dependency in copies[0].Metadata?.Requirements.Dependencies ?? [])
            {
                if (!dependents.TryGetValue(dependency.PackageId, out var children))
                    dependents[dependency.PackageId] = children = new(StringComparer.OrdinalIgnoreCase);
                children.Add(copies[0].PackageId);
            }
        }
        var pending = new Queue<string>(unavailable);
        while (pending.TryDequeue(out var key))
            if (dependents.TryGetValue(key, out var children))
                foreach (var child in children)
                    if (unavailable.Add(child)) pending.Enqueue(child);
        foreach (var id in order)
        {
            var candidates = Candidates(before, id, version);
            var name = candidates.FirstOrDefault()?.Name ?? id;
            var package = candidates.FirstOrDefault()?.PackageId ?? id;
            if (unavailable.Contains(id) || unavailable.Contains(package))
            {
                if (GameContentPolicy.KeepWhenClearing(id, version) || candidates.Any(c => c.Source == ModSource.Game))
                    throw new InvalidDataException("游戏内容或其必要前置不可用，不能自动剔除：" + name);
                removed.Add(id);
                messages.Add(candidates.Count == 0 ? "未安装，已从排序剔除：" + name
                    : unusablePackages.Contains(package) ? "所有副本均未能使用，已从排序剔除：" + name
                    : "必要前置不可用，已连带移出排序：" + name);
                continue;
            }
            var copies = Candidates(after, id, version);
            var selected = copies.Count == 1 ? copies[0] : throw new InvalidDataException("副本尚未实际隔离：" + name);
            var next = after.IdentifierFor(selected, version);
            mapping[id] = next;
            if (seen.Add(next)) result.Add(next);
            else messages.Add("重复启用同一模组，已合并排序项：" + name);
        }
        return new(result.ToArray(), removed.ToArray(), messages) { IdentifierMap = mapping };
    }
}
