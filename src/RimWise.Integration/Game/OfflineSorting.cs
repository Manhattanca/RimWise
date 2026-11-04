using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Databases;

namespace RimWise.Integration.Game;

public sealed record OfflineSortingResult(AiOrderPlan? Plan, IReadOnlyList<CustomOrderRule> Rules,
    IReadOnlyList<string> Messages, int AssemblyCount, int CacheHits, bool AnalysisLimited)
{
    public bool Complete => Plan is not null && Plan.Rejected.Count == 0;
}

/// <summary>One local preparation and topological sort. No transport, launcher, download,
/// removal or repair loop. Assembly metadata is inspected without executing mod code.</summary>
public static class OfflineSorting
{
    public static OfflineSortingResult Sort(ModCatalogue catalogue, IReadOnlyList<string> original,
        IReadOnlyList<string> retained, GameVersion version, IReadOnlyList<CustomOrderRule> userRules,
        DatabaseSnapshot? database, string cacheDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Allow absent identifiers into diagnostics so all missing prerequisites are reported.
        // The sorter blocks applying such an incomplete result; it never drops them.
        var expanded = AiOrderPlanner.ExpandRequiredOrder(original, catalogue, version, allowMissing: true);
        var rules = database?.Rules(catalogue, expanded, version, userRules) ?? userRules;
        var code = LocalCodeTopology.Prepare(catalogue, expanded, version, cacheDirectory, token);
        token.ThrowIfCancellationRequested();
        var sorted = new LoadOrderSorter().Sort(catalogue, expanded, version, version, rules,
            deferAdvisoryCycles: true, preferredOrder: code.PreferredOrder);
        token.ThrowIfCancellationRequested();
        // Describe the resulting order rather than warnings fixed by this sorting pass.
        var diagnostics = sorted.Complete
            ? CatalogueAnalysis.InspectSelectedOrder(catalogue, sorted.Order, version, version)
                .Concat(sorted.Diagnostics.Where(d => d.Code == "OrderingCycleDeferred"))
            : sorted.Diagnostics;
        var messages = diagnostics.Concat(database?.Inspect(catalogue, sorted.Order, version, userRules) ?? [])
            .Where(d => d.Severity != DiagnosticSeverity.Information).Select(d => d.Message).Distinct().ToList();
        if (!sorted.Complete) messages.Insert(0, sorted.FailureReason ?? "当前清单无法完成离线排序。");
        if (code.Report.Limited)
            messages.Add("部分程序集无法完整分析，已回退到元数据与规则排序；未将不完整的代码关系用于排序。");
        var plan = sorted.Complete
            ? AiOrderPlanner.PreparedOrder(original, sorted.Order, catalogue, retained, version, rules, []) : null;
        if (plan is not null) messages.AddRange(plan.Rejected);
        token.ThrowIfCancellationRequested();
        return new(plan, rules, messages.Distinct().ToArray(), code.Report.AssemblyCount,
            code.Report.CacheHits, code.Report.Limited);
    }
}
