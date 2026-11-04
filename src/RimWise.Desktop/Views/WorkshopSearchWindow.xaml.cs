using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RimWise.Desktop.Services;
using RimWise.Integration.Steam;

namespace RimWise.Desktop.Views;

public partial class WorkshopSearchWindow : Window
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<WorkshopSearchItem>>> search;
    private readonly CancellationTokenSource cancellation = new();
    private bool busy;
    private bool closed;
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;
    public WorkshopSearchItem? SelectedItem { get; private set; }

    public WorkshopSearchWindow(string text) : this(text, new SteamWorkshopClient(AppContext.BaseDirectory).SearchAsync) { }

    internal WorkshopSearchWindow(string text, Func<string, CancellationToken, Task<IReadOnlyList<WorkshopSearchItem>>> search)
    {
        InitializeComponent();
        this.search = search;
        SearchText.Text = text;
        Loaded += (_, _) => StartSearch();
    }

    private void StartSearch()
    {
        if (!busy && !closed) PendingSearch = SearchAsync();
    }
    private async Task SearchAsync()
    {
        busy = true;
        SearchButton.IsEnabled = false;
        ResultsGrid.ItemsSource = null;
        PageButton.IsEnabled = ChooseButton.IsEnabled = false;
        StatusText.Text = "正在向 Steam 查询环世界工坊…";
        try
        {
            var items = await search(SearchText.Text, cancellation.Token);
            if (closed) return;
            ResultsGrid.ItemsSource = items;
            StatusText.Text = items.Count == 0 ? "未找到公开可访问的结果，请改用模组名称或其他关键词。"
                : $"找到 {items.Count} 个候选（最多显示 50 个）。请核对名称及工坊页面；搜索匹配不代表包名一致。";
        }
        catch (OperationCanceledException) { if (!closed) StatusText.Text = "搜索已取消，可重试。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or TimeoutException)
        { if (!closed) StatusText.Text = "搜索未完成：" + ex.Message; }
        finally
        {
            busy = false;
            if (closed) cancellation.Dispose();
            else
            {
                SearchButton.IsEnabled = true;
                PageButton.IsEnabled = ChooseButton.IsEnabled = ResultsGrid.SelectedItem is WorkshopSearchItem;
            }
        }
    }
    private void Search_Click(object sender, RoutedEventArgs e) => StartSearch();
    private void SearchText_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; StartSearch(); } }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (PageButton is not null) PageButton.IsEnabled = ChooseButton.IsEnabled = !busy && ResultsGrid.SelectedItem is WorkshopSearchItem; }
    private void Page_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is WorkshopSearchItem item && new ModLocationOpener().OpenWorkshop(
            new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=" + item.Id)) == WorkshopOpenResult.Failed)
            StatusText.Text = "无法打开 Steam 或浏览器，请复制工坊编号手动查找。";
    }
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ResultsGrid.SelectedItem is not WorkshopSearchItem item) return;
        SelectedItem = item;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        cancellation.Cancel();
        if (!busy) cancellation.Dispose();
        base.OnClosed(e);
    }
}
