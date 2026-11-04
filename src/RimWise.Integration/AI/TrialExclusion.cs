using RimWise.Core;

namespace RimWise.Integration.AI;

/// <summary>An experimental exclusion, not a proven culprit or a player's removal approval.</summary>
public sealed record TrialExclusion(string SelectedId, string[] RemovedIds, string[] TestedOrder,
    string[] DiagnosticCodes, int Attempt)
{
    internal void Validate()
    {
        static bool Invalid(string[]? ids) => ids is null || ids.Length is < 1 or > 20000
            || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > AiModIdentifier.MaximumLength || id.Any(char.IsControl))
            || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length;
        if (string.IsNullOrWhiteSpace(SelectedId) || Invalid(RemovedIds) || Invalid(TestedOrder)
            || !RemovedIds.Contains(SelectedId, StringComparer.OrdinalIgnoreCase)
            || RemovedIds.Any(id => !TestedOrder.Contains(id, StringComparer.OrdinalIgnoreCase))
            || DiagnosticCodes is null || DiagnosticCodes.Length is < 1 or > 6
            || DiagnosticCodes.Any(code => !RuntimeConflictEvidence.IsDiagnosticCode(code)) || Attempt < 1)
            throw new InvalidDataException("临时试排除记录无效。");
    }

    internal TrialExclusion Copy() => this with { RemovedIds = RemovedIds.ToArray(),
        TestedOrder = TestedOrder.ToArray(), DiagnosticCodes = DiagnosticCodes.ToArray() };
}

/// <summary>Trusted local hypotheses and dependency/protection checks, independent of model confidence.</summary>
internal static class TrialExclusionPlanning
{
    internal static TrialExclusion? Select(MaintenanceGameReport report, ModCatalogue catalogue,
        IReadOnlyList<string> order, IReadOnlyList<string> retained, GameVersion version, int attempt)
        => Candidates(report, catalogue, order, retained, version, attempt).FirstOrDefault();

    internal static IReadOnlyList<TrialExclusion> Candidates(MaintenanceGameReport report, ModCatalogue catalogue,
        IReadOnlyList<string> order, IReadOnlyList<string> retained, GameVersion version, int attempt)
    {
        if (!report.CanDiagnose || report.Errors.Count == 0 || report.StartupFailure?.Context.EnvironmentFailure == true)
            return [];
        string? Active(string id) => order.FirstOrDefault(active => active.Equals(id, StringComparison.OrdinalIgnoreCase)
            || catalogue.Resolve(active, version).UniqueInstallation?.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase) == true);
        var facts = report.DiagnosticFacts.Where(f => RuntimeConflictEvidence.IsDiagnosticCode(f.Code)
            && Active(f.PackageId) is not null).ToArray();
        // Files are bounded, error-targeted local evidence. A reference is a hypothesis, never proof.
        var files = report.LogContext?.Files.Where(f => f.OrderIndex > 0 && Active(f.PackageId) is not null).ToArray() ?? [];
        var members = facts.Select(f => Active(f.PackageId)!).Concat(files.Select(f => Active(f.PackageId)!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var options = DeclaredConflictResolution.Options(catalogue, order, retained, version, members);
        // Interceptor stacks list innocent patches too. A verified type named by
        // the error itself is a more specific experimental candidate than those frames.
        var subjects = RimWise.Integration.Game.DiagnosticLogContext.SubjectSymbols(report.Errors);
        var subjectOwners = report.LogContext?.Owners.Where(owner => owner.Basis == "assembly-type"
            && subjects.Contains(owner.Symbol, StringComparer.Ordinal)).Select(owner => Active(owner.PackageId))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var candidates = options.Where(o => o.BlockedReason is null)
            .OrderBy(o => subjectOwners.Contains(o.Identifier) ? 0 : 1)
            .ThenBy(o => facts.Any(f => Active(f.PackageId) == o.Identifier && f.Code is "GamePatchFailure" or "GameTypeLoadFailure") ? 0
                : files.Any(f => Active(f.PackageId) == o.Identifier && f.Kind == "active-patch") ? 1
                : files.Any(f => Active(f.PackageId) == o.Identifier && f.Kind == "active-reference") ? 2
                : facts.Any(f => Active(f.PackageId) == o.Identifier) ? 3 : 4)
            .ThenBy(o => o.RemovedIds.Count);
        return candidates.Select(selected =>
        {
            var codes = facts.Where(f => Active(f.PackageId) == selected.Identifier).Select(f => f.Code).Distinct().ToArray();
            return new TrialExclusion(selected.Identifier, selected.RemovedIds.ToArray(), order.ToArray(),
                codes.Length == 0 ? ["GameLogError"] : codes, attempt);
        }).ToArray();
    }

    internal static string[] Replay(ModCatalogue catalogue, IReadOnlyList<string> original,
        IReadOnlyList<string> retained, GameVersion version, IReadOnlyList<TrialExclusion> trials)
    {
        var current = original.ToArray();
        foreach (var trial in trials)
        {
            trial.Validate();
            if (!current.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(trial.TestedOrder))
                throw new InvalidDataException("临时试排除的测试组合已变化。");
            var option = DeclaredConflictResolution.Options(catalogue, trial.TestedOrder, retained, version, [trial.SelectedId]).Single();
            if (option.BlockedReason is not null || !option.RemovedIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(trial.RemovedIds))
                throw new InvalidDataException("临时试排除的保护或连带依赖已变化。");
            current = current.Except(trial.RemovedIds, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return current;
    }

    internal static AiOrderPlan Plan(IReadOnlyList<string> before, IReadOnlyList<string> target,
        ModCatalogue catalogue, IReadOnlyList<string> retained, GameVersion version,
        IReadOnlyList<CustomOrderRule> rules, IReadOnlyList<TrialExclusion> trials,
        IReadOnlyList<ConflictRemovalApproval>? approvals = null, ModCatalogue? validationCatalogue = null)
    {
        var constraints = validationCatalogue ?? catalogue;
        var expanded = AiOrderPlanner.ExpandRequiredOrder(before, constraints, version, allowMissing: true);
        var approved = DeclaredConflictResolution.Replay(constraints, expanded, retained, version, approvals ?? []);
        var allowed = Replay(constraints, approved, retained, version, trials);
        AiOrderPlanner.ExpandRequiredOrder(allowed, constraints, version);
        var rejected = new List<string>();
        if (target.Count > 20000 || target.Distinct(StringComparer.OrdinalIgnoreCase).Count() != target.Count
            || !allowed.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(target))
            rejected.Add("试排除候选包含未授权增删。");
        if (rejected.Count == 0)
        {
            var verified = new LoadOrderSorter().Validate(constraints, target, version, version, rules, deferAdvisoryCycles: true);
            if (!verified.Complete || verified.Changed) rejected.Add(verified.FailureReason ?? "试排除候选违反排序规则。");
        }
        return new(before.ToArray(), rejected.Count == 0 ? target.ToArray() : before.ToArray(),
            AiOrderPlanner.Fingerprint(before, catalogue, retained, rules, version), rejected.ToArray(),
            trials.Select(t => "临时试排除（未证明根因）：" + t.SelectedId).ToArray());
    }
}

/// <summary>Only the private test adapter accepts these temporary authorities; the editor does not.</summary>
internal interface IMaintenanceTrialSession
{
    void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials);
}
