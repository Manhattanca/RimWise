using RimWise.Core;

namespace RimWise.Integration.AI;

/// <summary>One locally attributed read failure at a time; warnings alone grant no authority.</summary>
public static class MetadataReadFailureTrials
{
    public const string ReadFailureRemovalReason = "排序软件读取失败，且本轮游戏报错与该模组有关；为防止其他情况，已从排序剔除（安装文件保留；不代表已证明根因）";

    public const string UnknownDependenciesNotice = "剩余普通模组中仍有读取失败项，其依赖关系未知；本次仅为定向排除试验，尚未证明根因。";

    public static bool IsAffected(ModCatalogue catalogue, string id, GameVersion? version)
        => catalogue.IsUnverifiedOrderIdentifier(id) && catalogue.Resolve(id, version).Candidates.Count == 0
            || catalogue.Resolve(id, version).UniqueInstallation?.Diagnostics.Any(d => d.Code == "MetadataUnreadable") == true;

    internal static bool HasUnknownDependencies(ModCatalogue catalogue, IReadOnlyList<string> order, GameVersion? version)
        => order.Any(id => IsAffected(catalogue, id, version)
            || catalogue.Resolve(id, version).UniqueInstallation?.Metadata is null);

    internal static bool SafeRemoval(ModCatalogue catalogue, IReadOnlyList<string> order,
        IReadOnlyList<string> retained, GameVersion version, string selected, IReadOnlyList<string> removed)
    {
        // Unknown dependencies only block when they can compromise protected content.
        // Ordinary unreadable survivors remain experimental, with an explicit notice.
        var protectedIds = AiOrderPlanner.DependencyClosure(retained.Concat(order.Where(id =>
            GameContentPolicy.KeepWhenClearing(id, version)
                || catalogue.Resolve(id, version).UniqueInstallation?.Source == ModSource.Game)), catalogue, version);
        foreach (var id in protectedIds)
        {
            var mod = catalogue.Resolve(id, version).UniqueInstallation
                ?? (catalogue.FindPackage(id) is { Count: 1 } matches ? matches[0] : null);
            if (mod?.Metadata is null || catalogue.IsUnverifiedOrderIdentifier(id)
                || mod.Diagnostics.Any(d => d.Code == "MetadataUnreadable")) return false;
        }
        return ExactDependents(catalogue, order, version, selected).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(removed);
    }

    private static string[] ExactDependents(ModCatalogue catalogue, IReadOnlyList<string> order,
        GameVersion version, string selected)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in order)
            foreach (var dependency in catalogue.Resolve(id, version).UniqueInstallation?.Metadata?.Requirements.Dependencies ?? [])
            {
                if (!dependents.TryGetValue(dependency.PackageId, out var ids)) dependents[dependency.PackageId] = ids = [];
                ids.Add(id);
            }
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selected };
        var pending = new Queue<string>([selected]);
        while (pending.TryDequeue(out var id))
        {
            var mod = catalogue.Resolve(id, version).UniqueInstallation;
            // Opaque tokens are identities only within this order, never inferred package aliases.
            var dependencyId = !catalogue.IsUnverifiedOrderIdentifier(id) && mod?.PackageId is { Length: > 0 } package
                ? package : id;
            if (dependents.TryGetValue(dependencyId, out var ids))
                foreach (var dependent in ids)
                    if (closure.Add(dependent)) pending.Enqueue(dependent);
        }
        return order.Where(closure.Contains).ToArray();
    }

    internal static ConflictRemovalOption Option(ModCatalogue catalogue, IReadOnlyList<string> order,
        IReadOnlyList<string> retained, GameVersion version, string selected)
    {
        var removed = ExactDependents(catalogue, order, version, selected);
        // Reuse all official/retention/alias protection checks for every exact dependent.
        var checks = DeclaredConflictResolution.Options(catalogue, order, retained, version, removed);
        var selectedOption = checks.Single(o => o.Identifier.Equals(selected, StringComparison.OrdinalIgnoreCase));
        return selectedOption with { RemovedIds = Array.AsReadOnly(removed),
            BlockedReason = checks.FirstOrDefault(o => o.BlockedReason is not null)?.BlockedReason };
    }

    internal static IReadOnlyList<TrialExclusion> Candidates(MaintenanceGameReport report,
        ModCatalogue catalogue, IReadOnlyList<string> order, IReadOnlyList<string> retained,
        GameVersion version, int attempt)
    {
        if (!report.CanDiagnose || report.Errors.Count == 0
            || report.StartupFailure?.Context.EnvironmentFailure == true
            || report.StartupFailure?.Context.Evidence.Any(e => e.Code == "TestOrderMismatch") == true)
            return [];
        var facts = report.DiagnosticFacts.Where(f => RuntimeConflictEvidence.IsDiagnosticCode(f.Code)).ToArray();
        var blocker = MaintenanceFailureSignature.Blocker(report) is not null;
        bool Matches(string id, string factId) => id.Equals(factId, StringComparison.OrdinalIgnoreCase)
            || !catalogue.IsUnverifiedOrderIdentifier(id)
                && catalogue.Resolve(id, version).UniqueInstallation is { PackageId.Length: > 0 } mod
                && mod.PackageId.Equals(factId, StringComparison.OrdinalIgnoreCase);
        var subjects = order.Where(id => IsAffected(catalogue, id, version) && facts.Any(f =>
            (!blocker || f.Code == "GameTestBlockingFailure") && Matches(id, f.PackageId))).ToArray();
        return subjects.Select(id => Option(catalogue, order, retained, version, id))
            .Where(o => o.BlockedReason is null && SafeRemoval(catalogue, order, retained, version, o.Identifier, o.RemovedIds))
            .Select(o => new TrialExclusion(o.Identifier, o.RemovedIds.ToArray(), order.ToArray(),
                facts.Where(f => Matches(o.Identifier, f.PackageId)).Select(f => f.Code).Distinct().ToArray(), attempt)
            {
                ReadFailureRemoval = true,
                FailureFingerprint = MaintenanceFailureSignature.Create(report),
                ErrorSignatures = report.ErrorSignatures.ToArray()
            }).ToArray();
    }
}
