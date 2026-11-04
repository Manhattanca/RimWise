using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RimWise.Desktop.Presentation;
using RimWise.Integration.AI;

namespace RimWise.Desktop.Views;

public partial class InsertionPickerWindow : Window
{
    private readonly ObservableCollection<InsertionChoice> available;
    private readonly ObservableCollection<InsertionChoice> selected;
    private readonly ListCollectionView leftView;
    private readonly ListCollectionView rightView;
    internal string[] SelectedIdentifiers { get; private set; } = [];
    internal InsertionPickerWindow(MainViewModel editor)
    {
        InitializeComponent();
        DataContext = new { editor.ShowModBadgeIcons, ShowModBadgeText = !editor.ShowModBadgeIcons };
        available = new(editor.AvailableInsertionMods());
        selected = new(editor.PendingInsertionMods());
        leftView = new(available); rightView = new(selected);
        ModsList.ItemsSource = leftView; SelectedList.ItemsSource = rightView;
        UpdateCounts();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (leftView is null) return;
        static bool Matches(object item, string query) => item is InsertionChoice row &&
            (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.PackageId.Contains(query, StringComparison.OrdinalIgnoreCase));
        leftView.Filter = item => Matches(item, SearchBox.Text.Trim());
        rightView.Filter = item => Matches(item, SelectedSearchBox.Text.Trim());
    }
    internal void MoveSelection(bool add)
    {
        var list = add ? ModsList : SelectedList;
        var from = add ? available : selected;
        var to = add ? selected : available;
        var rows = list.SelectedItems.Cast<InsertionChoice>().ToHashSet();
        foreach (var row in from.Where(rows.Contains).ToArray())
        {
            if (add && !AiModIdentifier.IsValid(row.PackageId)) continue;
            from.Remove(row);
            if (!to.Any(other => string.Equals(other.PackageId, row.PackageId, StringComparison.OrdinalIgnoreCase))) to.Add(row);
        }
        UpdateCounts();
    }
    private void UpdateCounts()
    {
        AvailableTitle.Text = $"未启用（{available.Count}）";
        SelectedTitle.Text = $"准备测试（{selected.Count}）";
        CountText.Text = selected.Count == 0
            ? "未选择新增 Mod，可直接整理当前排序并测试；无需添加模组。"
            : "支持多选、双击、回车及左右方向键移动。";
        ConfirmButton.Content = selected.Count == 0 ? "整理当前排序并测试" : "确认并测试";
        ConfirmButton.IsEnabled = true;
    }
    private void Add_Click(object sender, RoutedEventArgs e) => MoveSelection(true);
    private void Remove_Click(object sender, RoutedEventArgs e) => MoveSelection(false);
    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(grid, source) is DataGridRow)
            MoveSelection(ReferenceEquals(grid, ModsList));
    }
    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        var add = ReferenceEquals(sender, ModsList);
        if (e.Key == Key.Enter || e.Key == (add ? Key.Right : Key.Left) || !add && e.Key == Key.Delete)
        { MoveSelection(add); e.Handled = true; }
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        SelectedIdentifiers = selected.Select(row => row.PackageId).ToArray();
        if (SelectedIdentifiers.Any(id => !AiModIdentifier.IsValid(id)))
        { CountText.Text = "部分模组缺少有效标识，请移出后重新选择。"; return; }
        DialogResult = true;
    }
}

