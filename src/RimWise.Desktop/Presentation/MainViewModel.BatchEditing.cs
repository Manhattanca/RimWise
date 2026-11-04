using RimWise.Core;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private LoadOrderRow[] selectedOrderRows = [];
    private InstalledModRow[] selectedAvailableRows = [];
    public IReadOnlyList<LoadOrderRow> SelectedOrderRows => selectedOrderRows;
    public IReadOnlyList<InstalledModRow> SelectedAvailableRows => selectedAvailableRows;

    public void SelectOrderRows(IEnumerable<LoadOrderRow> rows, LoadOrderRow? primary = null)
    {
        var selection = rows.Where(Rows.Contains).Distinct().OrderBy(Rows.IndexOf).ToArray();
        SelectedRow = primary is not null && selection.Contains(primary) ? primary : selection.FirstOrDefault();
        selectedOrderRows = selection;
        OnPropertyChanged(nameof(SelectedOrderRows));
        OnPropertyChanged(nameof(SelectedMarkerCountLabel));
        RefreshCommands();
    }

    public void SelectAvailableRows(IEnumerable<InstalledModRow> rows, InstalledModRow? primary = null)
    {
        var selection = OrderAvailableRows(rows.Where(AvailableMods.Contains).Distinct()).ToArray();
        SelectedAvailable = primary is not null && selection.Contains(primary) ? primary : selection.FirstOrDefault();
        selectedAvailableRows = selection;
        OnPropertyChanged(nameof(SelectedAvailableRows));
        OnPropertyChanged(nameof(SelectedMarkerCountLabel));
        RefreshCommands();
    }

    private LoadOrderRow[] CurrentOrderSelection() => selectedOrderRows.Where(Rows.Contains).ToArray();

    private string? AvailableIdentifier(InstalledModRow row)
    {
        if (document is null) return null;
        var version = EffectiveGameVersion;
        var id = scan?.Catalogue.IdentifierFor(row.Installation, version)
            ?? (version?.UsesLegacyIdentifiers == true ? row.Installation.FolderName : row.Installation.PackageId);
        return id.Length > 0 ? id : null;
    }

    private void RenumberRows()
    {
        for (var i = 0; i < Rows.Count; i++) Rows[i].Index = i + 1;
    }

    // The insertion index belongs to the complete list before the selected rows are removed.
    // Object identity preserves distinct entries even when their package identifiers repeat.
    public bool MoveRowsTo(IEnumerable<LoadOrderRow> rows, int insertionIndex)
    {
        if (!HasDocument || !string.IsNullOrWhiteSpace(SearchText)
            || insertionIndex < 0 || insertionIndex > Rows.Count) return false;
        var requested = rows.Distinct().ToArray();
        if (requested.Length == 0 || requested.Any(row => !Rows.Contains(row))) return false;
        var selected = requested.OrderBy(Rows.IndexOf).ToArray();
        var selectedSet = selected.ToHashSet();
        var original = Rows.ToArray();
        var destination = insertionIndex - original.Take(insertionIndex).Count(selectedSet.Contains);
        var result = original.Where(row => !selectedSet.Contains(row)).ToList();
        result.InsertRange(destination, selected);
        if (result.SequenceEqual(original)) return false;
        var before = CaptureEditor();
        var primary = SelectedRow;
        Rows.ReplaceWith(result);
        RenumberRows();
        SelectOrderRows(selected, primary);
        OrderEdited();
        RecordEdit(before, selected.Length == 1 ? $"{selected[0].Name} 位置修改到 {selected[0].Index}"
            : $"{selected.Length} 个模组位置修改到 {destination + 1}–{destination + selected.Length}",
            $"撤销拖动：恢复 {selected.Length} 个模组的位置");
        return true;
    }
}
