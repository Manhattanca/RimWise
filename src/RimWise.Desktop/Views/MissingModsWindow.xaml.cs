using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Integration.Steam;

namespace RimWise.Desktop.Views;

internal sealed class MissingModDownloadRow : ObservableObject
{
    private string input;
    private bool selected = true;
    private bool busy;
    private bool completed;
    private string status;
    private readonly bool allowed;
    public string Identifier { get; }
    public MissingModDownloadRow(string identifier, string workshop, string notice, bool allowed = true)
    { Identifier = identifier; input = workshop; status = notice.Length > 0 ? notice : "尚未订阅下载"; this.allowed = allowed; }
    public string WorkshopInput
    {
        get => input;
        set { input = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanDownload)); OnPropertyChanged(nameof(HasSource)); }
    }
    public bool Selected { get => selected; set { selected = value; OnPropertyChanged(); } }
    public string Status { get => status; set { status = value; OnPropertyChanged(); } }
    public bool CanDownload => allowed && !busy && !completed && ImportedOrderDownloads.WorkshopIdFromInput(input) is not null;
    public bool HasSource => ImportedOrderDownloads.WorkshopIdFromInput(input) is not null;
    public bool CanEditSource => allowed && !busy && !completed;
    public bool NeedsSource => allowed && !completed && ImportedOrderDownloads.WorkshopIdFromInput(input) is null;
    public bool Subscribed { get; private set; }
    public void ConfirmSubscription() => Subscribed = true;
    public void SetBusy(bool value)
    { busy = value; OnPropertyChanged(nameof(CanDownload)); OnPropertyChanged(nameof(CanEditSource)); }
    public void Complete()
    { completed = true; Selected = false; Status = "Steam 已完成下载，已核对模组标识"; SetBusy(false); }
}

public partial class MissingModsWindow : Window
{
    internal IReadOnlyList<MissingModDownloadRow> Rows { get; private set; } = [];
    private readonly Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task> subscribe;
    private readonly Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task>? enqueue;
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationTokenRegistration cancellationRegistration;
    internal Task PendingDownloads { get; private set; } = Task.CompletedTask;
    internal Task PendingResolution { get; private set; } = Task.CompletedTask;
    internal string? ResolutionFailure { get; private set; }
    internal int CompletedCount { get; private set; }
    internal int SubscribedCount => Rows.Count(r => r.Subscribed);
    private bool running;
    private bool resolving;
    private bool closed;

