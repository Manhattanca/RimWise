using RimWise.Integration.AI;

namespace RimWise.Desktop.Presentation;

internal sealed record InsertionChoice(string PackageId, string Name, object Row);

public sealed partial class MainViewModel
{
    internal InsertionChoice[] PendingInsertionMods() => Rows.Where(row => !savedOrderIds.Contains(row.PackageId))
        .Select(row => new InsertionChoice(row.PackageId, row.Name, row)).ToArray();

    internal InsertionChoice[] AvailableInsertionMods() => AvailableMods
        .Select(row => new InsertionChoice(AvailableIdentifier(row) ?? "", row.Name, row)).ToArray();

    internal string ApplyInsertionSelection(IEnumerable<string> identifiers)
    {
        var selected = identifiers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var pending = PendingInsertionMods().Select(row => row.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = AvailableInsertionMods().Select(row => row.PackageId).Concat(pending).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Length == 0 || selected.Any(id => !AiModIdentifier.IsValid(id) || !available.Contains(id)))
            throw new InvalidOperationException("选中模组缺少有效标识或安装已变化，请重新选择。");
        if (Rows.Any(row => pending.Contains(row.PackageId) && row.IsRetained && !selected.Contains(row.PackageId, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("尚未保存的必须保留模组不能移出，请先修改保留标记。");
        var baseline = Rows.Where(row => !pending.Contains(row.PackageId)).ToArray();
        var next = baseline.Select(row => row.PackageId).Concat(selected).ToArray();
        if (!Rows.Select(row => row.PackageId).SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
        {
            var before = CaptureEditor();
            Rows.ReplaceWith(next.Select((id, index) => new LoadOrderRow(id, index + 1)));
            RefreshCatalogueMatches();
            OrderEdited();
            RecordEdit(before, $"选择 {selected.Length} 个新增模组进行测试", "撤销新增模组测试选择");
        }
        return InsertionScope.Capture(baseline.Select(row => row.PackageId));
    }
}
