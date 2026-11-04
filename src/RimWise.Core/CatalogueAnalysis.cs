namespace RimWise.Core;

/// <summary>Read-only diagnostics. These observations never change enabled mods or their order.</summary>
public static class CatalogueAnalysis
{
    public static IReadOnlyList<CatalogueDiagnostic> Inspect(ModCatalogue catalogue, GameVersion? target)
    {
        var issues = new List<CatalogueDiagnostic>();
        foreach (var mod in catalogue.Installations)
        {
            InspectInstallation(mod, catalogue, target, issues);
        }
        foreach (var group in catalogue.Installations.Where(mod => mod.PackageId.Length > 0)
                     .GroupBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            if (target?.UsesLegacyIdentifiers != true && group.Count() == 2
                && group.Count(mod => mod.Source == ModSource.Workshop) == 1) continue;
            issues.Add(new("DuplicateInstallation", DiagnosticSeverity.Warning,
                $"标识 {group.Key} 有 {group.Count()} 份安装，请检查来源。", string.Join("\n", group.Select(mod => mod.Directory)), group.Key));
        }
        return issues.AsReadOnly();
    }

    /// <summary>Checks only the current order, while resolving prerequisites against the full catalogue.</summary>
    public static IReadOnlyList<CatalogueDiagnostic> InspectSelectedOrder(ModCatalogue catalogue,
        IReadOnlyList<string> order, GameVersion? configurationVersion, GameVersion? target)
    {
        var issues = new List<CatalogueDiagnostic>();
        foreach (var id in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (catalogue.Resolve(id, configurationVersion).UniqueInstallation is not { } mod) continue;
            var installationIssues = new List<CatalogueDiagnostic>();
            InspectInstallation(mod, catalogue, target, installationIssues);
            // Keep the raw order identifier, including legacy folders and _steam aliases.
            issues.AddRange(installationIssues.Select(issue => issue with
            {
                Identifier = id,
                Severity = issue.Code == "MetadataUnreadable" ? DiagnosticSeverity.Error : issue.Severity
            }));
        }
        issues.AddRange(InspectOrder(catalogue, order, configurationVersion));
        return issues.AsReadOnly();
    }

    private static void InspectInstallation(ModInstallation mod, ModCatalogue catalogue, GameVersion? target,
        List<CatalogueDiagnostic> issues)
    {
        issues.AddRange(mod.Diagnostics);
        if (mod.Metadata is not { } metadata) return;
        var declared = metadata.SupportedVersions.Count > 0 ? metadata.SupportedVersions
            : string.IsNullOrWhiteSpace(metadata.LegacyTargetVersion) ? [] : new[] { metadata.LegacyTargetVersion };
        if (target is not null && declared.Count > 0
            && !declared.Any(version => GameVersion.Parse(version)?.Series == target.Series))
            issues.Add(new("VersionNotDeclared", DiagnosticSeverity.Warning,
                $"{mod.Name} 未声明支持 {target.Series}；不等同于已确认不兼容。", mod.Directory, mod.PackageId));
        if (target is null && metadata.HasVersionedRequirements)
            issues.Add(new("TargetVersionUnknown", DiagnosticSeverity.Warning,
                $"{mod.Name} 存在按版本定义的规则，尚未确定目标游戏版本。", mod.Directory, mod.PackageId));
        foreach (var dependency in metadata.Requirements.Dependencies)
            if (target?.UsesLegacyIdentifiers != true && catalogue.FindPackage(dependency.PackageId).Count == 0)
                issues.Add(new("DependencyNotInstalled", DiagnosticSeverity.Error,
                    $"扫描范围内未找到 {mod.Name} 的前置：{dependency.DisplayName}（{dependency.PackageId}）", mod.Directory, mod.PackageId));
    }

