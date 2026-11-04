using RimWise.Core;

namespace RimWise.Integration.AI;

/// <summary>Only a missing installation can justify this choice; ambiguous installations never qualify.</summary>
public static class MissingModResolution
{
    public static DeclaredConflict? Find(ModCatalogue catalogue, IReadOnlyList<string> expandedOrder,
        IReadOnlyList<string> retained, GameVersion version, IReadOnlyDictionary<string, string>? failures = null)
    {
        var missing = new List<string>();
        foreach (var id in expandedOrder)
        {
            var match = catalogue.Resolve(id, version);
            if (match.Candidates.Count == 0) missing.Add(id);
            else if (match.UniqueInstallation is null) throw new InvalidDataException(DuplicateInstallations.Describe(match));
        }
        if (missing.Count == 0) return null;
        var reason = "以下模组仍未安装，无法继续游戏测试。确认后仅从排序移出，并连带移出依赖它们的模组。\n"
            + string.Join("\n", missing.Select(id => id + "：" + (failures?.GetValueOrDefault(id) ?? "尚未完成 Steam 下载")));
        return new(missing[0], missing[^1], reason,
            DeclaredConflictResolution.Options(catalogue, expandedOrder, retained, version, missing)) { MissingIds = missing.ToArray() };
    }
}
