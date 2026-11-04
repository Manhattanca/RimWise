using RimWise.Core;

namespace RimWise.Integration.AI;

public sealed record AiConflictSuggestion(IReadOnlyList<string> PackageIds);

/// <summary>Local observations plus model suspects, never proof of causation or pairwise incompatibility.</summary>
public sealed record RuntimeConflictEvidence(string[] Members, string[] TestedOrder, string[] DiagnosticCodes)
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    { "GameLogError", "GameMissingXmlParent", "GameTypeLoadFailure", "GamePatchFailure", "GameInitializationFailure", "GameHarmonyFailure", "GameTestBlockingFailure" };

    internal static bool IsDiagnosticCode(string code) => Codes.Contains(code);

    internal void Validate()
    {
        static bool Invalid(string[]? ids, int min) => ids is null || ids.Length < min || ids.Length > 20000
            || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > AiModIdentifier.MaximumLength || id.Any(char.IsControl))
            || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length;
        if (Invalid(Members, 2) || Invalid(TestedOrder, 2)
            || !Members.All(id => TestedOrder.Contains(id, StringComparer.OrdinalIgnoreCase))
            || DiagnosticCodes is null || DiagnosticCodes.Length is < 1 or > 7
            || DiagnosticCodes.Any(code => code is null || !Codes.Contains(code)))
            throw new InvalidDataException("运行时组合冲突证据无效。");
    }

    internal RuntimeConflictEvidence Copy() => new(Members.ToArray(), TestedOrder.ToArray(), DiagnosticCodes.ToArray());
}

public static class RuntimeConflictResolution
{
    internal static DeclaredConflict? FromReport(AiConflictSuggestion suggestion, MaintenanceGameReport report,
        ModCatalogue catalogue, IReadOnlyList<string> order, IReadOnlyList<string> retained, GameVersion version)
    {
        if (!report.CanDiagnose || report.Errors.Count == 0 || suggestion.PackageIds is null
            || suggestion.PackageIds.Count < 2 || suggestion.PackageIds.Count > order.Count) return null;
        // Every suspect must map to an active installation AND a local diagnostic observation.
        // A model cannot implicate arbitrary installed/inactive mods or turn loadAfter into a prerequisite.
        string? ActiveId(string id) => order.FirstOrDefault(active => active.Equals(id, StringComparison.OrdinalIgnoreCase)
            || catalogue.Resolve(active, version).UniqueInstallation?.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase) == true);
        var members = suggestion.PackageIds.Select(ActiveId).ToArray();
        if (members.Any(id => id is null) || members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length) return null;
        var facts = report.DiagnosticFacts.Where(f => RuntimeConflictEvidence.IsDiagnosticCode(f.Code)).ToArray();
        if (members.Any(id => !facts.Any(fact => ActiveId(fact.PackageId) == id))) return null;
        var codes = facts.Select(f => f.Code)
            .Distinct().ToArray();
        var evidence = new RuntimeConflictEvidence(members.Select(id => id!).ToArray(), order.ToArray(), codes);
        return Restore(evidence, catalogue, order, retained, version);
    }

    internal static DeclaredConflict Restore(RuntimeConflictEvidence evidence, ModCatalogue catalogue,
        IReadOnlyList<string> order, IReadOnlyList<string> retained, GameVersion version)
    {
        evidence.Validate();
        if (!evidence.TestedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(order))
            throw new InvalidDataException("运行时冲突的启用组合已变化，需要重新测试。");
        return new(evidence.Members[0], evidence.Members[1],
            "补齐必要前置并排序后仍检测到报错。以下为 AI 建议排查的组合，尚未证实因果关系；移出后需要复测。",
            DeclaredConflictResolution.Options(catalogue, order, retained, version, evidence.Members))
        { RuntimeEvidence = evidence.Copy() };
    }
}