    public static IReadOnlyList<CatalogueDiagnostic> InspectOrder(ModCatalogue catalogue,
        IReadOnlyList<string> order, GameVersion? configurationVersion)
    {
        var issues = new List<CatalogueDiagnostic>();
        var matches = order.Select(id => catalogue.Resolve(id, configurationVersion)).ToArray();
        var activePackages = new HashSet<string>(matches.Where(match => match.UniqueInstallation is not null)
            .Select(match => match.UniqueInstallation!.PackageId), StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            if (match.Candidates.Count == 0)
            {
                issues.Add(new("OrderModMissing", DiagnosticSeverity.Error, $"扫描范围内未找到排序中的模组：{match.Identifier}", Identifier: match.Identifier));
                continue;
            }
            if (match.Candidates.Count > 1)
            {
                issues.Add(new("OrderModAmbiguous", DiagnosticSeverity.Warning,
                    DuplicateInstallations.Describe(match), Identifier: match.Identifier));
                continue;
            }
            var mod = match.UniqueInstallation!;
            if (mod.Metadata is not { } metadata) continue;
            if (configurationVersion?.UsesLegacyIdentifiers == true) continue;
            foreach (var dependency in metadata.Requirements.Dependencies)
                if (!activePackages.Contains(dependency.PackageId) && catalogue.FindPackage(dependency.PackageId).Count > 0)
                    issues.Add(new("DependencyNotResolvedInOrder", DiagnosticSeverity.Error,
                        $"{mod.Name} 的前置未启用或来源未确定：{dependency.DisplayName}", mod.Directory, match.Identifier));
            foreach (var incompatible in metadata.Requirements.IncompatibleWith.Where(activePackages.Contains))
            {
                var message = $"{mod.Name} 声明与 {incompatible} 不兼容，当前排序同时包含两者。";
                issues.Add(new("DeclaredConflict", DiagnosticSeverity.Error, message, mod.Directory, match.Identifier));
                foreach (var other in matches.Where(candidate => candidate.UniqueInstallation?.PackageId.Equals(incompatible, StringComparison.OrdinalIgnoreCase) == true))
                    issues.Add(new("DeclaredConflict", DiagnosticSeverity.Error, message, Identifier: other.Identifier));
            }
        }
        foreach (var group in order.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            issues.Add(new("DuplicateOrderEntry", DiagnosticSeverity.Error,
                $"排序中重复出现 {group.Key}，已保留原条目。", Identifier: group.Key));
        foreach (var group in matches.Where(match => match.UniqueInstallation is not null)
                     .GroupBy(match => match.UniqueInstallation!.Directory, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(match => match.Identifier).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            foreach (var match in group)
                issues.Add(new("DuplicateActiveInstallation", DiagnosticSeverity.Error,
                    $"同一模组通过不同标识重复启用：{string.Join("、", group.Select(item => item.Identifier))}", Identifier: match.Identifier));
        InspectPositions(matches, configurationVersion, issues);
        var indices = order.Select((id, index) => (id, index)).GroupBy(e => e.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        foreach (var (before, after) in GameContentOrder.Pairs(catalogue, order, configurationVersion))
            if (indices[before] >= indices[after])
                issues.Add(new("GameContentLoadsAfter", DiagnosticSeverity.Error,
                    $"{after} 必须在游戏内容 {before} 之后加载。", Identifier: after));
        return issues.AsReadOnly();
    }

    private static void InspectPositions(OrderMatch[] matches, GameVersion? version, List<CatalogueDiagnostic> issues)
    {
        if (version?.UsesLegacyIdentifiers == true) return;
        var positions = matches.Select((match, index) => (Mod: match.UniqueInstallation, Index: index))
            .Where(item => item.Mod?.PackageId.Length > 0)
            .GroupBy(item => item.Mod!.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < matches.Length; index++)
        {
            var match = matches[index];
            if (match.UniqueInstallation is not { Metadata: { } metadata } mod) continue;
            if (metadata.Requirements.Dependencies.Any(d => d.PackageId.Equals(mod.PackageId, StringComparison.OrdinalIgnoreCase))
                || metadata.Requirements.LoadBefore.Concat(metadata.Requirements.LoadAfter)
                    .Any(id => id.Equals(mod.PackageId, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new("SelfRequirementIgnored", DiagnosticSeverity.Warning,
                    $"{mod.Name} 的元数据声明了指向自身的前置或先后关系；已忽略这条无效关系，其他要求仍然检查。",
                    mod.Directory, match.Identifier));
            foreach (var dependency in metadata.Requirements.Dependencies)
                if (positions.TryGetValue(dependency.PackageId, out var prerequisite) && prerequisite != index && prerequisite >= index)
                    issues.Add(new("DependencyLoadsAfter", DiagnosticSeverity.Error,
                        $"{mod.Name} 的前置 {dependency.DisplayName} 必须在它之前加载。", mod.Directory, match.Identifier));
            foreach (var before in metadata.Requirements.LoadBefore)
                if (positions.TryGetValue(before, out var other) && other != index && index >= other)
                    issues.Add(new("LoadBeforeViolated", DiagnosticSeverity.Warning,
                        $"{mod.Name} 声明应在 {before} 之前加载。", mod.Directory, match.Identifier));
            foreach (var after in metadata.Requirements.LoadAfter)
                if (positions.TryGetValue(after, out var other) && other != index && index <= other)
                    issues.Add(new("LoadAfterViolated", DiagnosticSeverity.Warning,
                        $"{mod.Name} 声明应在 {after} 之后加载。", mod.Directory, match.Identifier));
        }
    }
}
