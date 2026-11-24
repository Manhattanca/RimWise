using RimWise.Core;

namespace RimWise.Integration.AI;

/// <summary>Loading failures are hypotheses for a private differential test, not removal approvals.
/// Version declarations, model prose, warnings and incidental shared-library stacks are not evidence.</summary>
internal static class SuspectedModFailureTrials
{
    internal static bool IsFailureCode(string code) => code is "GameTypeLoadFailure" or "GameMissingXmlParent" or "GameModContentFailure";

    internal static IReadOnlyList<TrialExclusion> Candidates(MaintenanceGameReport report,
        ModCatalogue catalogue, IReadOnlyList<string> order, IReadOnlyList<string> retained,
        GameVersion version, int attempt)
    {
        if (!report.CanDiagnose || report.Errors.Count == 0
            || report.StartupFailure?.Context is { EnvironmentFailure: true } or { CanAdjustOrder: false }
            || report.StartupFailure?.Context.Evidence.Any(e => e.Code == "TestOrderMismatch") == true)
            return [];
        var failures = report.DiagnosticFacts.Where(f => IsFailureCode(f.Code))
            .Select(f => f.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suspects = order.Where(id => catalogue.Resolve(id, version).UniqueInstallation is { } mod
            && (failures.Contains(id) || failures.Contains(mod.PackageId))).ToArray();
        if (suspects.Length == 0) return [];
        // Reuse dependency closure, official content and retained-mod protections. The generic
        // planner prioritizes a journey blocker; intersect again so it cannot broaden this scope.
        return TrialExclusionPlanning.Candidates(report, catalogue, order, retained, version, attempt, suspects)
            .Where(t => suspects.Contains(t.SelectedId, StringComparer.OrdinalIgnoreCase))
            .Select(t => t with { SuspectedFailureVerification = true }).ToArray();
    }
}
