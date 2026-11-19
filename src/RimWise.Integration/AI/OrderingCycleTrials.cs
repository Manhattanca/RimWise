using RimWise.Core;

namespace RimWise.Integration.AI;

/// <summary>Local, evidence-led experiments. Seen orders are journalled by the controller,
/// so resuming or changing unrelated rows cannot repeat a previously tested group order.</summary>
internal static class OrderingCycleTrials
{
    internal static bool Changed(string[] group, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var members = group.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !before.Where(members.Contains).SequenceEqual(after.Where(members.Contains), StringComparer.OrdinalIgnoreCase);
    }
    internal static string[] Associated(MaintenanceGameReport report, ModCatalogue catalogue,
        IReadOnlyList<string> order, GameVersion version, bool includeVerifiedBlocker = false)
    {
        if (!report.CanDiagnose || report.Errors.Count == 0
            || report.StartupFailure?.Context.EnvironmentFailure == true) return [];
        var hasBlocker = MaintenanceFailureSignature.Blocker(report) is not null;
        if (hasBlocker && !includeVerifiedBlocker) return [];
        var packages = hasBlocker ? report.DiagnosticFacts.Where(f => f.Code == "GameTestBlockingFailure")
            .Select(f => f.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : report.DiagnosticFacts.Where(f => RuntimeConflictEvidence.IsDiagnosticCode(f.Code))
            .Select(f => f.PackageId).Concat(report.LogContext?.Files.Where(f => f.OrderIndex > 0)
                .Select(f => f.PackageId) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return order.Where(id => packages.Contains(id)
            || packages.Contains(catalogue.Resolve(id, version).UniqueInstallation?.PackageId ?? id)).ToArray();
    }

    internal static IReadOnlyList<string[]> Related(IReadOnlyList<string[]> groups,
        IReadOnlyList<string> order, IReadOnlyList<string> associated)
    {
        var active = order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owners = associated.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groups.Where(group => group.Any(id => active.Contains(id) && owners.Contains(id)))
            .Select(group => group.Where(active.Contains).ToArray()).ToArray();
    }

    internal static IEnumerable<string[]> Swaps(IReadOnlyList<string[]> groups, IReadOnlyList<string> current,
        IReadOnlyList<string> associated, IReadOnlyList<string[]> seen, CancellationToken token)
    {
        var owners = associated.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.Where(group => group.Length > 1))
        {
            var members = group.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Never permute recursively: each experiment swaps just one associated pair
            // from the first tested arrangement of this exact surviving group.
            var arrangements = seen.Select(order => order.Where(members.Contains).ToArray())
                .Where(order => order.Length == group.Length).ToArray();
            var baseline = arrangements.FirstOrDefault() ?? current.Where(members.Contains).ToArray();
            for (var first = 0; first < baseline.Length; first++)
                for (var second = first + 1; second < baseline.Length; second++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!owners.Contains(baseline[first]) && !owners.Contains(baseline[second])) continue;
                    var swapped = baseline.ToArray();
                    (swapped[first], swapped[second]) = (swapped[second], swapped[first]);
                    if (arrangements.Any(order => order.SequenceEqual(swapped, StringComparer.OrdinalIgnoreCase))) continue;
                    var remaining = new Queue<string>(swapped);
                    yield return current.Select(id => members.Contains(id) ? remaining.Dequeue() : id).ToArray();
                }
        }
    }
}
