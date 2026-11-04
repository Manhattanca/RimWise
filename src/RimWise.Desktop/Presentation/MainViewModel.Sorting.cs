using RimWise.Core;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    public RelayCommand AutoSortCommand { get; private set; } = null!;

    private void InitializeSorting()
    {
        AutoSortCommand = new RelayCommand(AutoSort, () => HasDocument && scan is not null && Rows.Count > 0 && !IsScanning && !IsShowingCache);
    }

    private void AutoSort()
    {
        if (!AutoSortCommand.CanExecute(null) || scan is null || document is null) return;
        if (ScanIssues.Any(issue => issue.Diagnostic.Code is "ScanLimit" or "ScanRootUnreadable" or "NoScanPaths"))
        {
            Status = "模组目录未完整读取，请检查读取记录并刷新后再自动排序。";
            return;
        }
        var before = CaptureEditor();
        var current = Rows.Select(row => row.PackageId).ToArray();
        var result = new LoadOrderSorter().Sort(scan.Catalogue, current,
            EffectiveGameVersion, scan.TargetVersion, CustomRules);
        if (!result.Complete)
        {
            RefreshOrderDiagnostics(result.Diagnostics.Where(issue => issue.Code == "OrderingCycle").ToArray());
            Status = result.Explanation;
            return;
        }
        if (!result.Changed)
        {
            Status = result.Explanation;
            DescribeAction("自动排序检查：顺序无需调整");
            return;
        }
        var selectedId = SelectedRow?.PackageId;
        Rows.ReplaceWith(result.Order.Select((id, index) => new LoadOrderRow(id, index + 1)));
        RefreshCatalogueMatches();
        SelectedRow = selectedId is null ? Rows.FirstOrDefault() : Rows.FirstOrDefault(row => row.PackageId.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        OrderEdited();
        var moved = result.Order.Where((id, index) => !id.Equals(current[index], StringComparison.Ordinal)).Count();
        RecordEdit(before, $"自动排序：{moved} 项位置发生变化", "撤销自动排序，恢复原顺序");
    }

    private void RefreshSortingCommand() => AutoSortCommand?.Refresh();
}
