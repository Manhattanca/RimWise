using RimWise.Core;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    public SnapshotCollection<DiagnosticRow> Diagnostics { get; } = [];
    public bool HasOrderStatus => scan is not null && document is not null;
    public int ErrorCount => Diagnostics.Count(issue => issue.Diagnostic.Severity == DiagnosticSeverity.Error);
    public int WarningCount => Diagnostics.Count(issue => issue.Diagnostic.Severity == DiagnosticSeverity.Warning);
    public string ErrorCountLabel => $"{ErrorCount} 个错误";
    public string WarningCountLabel => $"{WarningCount} 个警告";
    public string OrderStatusTooltip => !HasOrderStatus ? "等待载入排序和扫描模组资料"
        : IsScanning ? "正在刷新，显示上次刷新资料对应的排序状态"
        : "按当前启用清单统计问题条数；同一问题涉及多个模组只计一次。悬停行末图标查看原因。尚未经过游戏运行测试。"
            + (ScanIssues.Count > 0 ? "\n部分安装资料未完整读取，请查看读取记录。" : "");

    private void RefreshOrderDiagnostics(IReadOnlyList<CatalogueDiagnostic>? sortingIssues = null)
    {
        var issues = new List<CatalogueDiagnostic>();
        if (scan is not null && document is not null)
        {
            var version = EffectiveGameVersion;
            if (CommunityDatabase is not null && version is not null && !version.UsesLegacyIdentifiers)
                try { issues.AddRange(CommunityDatabase.Inspect(scan.Catalogue,Rows.Select(r => r.PackageId).ToArray(),version,CustomRules)); }
                catch (System.IO.InvalidDataException ex) { issues.Add(new("CommunityDatabaseLimit", DiagnosticSeverity.Warning, ex.Message)); }
            issues.AddRange(CatalogueAnalysis.InspectSelectedOrder(scan.Catalogue,
                Rows.Select(row => row.PackageId).ToArray(), version, scan.TargetVersion));
            var positions = Rows.SelectMany(row => new[] { (Id: row.PackageId, row.Index),
                (Id: row.Match?.UniqueInstallation?.PackageId ?? row.PackageId, row.Index) })
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
            foreach (var rule in CustomRules.Where(rule => rule.Enabled))
                if (positions.TryGetValue(rule.Before, out var before) && positions.TryGetValue(rule.After, out var after) && before >= after)
                    foreach (var row in Rows.Where(row => row.Index == before || row.Index == after))
                        issues.Add(new("CustomRuleViolated", DiagnosticSeverity.Warning,
                            $"自定义规则：{rule.Before} 应在 {rule.After} 之前", Identifier: row.PackageId));
            var savedVersion = GameVersion.Parse(document.GameVersion);
            if (Rows.Count > 0 && scan.TargetVersion is not null && savedVersion?.Series != scan.TargetVersion.Series)
                issues.Add(new("ConfigurationVersionMismatch",
                    savedVersion?.UsesLegacyIdentifiers == true || scan.TargetVersion.UsesLegacyIdentifiers ? DiagnosticSeverity.Warning : DiagnosticSeverity.Information,
                    $"排序文件版本 {savedVersion?.Series ?? "未注明"}，目标游戏 {scan.TargetVersion.Series}；按目标游戏检查和排序，保存时备份并更新兼容的版本标记；旧版文件夹标识仍需单独转换。"));
        }
        if (sortingIssues is not null) issues.AddRange(sortingIssues);
        var names = Rows.GroupBy(row => row.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        var summarized = DiagnosticSummary.Consolidate(issues);
        Diagnostics.ReplaceWith(summarized.Select(issue => new DiagnosticRow(issue, names.GetValueOrDefault(issue.Identifier, ""))));
        var byIdentifier = summarized.SelectMany(issue => issue.AffectedIdentifiers.Select(id => (Id: id, Issue: issue)))
            .ToLookup(item => item.Id, item => item.Issue, StringComparer.OrdinalIgnoreCase);
        var global = summarized.Where(issue => !issue.AffectedIdentifiers.Any()).ToArray();
        foreach (var row in Rows) row.SetDiagnostics(byIdentifier[row.PackageId].Concat(global).ToArray(), HasOrderStatus);
        foreach (var property in new[] { nameof(HasOrderStatus), nameof(ErrorCount), nameof(WarningCount),
                     nameof(ErrorCountLabel), nameof(WarningCountLabel), nameof(OrderStatusTooltip) }) OnPropertyChanged(property);
    }
}
