using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RimWise.Integration.Marketplace;
using RimWise.Desktop.Services;

namespace RimWise.Desktop.Views;

public sealed class MarketplaceMyOrdersActions
{
    public Action<MarketplaceSavedDraft>? DeleteDraft { get; init; }
    public Func<MarketplaceEntry, CancellationToken, Task<MarketplaceSubmission>>? DeleteOrderAsync { get; init; }
    internal Func<string, bool>? ConfirmDelete { get; init; }
}

public partial class MarketplaceMyOrdersWindow : Window
{
    private readonly MarketplaceMyOrdersActions actions;
    private readonly HashSet<string> pendingDeletions = new(StringComparer.OrdinalIgnoreCase);
    public MarketplaceEntry? SelectedEntry => OrdersList.SelectedItem as MarketplaceEntry;
    public MarketplaceSavedDraft? SelectedDraft => DraftsList.SelectedItem as MarketplaceSavedDraft;
    public bool IsBusy { get; private set; }
    public Task PendingOperation { get; private set; } = Task.CompletedTask;

    public MarketplaceMyOrdersWindow(IReadOnlyList<MarketplaceEntry> entries, IReadOnlyList<MarketplaceSavedDraft>? drafts = null,
        MarketplaceMyOrdersActions? actions = null)
    {
        this.actions = actions ?? new();
        InitializeComponent();
        OrdersList.ItemsSource = entries;
        DraftsList.ItemsSource = drafts ?? [];
        EmptyText.Text = entries.Count == 0 ? "当前没有你已上架的排序；可登录后刷新市场。" : "";
        if (drafts?.Count == 0) EmptyText.Text += " 本机尚无草稿，在发布窗口点击“保存草稿”即可。";
        EmptyText.Visibility = EmptyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Order_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedEntry is not null) DraftsList.SelectedIndex = -1;
        RefreshState();
    }
    private void Draft_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDraft is not null) OrdersList.SelectedIndex = -1;
        RefreshState();
    }
    private void RefreshState() => EditButton.IsEnabled = !IsBusy && (SelectedDraft is not null || SelectedEntry is not null);
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (EditButton.IsEnabled) DialogResult = true;
    }
    private void List_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (IsBusy) { e.Handled = true; return; }
        var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
        list.SelectedItem = item?.DataContext;
        if (item is null) { e.Handled = true; return; }
        item.Focus();
    }
    private void List_MenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is null || IsBusy) { e.Handled = true; return; }
        var remove = (MenuItem)list.ContextMenu.Items[1];
        remove.IsEnabled = list.SelectedItem is MarketplaceSavedDraft ? actions.DeleteDraft is not null
            : list.SelectedItem is MarketplaceEntry entry && actions.DeleteOrderAsync is not null && !pendingDeletions.Contains(entry.Author + "/" + entry.Id);
    }
    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBox list || IsBusy) return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        list.SelectedItem = item.DataContext;
        e.Handled = true;
        Edit_Click(sender, e);
    }
    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (IsBusy || (SelectedDraft is null && SelectedEntry is null)) return;
        if (e.Key == Key.Enter) { e.Handled = true; Edit_Click(sender, e); }
        else if (e.Key == Key.Delete) { e.Handled = true; PendingOperation = DeleteSelectedAsync(); }
    }
    private void Delete_Click(object sender, RoutedEventArgs e) => PendingOperation = DeleteSelectedAsync();
    public async Task DeleteSelectedAsync()
    {
        if (IsBusy) return;
        var draft = SelectedDraft;
        var entry = SelectedEntry;
        if (draft is null && entry is null) return;
        if (draft is not null && actions.DeleteDraft is null || entry is not null &&
            (actions.DeleteOrderAsync is null || pendingDeletions.Contains(entry.Author + "/" + entry.Id))) return;
        var question = draft is not null ? $"删除草稿“{draft.Title}”？删除后无法继续编辑这份草稿。"
            : $"申请删除已发布排序“{entry!.Title}”的所有版本？审核通过后从市场下架，不影响已下载的排序或模组文件。";
        if (!(actions.ConfirmDelete?.Invoke(question) ?? MessageDialog.Show(this, question, "删除排序", MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes)) return;
        IsBusy = true;
        OrdersList.IsEnabled = DraftsList.IsEnabled = false;
        RefreshState();
        try
        {
            if (draft is not null)
            {
                actions.DeleteDraft!(draft);
                DraftsList.ItemsSource = DraftsList.Items.Cast<MarketplaceSavedDraft>().Where(item => item.Key != draft.Key).ToArray();
                EmptyText.Text = "草稿已删除。";
            }
            else
            {
                EmptyText.Text = "正在提交删除审核…";
                EmptyText.Visibility = Visibility.Visible;
                var result = await actions.DeleteOrderAsync!(entry!, CancellationToken.None);
                pendingDeletions.Add(entry!.Author + "/" + entry.Id);
                EmptyText.Text = $"删除申请已提交（审核 #{result.PullRequestNumber}），审核通过后下架；可稍后刷新市场查看。";
            }
        }
        catch (Exception ex)
        {
            var message = new RimWise.Integration.Diagnostics.DiagnosticRedactor().Text(ex.Message);
            EmptyText.Text = message.Any(c => c is >= '\u3400' and <= '\u9fff') ? "删除未完成：" + message : "删除未完成，请检查网络或文件权限后重试。";
        }
        finally
        {
            IsBusy = false;
            OrdersList.IsEnabled = DraftsList.IsEnabled = true;
            EmptyText.Visibility = Visibility.Visible;
            RefreshState();
        }
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
