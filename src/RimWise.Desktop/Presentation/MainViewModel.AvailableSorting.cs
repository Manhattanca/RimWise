using System.Windows.Data;
using System.ComponentModel;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    public AvailableModSortField AvailableSortField { get; private set; } = AvailableModSortField.Name;
    public bool IsAvailableSortDescending { get; private set; }
    public string AvailableSortDirectionLabel => IsAvailableSortDescending ? "倒序" : "正序";
    public bool IsOrderSortDescending { get; private set; }
    public string OrderSortDirectionLabel => IsOrderSortDescending ? "倒序" : "正序";

    public void SetAvailableSortField(AvailableModSortField field)
    {
        if (!Enum.IsDefined(field)) throw new ArgumentOutOfRangeException(nameof(field));
        if (AvailableSortField == field) return;
        AvailableSortField = field;
        // The field belongs only to the inactive list; it must not silently flip any display direction.
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

    public void ToggleOrderSortDirection()
    {
        IsOrderSortDescending = !IsOrderSortDescending;
        ApplyOrderSort();
        OnPropertyChanged(nameof(IsOrderSortDescending));
        OnPropertyChanged(nameof(OrderSortDirectionLabel));
    }

    private void ApplyAvailableSort()
    {
        var selection = selectedAvailableRows.ToArray();
        var primary = SelectedAvailable;
        ((ListCollectionView)VisibleAvailableMods).CustomSort = new AvailableModComparer(AvailableSortField, IsAvailableSortDescending);
        if (selection.Length > 0) SelectAvailableRows(selection, primary);
    }

    private void ApplyOrderSort()
    {
        // Sort only the view by the real load index. Rows, export, undo and saving remain untouched.
        var orderSelection = SelectedOrderRows.ToArray();
        var orderPrimary = SelectedRow;
        using (VisibleRows.DeferRefresh())
        {
            VisibleRows.SortDescriptions.Clear();
            if (IsOrderSortDescending)
                VisibleRows.SortDescriptions.Add(new SortDescription(nameof(LoadOrderRow.Index), ListSortDirection.Descending));
        }
        if (orderSelection.Length > 0) SelectOrderRows(orderSelection, orderPrimary);
    }

    private IEnumerable<InstalledModRow> OrderAvailableRows(IEnumerable<InstalledModRow> rows) =>
        rows.OrderBy(row => row, new AvailableModComparer(AvailableSortField, IsAvailableSortDescending));
}
