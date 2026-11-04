using RimWise.Core;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private bool CanAddToOrder() => HasDocument && selectedAvailableRows.Any(AvailableMods.Contains);

    private void AddSelectedToOrder()
        => AddAvailableRows(selectedAvailableRows.ToArray(), Rows.Count);

    internal string[] InvalidInsertionSelection(IEnumerable<InstalledModRow> selection) => selection
        .Where(row => !AvailableMods.Contains(row) || AvailableIdentifier(row) is null).Select(row => row.Name).ToArray();

    public bool AddAvailableRows(IEnumerable<InstalledModRow> selection, int insertionIndex)
    {
        var requested = selection.Distinct().ToArray();
        if (!HasDocument || insertionIndex < 0 || insertionIndex > Rows.Count || requested.Length == 0
            || requested.Any(row => !AvailableMods.Contains(row))) return false;
        var skipped = requested.Where(row => AvailableIdentifier(row) is null).ToArray();
        var identifiers = OrderAvailableRows(requested)
            .Select(AvailableIdentifier).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var skippedNotice = skipped.Length == 0 ? string.Empty
            : $"；{skipped.Length} 个模组未加入：{string.Join("、", skipped.Select(row => row.Name))}。缺少可用模组标识，请检查 About.xml";
        if (identifiers.Length == 0)
        {
            Status = skippedNotice.TrimStart('；');
            return false;
        }
        var before = CaptureEditor();
        var added = identifiers.Select((id, index) => new LoadOrderRow(id, insertionIndex + index + 1)).ToArray();
        var result = Rows.ToList();
        result.InsertRange(insertionIndex, added);
        Rows.ReplaceWith(result);
        RenumberRows();
        RefreshCatalogueMatches();
        SelectOrderRows(added);
        OrderEdited();
        RecordEdit(before, added.Length == 1 ? $"{added[0].Name} 加入排序，位置为 {added[0].Index}"
            : $"{added.Length} 个模组加入排序，位置为 {added[0].Index}–{added[^1].Index}",
            $"撤销加入：{added.Length} 个模组已移出排序");
        Status = $"已加入 {added.Length} 个模组，位置为 {added[0].Index}–{added[^1].Index}" + skippedNotice;
        return true;
    }

    private void RemoveSelectedFromOrder()
    {
        if (!CanRemoveSelected()) return;
        var selection = CurrentOrderSelection().OrderBy(Rows.IndexOf).ToArray();
        if (selection.Length == 0) return;
        var row = selection[0];
        var oldIndex = Rows.IndexOf(row);
        var before = CaptureEditor();
        var removed = selection.ToHashSet();
        Rows.ReplaceWith(Rows.Where(item => !removed.Contains(item)).ToArray());
        RenumberRows();
        SelectedRow = null;
        RefreshCatalogueMatches();
        if (Rows.Count > 0) SelectedRow = Rows[Math.Min(oldIndex, Rows.Count - 1)];
        OrderEdited();
        RecordEdit(before, selection.Length == 1 ? $"{row.Name} 已移出排序（原位置 {oldIndex + 1}）"
            : $"{selection.Length} 个模组已移出排序",
            selection.Length == 1 ? $"撤销移出：{row.Name} 恢复到位置 {oldIndex + 1}"
                : $"撤销移出：恢复 {selection.Length} 个模组");
    }

    private bool KeepWhenClearing(LoadOrderRow row) => GameContentPolicy.KeepWhenClearing(row.PackageId,
        EffectiveGameVersion) || row.IsRetained;

    private void ClearOrder()
    {
        if (!HasDocument) return;
        var retained = Rows.Where(KeepWhenClearing).ToArray();
        var removed = Rows.Count - retained.Length;
        if (removed == 0) return;
        var before = CaptureEditor();
        SearchText = string.Empty;
        Rows.ReplaceWith(retained);
        for (var i = 0; i < Rows.Count; i++) Rows[i].Index = i + 1;
        RefreshCatalogueMatches();
        SelectedRow = Rows.FirstOrDefault();
        OrderEdited();
        RecordEdit(before, $"已清空 {removed} 个模组，保留 {retained.Length} 项游戏内容",
            $"撤销清空：恢复 {removed} 个模组");
    }

    private void OrderEdited()
    {
        OrderRevision++;
        ContentRevision++;
        hasChanges = true;
        RefreshOrderDiagnostics();
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangeLabel));
        RefreshCounts();
        RefreshCommands();
        Status = "排序已调整，尚未保存";
    }
}
