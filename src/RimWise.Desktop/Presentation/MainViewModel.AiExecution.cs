using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Databases;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    public ModCatalogue? CurrentCatalogue => scan?.Catalogue;
    public IReadOnlyList<CustomOrderRule> CustomRules { get; private set; } = [];
    public DatabaseSnapshot? CommunityDatabase { get; private set; }
    public void SetCommunityDatabase(DatabaseSnapshot database)
    {
        if (CommunityDatabase?.Id == database.Id) return;
        CommunityDatabase = database;
        ContentRevision++;
        RefreshOrderDiagnostics();
    }
    public void SetCustomRules(IReadOnlyList<CustomOrderRule> rules)
    {
        CustomRules = rules.Select(rule => rule.Normalize()).ToArray();
        ContentRevision++;
        RefreshOrderDiagnostics();
    }

    public void ValidateAiPlan(AiOrderPlan plan, long expectedRevision) => ValidateAiPlan(plan, expectedRevision, CustomRules);
    internal void ValidateAiPlan(AiOrderPlan plan, long expectedRevision, IReadOnlyList<CustomOrderRule> rules,
        IReadOnlyList<string>? effectiveRetained = null)
    {
        effectiveRetained ??= RetainedPackageIds;
        if (RetainedPackageIds.Except(effectiveRetained, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidOperationException("任务不能放宽玩家的保留规则。");
        if (!ColorRetentionAvailable || scan is null || document is null || IsScanning || IsShowingCache || !plan.CanApply
            || (GameVersion.Parse(document.GameVersion) is { } configurationVersion && EffectiveGameVersion is { } targetVersion
                && configurationVersion.Series != targetVersion.Series
                && (configurationVersion.UsesLegacyIdentifiers || targetVersion.UsesLegacyIdentifiers))
            || expectedRevision != ContentRevision
            || !plan.Before.SequenceEqual(Rows.Select(row => row.PackageId), StringComparer.Ordinal)
            || AiOrderPlanner.Fingerprint(Rows.Select(row => row.PackageId), scan.Catalogue, effectiveRetained,
                rules, EffectiveGameVersion) != plan.Fingerprint)
            throw new InvalidOperationException("排序、安装资料、规则或保留清单已变化，请重新开始 自动排序。");
    }

    public bool ApplyAiPlan(AiOrderPlan plan, long expectedRevision) => ApplyAiPlan(plan, expectedRevision, CustomRules);
    internal bool ApplyAiPlan(AiOrderPlan plan, long expectedRevision, IReadOnlyList<CustomOrderRule> rules,
        IReadOnlyList<string>? effectiveRetained = null)
    {
        ValidateAiPlan(plan, expectedRevision, rules, effectiveRetained);
        var before = CaptureEditor();
        var selected = SelectedRow?.PackageId;
        Rows.ReplaceWith(plan.After.Select((id, i) => new LoadOrderRow(id, i + 1)));
        RefreshCatalogueMatches();
        SelectedRow = Rows.FirstOrDefault(row => row.PackageId == selected) ?? Rows.FirstOrDefault();
        OrderEdited();
        RecordEdit(before, "应用 自动排序", "撤销 自动排序，恢复原排序");
        Status = plan.Notices.Count > 0 ? string.Join("；", plan.Notices) : "已应用 自动排序，可撤销";
        return true;
    }
}