    public MissingModsWindow(ImportDownloadPlan plan,
        Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task> subscribe, CancellationToken token,
        Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task>? enqueue = null)
    {
        InitializeComponent();
        this.subscribe = subscribe;
        this.enqueue = enqueue;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        ApplyPlan(plan);
        cancellationRegistration = token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (!closed) Close(); })));
    }

    internal MissingModsWindow(IReadOnlyList<string> missing,
        Func<IProgress<string>, CancellationToken, Task<ImportDownloadPlan>> resolve,
        Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task> subscribe, CancellationToken token,
        Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task>? enqueue = null)
        : this(new ImportDownloadPlan([], []), subscribe, token, enqueue)
    {
        resolving = true;
        ReplaceRows(missing.Select(id => new MissingModDownloadRow(id, "", "正在检索工坊来源…", false)).ToArray());
        SummaryText.Text = missing.Count > 0 ? $"缺少 {missing.Count} 个模组，正在检索…" : "正在核对缺失模组…";
        ProgressText.Text = "正在核对已安装模组并检索工坊来源，请稍候。";
        UpdateButtons();
        // Start after the window has been shown, so even slow startup/catalogue loading has visible feedback.
        Loaded += (_, _) => PendingResolution = ResolveAsync(resolve);
    }

    private void ReplaceRows(IReadOnlyList<MissingModDownloadRow> rows)
    {
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        Rows = rows;
        MissingGrid.ItemsSource = Rows;
        foreach (var row in Rows) row.PropertyChanged += RowChanged;
    }

    private void ApplyPlan(ImportDownloadPlan plan)
    {
        ReplaceRows(plan.Downloads.Select(d => new MissingModDownloadRow(d.Identifier, d.WorkshopId, d.Notice))
            .Concat(plan.Issues.Select(i => new MissingModDownloadRow(i.Identifier, "", i.Reason, i.CanSubscribe))).ToArray());
        SummaryText.Text = $"缺少 {Rows.Count} 个模组";
        UpdateButtons();
    }

    private async Task ResolveAsync(Func<IProgress<string>, CancellationToken, Task<ImportDownloadPlan>> resolve)
    {
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            cancellation.Token.ThrowIfCancellationRequested();
            var progress = new Progress<string>(message => { if (!closed && resolving) ProgressText.Text = message; });
            var plan = await resolve(progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closed) return;
            ApplyPlan(plan);
            ProgressText.Text = "检索完成，可选择通过 Steam 订阅下载。";
            if (Rows.Count == 0) Close();
        }
        catch (OperationCanceledException) { if (!closed) Close(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Xml.XmlException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException)
        {
            if (closed) return;
            ResolutionFailure = ex.Message;
            SummaryText.Text = "缺失模组检索未完成";
            foreach (var row in Rows) row.Status = "检索未完成，可关闭后重试";
            ProgressText.Text = "当前排序已保留，可关闭后重试。" + ex.Message;
        }
        finally
        {
            resolving = false;
            if (closed) cancellation.Dispose();
            else UpdateButtons();
        }
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e) => UpdateButtons();
    private void UpdateButtons()
    {
        DownloadSelectedButton.IsEnabled = !running && !resolving && Rows.Any(r => r.Selected && r.CanDownload);
        DownloadAllButton.IsEnabled = !running && !resolving && Rows.Any(r => r.CanDownload);
        CloseButton.Content = resolving ? "取消检索" : running ? "停止等待并关闭" : ResolutionFailure is not null || SubscribedCount > 0 || CompletedCount > 0 ? "关闭" : "暂不下载";
    }
    private void DownloadRow_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { DataContext: MissingModDownloadRow row }) StartDownloads([row]); }
    private void SearchRow_Click(object sender, RoutedEventArgs e)
    {
        if (running || resolving || closed || sender is not Button { DataContext: MissingModDownloadRow row } || !row.CanEditSource) return;
        var window = new WorkshopSearchWindow(row.Identifier) { Owner = this };
        if (window.ShowDialog() == true && window.SelectedItem is { } item && !closed)
        {
            row.WorkshopInput = item.Id;
            row.Status = $"已选择：{item.Title}；下载后将核对包名";
        }
    }
    private void PageRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MissingModDownloadRow row }
            && ImportedOrderDownloads.WorkshopIdFromInput(row.WorkshopInput) is { } id
            && new ModLocationOpener().OpenWorkshop(new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id)) == WorkshopOpenResult.Failed)
            row.Status = "无法打开 Steam 或浏览器，请复制工坊编号手动查找。";
    }
    private void DownloadSelected_Click(object sender, RoutedEventArgs e) => StartDownloads(Rows.Where(r => r.Selected));
    private void DownloadAll_Click(object sender, RoutedEventArgs e) => StartDownloads(Rows);
    private void StartDownloads(IEnumerable<MissingModDownloadRow> rows)
    {
        if (running || resolving || closed) return;
        // Snapshot IDs before awaiting; editing another row cannot redirect an in-flight subscription.
        var pending = rows.Where(r => r.CanDownload).Select(r => (Row: r, Id: ImportedOrderDownloads.WorkshopIdFromInput(r.WorkshopInput)!)).ToArray();
        if (pending.Length > 0) PendingDownloads = RunAsync(pending);
    }
    private async Task RunAsync((MissingModDownloadRow Row, string Id)[] pending)
    {
        running = true; UpdateButtons();
        foreach (var row in Rows) row.SetBusy(true);
        foreach (var (row, _) in pending) row.Status = "等待提交 Steam 订阅";
        var queued = new List<(MissingModDownloadRow Row, string Id)>();
        var failures = 0;
        try
        {
            // Do not wait for the first large/paused download before subscribing the rest of the collection.
            foreach (var (row, id) in pending)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (enqueue is null || await RunRowAsync(row, id, enqueue, "正在向 Steam 提交订阅"))
                {
                    if (enqueue is not null) row.ConfirmSubscription();
                    row.Status = "已提交订阅，等待 Steam 下载";
                    queued.Add((row, id));
                }
            }
            foreach (var (row, id) in queued)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (await RunRowAsync(row, id, subscribe, "正在等待 Steam 下载并核对安装"))
                { row.ConfirmSubscription(); row.Complete(); CompletedCount++; }
            }
            var unknown = Rows.Count(r => r.NeedsSource);
            ProgressText.Text = $"已确认订阅 {SubscribedCount} 项，下载完成 {CompletedCount} 项；本次失败 {failures} 项"
                + (unknown > 0 ? $"；另有 {unknown} 项需填写工坊来源" : "") + "。未完成项仍保留在排序中。";
        }
        catch (OperationCanceledException)
        {
            foreach (var (row, _) in pending)
                if (row.Status.StartsWith("等待提交") || row.Status.StartsWith("已提交") || row.Status.StartsWith("正在"))
                    row.Status = row.Subscribed ? "已订阅，已停止等待；Steam 可能仍在下载" : "已停止；尚未确认订阅结果";
            ProgressText.Text = $"已停止等待；已确认订阅 {SubscribedCount} 项，下载完成 {CompletedCount} 项。Steam 已接收的订阅可能仍在下载";
        }
        finally
        {
            running = false;
            foreach (var row in Rows) row.SetBusy(false);
            UpdateButtons();
            if (closed) cancellation.Dispose();
        }

        async Task<bool> RunRowAsync(MissingModDownloadRow row, string id,
            Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task> operation, string stage)
        {
            var reporting = true;
            var watch = Stopwatch.StartNew();
            var message = stage;
            void Report()
            {
                if (closed || !reporting) return;
                row.Status = message + $"（本步已等待 {watch.Elapsed:mm\\:ss}）";
                ProgressText.Text = $"本次 {pending.Length} 项；已确认订阅 {SubscribedCount} 项，下载完成 {CompletedCount} 项。{row.Identifier}：{row.Status}";
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => Report();
            try
            {
                Report(); timer.Start();
                // Ignore delayed progress posts after an operation has finished or failed.
                var progress = new Progress<string>(text => { if (reporting) { message = text; Report(); } });
                await operation(new([new(row.Identifier, id)], []), progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                return true;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { row.Status = "未完成：本项 Steam 请求超时，可重试；继续处理其他模组"; failures++; return false; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or TimeoutException)
            { row.Status = "未完成：" + ex.Message; failures++; return false; }
            finally { reporting = false; timer.Stop(); }
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosed(EventArgs e)
    {
        closed = true; cancellation.Cancel(); cancellationRegistration.Dispose();
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        if (!running && !resolving) cancellation.Dispose();
        base.OnClosed(e);
    }
}
