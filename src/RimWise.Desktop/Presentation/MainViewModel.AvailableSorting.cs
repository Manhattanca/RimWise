using System.Windows.Data;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    public AvailableModSortField AvailableSortField { get; private set; } = AvailableModSortField.Name;
    public bool IsAvailableSortDescending { get; private set; }
    public string AvailableSortDirectionLabel => IsAvailableSortDescending ? "降序" : "正序";

    public void SetAvailableSortField(AvailableModSortField field)
    {
        if (!Enum.IsDefined(field)) throw new ArgumentOutOfRangeException(nameof(field));
        if (AvailableSortField == field) return;
        AvailableSortField = field;
        if (field is AvailableModSortField.RecentlyAdded or AvailableModSortField.RecentlyUpdated)
        {
            IsAvailableSortDescending = true;
            OnPropertyChanged(nameof(IsAvailableSortDescending));
            OnPropertyChanged(nameof(AvailableSortDirectionLabel));
        }
        ApplyAvailableSort();
        OnPropertyChanged(nameof(AvailableSortField));
    }

    public void ToggleAvailableSortDirection()
    {
        IsAvailableSortDescending = !IsAvailableSortDescending;
        ApplyAvailableSort();
        OnPropertyChanged(nameof(IsAvailableSortDescending));
        OnPropertyChanged(nameof(AvailableSortDirectionLabel));
    }

    private void ApplyAvailableSort()
    {
        var selection = selectedAvailableRows.ToArray();
        var primary = SelectedAvailable;
        ((ListCollectionView)VisibleAvailableMods).CustomSort = new AvailableModComparer(AvailableSortField, IsAvailableSortDescending);
        if (selection.Length > 0) SelectAvailableRows(selection, primary);
    }

    private IEnumerable<InstalledModRow> OrderAvailableRows(IEnumerable<InstalledModRow> rows) =>
        rows.OrderBy(row => row, new AvailableModComparer(AvailableSortField, IsAvailableSortDescending));
}
