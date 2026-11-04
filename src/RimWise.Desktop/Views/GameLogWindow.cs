using System.IO;
using System.Windows;
using System.Windows.Controls;
using RimWise.Core;
using RimWise.Integration.Game;

namespace RimWise.Desktop.Views;

public sealed class GameLogWindow : Window
{
    private readonly string path;
    private bool refreshing;
    private bool closed;
    private readonly ModCatalogue? catalogue;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ListBox issues = new();
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    public GameLogWindow(string path, ModCatalogue? catalogue)
    {
        this.path = path; this.catalogue = catalogue;
        Title = "游戏日志"; Width = 980; Height = 690; MinWidth = 620; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(20) };
        var refresh = new Button { Content = "刷新日志", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += async (_, _) => await RefreshAsync();
        DockPanel.SetDock(refresh, Dock.Top); panel.Children.Add(refresh);
        DockPanel.SetDock(status, Dock.Top); panel.Children.Add(status);
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new GridLength(2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(3, GridUnitType.Star) });
        grid.Children.Add(issues); Grid.SetRow(detail, 1); detail.Margin = new Thickness(0, 12, 0, 0); grid.Children.Add(detail);
        panel.Children.Add(grid); Content = panel;
        issues.SelectionChanged += (_, _) =>
        {
            if (issues.SelectedItem is ListBoxItem { Tag: GameLogIssue issue })
                detail.Text = issue.Detail + (issue.RelatedMods.Length > 0 ? "\n\n可能相关（根据标识匹配）：" + string.Join("、", issue.RelatedMods) : "");
        };
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => closed = true;
    }
    private async Task RefreshAsync()
    {
        if (refreshing || closed) return;
        refreshing = true;
        try
        {
            var snapshot = await Task.Run(() => PlayerLogReader.Read(path, catalogue));
            if (closed) return;
            status.Text = $"{snapshot.Path}\n{snapshot.LastWriteUtc.LocalDateTime:g} · {snapshot.Issues.Count} 类疑似错误"
                + (snapshot.ContextAbbreviated ? " · 正文显示摘要，错误已扫描完整日志" : "")
                + (snapshot.Truncated ? " · 日志采集不完整" : "");
            issues.ItemsSource = snapshot.Issues.Select(issue => new ListBoxItem { Content = $"{issue.Count} 次 · {issue.Summary}", Tag = issue }).ToArray();
            detail.Text = snapshot.Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!closed) status.Text = ex.Message; }
        finally { refreshing = false; }
    }
#if DEBUG
    internal async Task RefreshForCheckAsync()
    {
        while (refreshing) await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await RefreshAsync();
    }
#endif
}
