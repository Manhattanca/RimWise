namespace RimWise.Core;

/// <summary>Read-only preflight for active installations and their declared prerequisites.</summary>
public static class DuplicateInstallations
{
    public static IReadOnlyList<OrderMatch> FindRequired(ModCatalogue catalogue, IReadOnlyList<string> order,
        GameVersion? version)
    {
        var matches = order.Select(id => catalogue.Resolve(id, version)).ToArray();
        var activePackages = matches.Where(match => match.UniqueInstallation is not null)
            .Select(match => match.UniqueInstallation!.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(order);
        var result = new List<OrderMatch>();
        while (queue.TryDequeue(out var id))
        {
            if (!visited.Add(id)) continue;
            var match = catalogue.Resolve(id, version);
            if (match.Candidates.Count > 1) { result.Add(match); continue; }
            if (version?.UsesLegacyIdentifiers == true || match.UniqueInstallation?.Metadata is not { } metadata) continue;
            foreach (var dependency in metadata.Requirements.Dependencies)
                // A deliberately selected _steam installation already fulfils its base dependency.
                if (!activePackages.Contains(dependency.PackageId)) queue.Enqueue(dependency.PackageId);
        }
        return result.AsReadOnly();
    }

    public static string Describe(OrderMatch match) =>
        $"模组标识 {match.Identifier} 匹配到 {match.Candidates.Count} 份安装，无法确定游戏使用哪一份：\n"
        + string.Join("\n", match.Candidates.Select(mod => $"{mod.Name}（{SourceName(mod.Source)}）\n{mod.Directory}"))
        + "\n请将多余副本移出模组目录或取消对应订阅，再刷新。仅从排序中移出不会解决重复安装。";

    public static string SourceName(ModSource source) => source switch
    {
        ModSource.Game => "游戏内容", ModSource.Local => "本地", ModSource.Workshop => "创意工坊", _ => "未知来源"
    };
}
