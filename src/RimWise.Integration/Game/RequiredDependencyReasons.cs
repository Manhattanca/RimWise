using RimWise.Core;

namespace RimWise.Integration.Game;

/// <summary>Display-only provenance from the active, version-selected metadata, never inferred from titles.</summary>
internal static class RequiredDependencyReasons
{
    internal static IReadOnlyDictionary<string, string> Build(ModCatalogue catalogue,
        IReadOnlyList<string> original, IReadOnlyList<string> expanded, GameVersion version)
    {
        var active = expanded.Select(id => catalogue.Resolve(id, version).UniqueInstallation)
            .OfType<ModInstallation>().DistinctBy(m => m.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
        var parents = active.SelectMany(mod => (mod.Metadata?.Requirements.Dependencies ?? [])
            .Select(dependency => (Mod: mod, Dependency: dependency)))
            .GroupBy(x => x.Dependency.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var originalDirectories = original.Select(id => catalogue.Resolve(id, version).UniqueInstallation?.Directory)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in expanded)
        {
            var installed = catalogue.Resolve(id, version).UniqueInstallation;
            var initiallySelected = original.Contains(id, StringComparer.OrdinalIgnoreCase)
                || installed is not null && originalDirectories.Contains(installed.Directory);
            if (initiallySelected && installed is not null) continue;
            var package = installed?.PackageId ?? (id.EndsWith("_steam", StringComparison.OrdinalIgnoreCase) ? id[..^6] : id);
            if (parents.TryGetValue(package, out var requiredBy))
            {
                var name = installed?.Name ?? requiredBy.Select(x => x.Dependency.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? id;
                // Keep the complete parent IDs even for shared dependencies; bound display names separately.
                var details = requiredBy.Select(x => $"{Label(x.Mod.Name)} [{x.Mod.PackageId}]").Distinct();
                var reason = $"{Label(name)} [{package}]：由 {string.Join("、", details)} 要求；依据：About.xml 必要前置（游戏 {version.Series}）。";
                result[id] = reason.Length <= 16000 ? reason : reason[..15940] + "…（关系过多，详见模组元数据）";
            }
            else if (initiallySelected)
                result[id] = $"{id}：原排序中的模组尚未安装，恢复所选模组。";
        }
        return result;
    }

    private static string Label(string text) => new(text.Take(160).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
