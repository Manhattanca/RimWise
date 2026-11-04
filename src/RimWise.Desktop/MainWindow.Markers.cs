using System.Windows;
using System.Windows.Controls;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Views;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private void SourceFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string value } }
            && Enum.TryParse<ModSourceFilter>(value, out var filter))
        {
            var enabledList = ReferenceEquals(sender, OrderSourceFilterBox);
            ViewModel.SetSourceFilter(filter, enabledList);
            if (!listDisplayInitialized) return;
            listDisplay = listDisplay with { AvailableSource = (int)ViewModel.AvailableSourceFilter,
                OrderSource = (int)ViewModel.OrderSourceFilter };
            SaveListDisplay();
        }
    }

    private void AvailableSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string value } }) return;
        if (Enum.TryParse<AvailableModSortField>(value, out var field)) ViewModel.SetAvailableSortField(field);
    }

    private void AvailableSortDirection_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleAvailableSortDirection();

    private void BatchMark_Click(object sender, RoutedEventArgs e) => OpenBatchMarkWindow();

    private void OpenBatchMarkWindow()
    {
        var count = ViewModel.SelectedOrderRows.Count + ViewModel.SelectedAvailableRows.Count;
        if (count == 0)
        {
            ViewModel.Status = "请先选择要标记的模组";
            return;
        }

        var dialog = new BatchMarkWindow(count, ViewModel.RetainedColors) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Action == BatchMarkAction.None) return;
        try
        {
            if (dialog.Action is BatchMarkAction.Color or BatchMarkAction.ClearColor)
            {
                ViewModel.ApplyColor(dialog.SelectedColor, ViewModel.SelectedOrderRows, ViewModel.SelectedAvailableRows);
                return;
            }
            if (dialog.SelectedColor is { } color)
                ViewModel.SetColorRetention(color, dialog.Action == BatchMarkAction.RetainColor);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ViewModel.Status = "批量标记未完成：" + ex.Message;
        }
    }
}
