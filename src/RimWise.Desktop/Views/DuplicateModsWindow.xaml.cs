using System.IO;
using System.Windows;
using RimWise.Core;
using RimWise.Desktop.Services;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop.Views;

internal sealed record DuplicateModRow(string Identifier, ModInstallation Installation, DuplicateCopyInspection? Inspection = null, bool Recommended = false)
{
    public string Label => Installation.Name + "\n" + Identifier;
    public string Source => DuplicateInstallations.SourceName(Installation.Source);
    public string Directory => Installation.Directory;
    public bool HasWorkshop => ulong.TryParse(Installation.WorkshopId, out var id) && id > 0;
    public string Version => string.IsNullOrWhiteSpace(Inspection?.Version) ? "版本未知" : Inspection.Version + "（About.xml）";
    public string Check => (Recommended ? "推荐 · " : "") + (Inspection?.Status ?? "待检查");
}

public partial class DuplicateModsWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<OrderMatch>>> refresh;
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;
    private bool applying;
    private bool inspecting;
    private readonly ModLocationOpener opener = new();
    private readonly Func<IReadOnlyList<DuplicateCopyInspection>, DuplicateCopyInspection, CancellationToken, Task<bool>>? choose;
    private readonly GameVersion? version;
    private readonly IReadOnlySet<string> active;
    private IReadOnlyList<OrderMatch> duplicates = [];

    internal DuplicateModsWindow(IReadOnlyList<OrderMatch> duplicates,
        Func<CancellationToken, Task<IReadOnlyList<OrderMatch>>> refresh,
        Func<IReadOnlyList<DuplicateCopyInspection>, DuplicateCopyInspection, CancellationToken, Task<bool>>? choose = null,
        GameVersion? version = null, IReadOnlySet<string>? active = null)
    {
        InitializeComponent();
        this.refresh = refresh;
        this.choose = choose; this.version = version; this.active = active ?? new HashSet<string>();
        SetDuplicates(duplicates);
        Loaded += async (_, _) => await InspectAsync();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); };
        Closing += (_, e) => { if (applying) { lifetime.Cancel(); e.Cancel = true; } };
    }

    private void SetDuplicates(IReadOnlyList<OrderMatch> duplicates)
    {
        this.duplicates = duplicates;
        ChooseButton.IsEnabled = false;
        InstallationsGrid.ItemsSource = duplicates.SelectMany(match =>
            match.Candidates.Select(mod => new DuplicateModRow(match.Identifier, mod))).ToArray();
        StatusText.Text = $"有 {duplicates.Count} 个标识需要处理。";
    }

    internal async Task InspectAsync()
    {
        if (choose is null || closed || inspecting) return;
        inspecting = true;
        ChooseButton.IsEnabled = false;
        StatusText.Text = "正在检查副本文件与版本…";
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = duplicates;
            var rows = await Task.Run(() => snapshot.SelectMany(match =>
            {
                var ranked = DuplicateCopyInspector.Rank(match.Candidates.Select(mod =>
                    DuplicateCopyInspector.Inspect(mod, version, active, lifetime.Token)));
                return ranked.Select((item, index) => new DuplicateModRow(match.Identifier, item.Installation, item, index == 0));
            }).ToArray(), lifetime.Token);
            if (closed) return;
            InstallationsGrid.ItemsSource = rows;
            InstallationsGrid.SelectedIndex = rows.Length > 0 ? 0 : -1;
            ChooseButton.IsEnabled = rows.Length > 0;
            StatusText.Text = "选择要使用的一行，再确认移动方案。版本未知或检查不完整时，推荐仅供参考；可改选或返回。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { if (!closed) StatusText.Text = "副本检查未完成：" + ex.Message; }
        finally { inspecting = false; if (!closed) RefreshButton.IsEnabled = true; }
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (choose is null || InstallationsGrid.SelectedItem is not DuplicateModRow { Inspection: { } selected } row) return;
        ChooseButton.IsEnabled = RefreshButton.IsEnabled = false;
        applying = true;
        try
        {
            var group = ((IEnumerable<DuplicateModRow>)InstallationsGrid.ItemsSource)
                .Where(r => r.Identifier == row.Identifier).Select(r => r.Inspection!).ToArray();
            if (await choose(group, selected, lifetime.Token) && !closed)
            {
                SetDuplicates(await refresh(lifetime.Token));
                if (duplicates.Count == 0) { applying = false; DialogResult = true; return; }
                await InspectAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!closed) StatusText.Text = "切换未完成：" + ex.Message + "\n如已移动部分副本，可从自动排序页面的“副本备份”恢复。"; }
        finally
        {
            applying = false;
            if (!closed && lifetime.IsCancellationRequested) Close();
            else if (!closed) ChooseButton.IsEnabled = RefreshButton.IsEnabled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    internal async Task RefreshAsync()
    {
        if (closed || !RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在刷新安装信息，当前排序保持不变…";
        try
        {
            var duplicates = await refresh(lifetime.Token);
            if (closed) return;
            SetDuplicates(duplicates);
            DiagnosticEventLog.Current?.Record("automatic-sorting", "duplicate-installations-refreshed", $"remaining={duplicates.Count}");
            if (duplicates.Count == 0) DialogResult = true;
            else await InspectAsync();
        }
        catch (OperationCanceledException) { if (!closed) StatusText.Text = "已取消刷新。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!closed) StatusText.Text = "刷新未完成：" + ex.Message;
            DiagnosticEventLog.Current?.Record("automatic-sorting", "duplicate-refresh-failed", ex.Message);
        }
        finally { if (!closed) RefreshButton.IsEnabled = true; }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateModRow row && !opener.OpenDirectory(row.Directory))
            StatusText.Text = "无法打开该文件夹，请复制安装路径检查。";
    }

    private void Workshop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateModRow { HasWorkshop: true } row
            && opener.OpenWorkshop(new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=" + row.Installation.WorkshopId)) == WorkshopOpenResult.Failed)
            StatusText.Text = "无法打开创意工坊，请检查 Steam 或浏览器。";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
