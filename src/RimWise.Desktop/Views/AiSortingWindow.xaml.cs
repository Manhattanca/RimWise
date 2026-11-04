using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow : Window
{
    private readonly MainViewModel editor;
    private readonly AiSortingSession session;
    private readonly CancellationToken ownerLifetime;
    private CancellationTokenSource? requestCancellation;
    private AiSortingOutcome? outcome;
    private AiSettings settings = AiSettings.Default;
    private bool closed;
    private bool clearingLogs;
    private readonly string dataDirectory;
    private bool updatingPermission;
    private readonly AutomaticSortingPreferencesStore sortingPreferences;
    private readonly Func<Task>? prepareCatalogue;
    private AiUsageMonitorWindow? usageWindow;
    internal bool IsWorking => requestCancellation is not null || clearingLogs || savingLogs;

    public AiSortingWindow(MainViewModel editor, string dataDirectory, CancellationToken ownerLifetime = default,
        IAiClient? client = null, ISecretStore? secrets = null, Func<GamePaths>? gamePaths = null,
        Func<Action>? prepareSave = null, Func<Task>? prepareCatalogue = null)
    {
        InitializeComponent();
        this.dataDirectory = dataDirectory;
        logStore = new(dataDirectory);
        LoadLogSettings();
        sortingPreferences = new(dataDirectory);
        try
        {
            var preferences = sortingPreferences.Load();
            AttemptLimitToggle.IsChecked = preferences.AttemptLimitEnabled;
            TokenLimitToggle.IsChecked = preferences.TokenLimitEnabled;
            AttemptsInput.Text = preferences.MaximumAttempts.ToString();
            BudgetInput.Text = preferences.TokenBudget.ToString();
        }
        catch (Exception ex) when (IsExpectedError(ex))
        { RimWise.Integration.Diagnostics.DiagnosticEventLog.Current?.Record("automatic-sorting", "preferences-load-failed", ex.Message); }
        this.editor = editor;
        this.ownerLifetime = ownerLifetime;
        session = new(editor, dataDirectory, client, secrets);
        this.gamePaths = gamePaths;
        this.prepareSave = prepareSave;
        this.prepareCatalogue = prepareCatalogue;
        automatic = new(editor,dataDirectory,session,client);
        editor.PropertyChanged += Editor_Changed;
        Activated += (_, _) => RefreshConfiguration();
        RefreshConfiguration();
        RefreshUsage();
    }

    internal void RefreshConfiguration()
    {
        if (closed) return;
        try
        {
            settings = session.SettingsStore.Load();
            updatingPermission = true;
            try
            {
                if (requestCancellation is null)
                {
                    var preferences = sortingPreferences.Load();
                    PermissionBox.SelectedIndex = preferences.Mode switch { "insert" => 0, "full" => 1, _ => 2 };
                    ConfirmRemovalToggle.IsChecked = preferences.ConfirmModRemoval;
                }
            }
            finally { updatingPermission = false; }
            ConfigurationText.Text = settings.Enabled ? $"AI 模型：{(settings.Model.Length == 0 ? "未选择模型" : settings.Model)}"
                : "尚未启用 AI";
            if (outcome is not null && settings != outcome.Settings) InvalidateResult("AI 设置已变化，请重新开始排序。");
            if (requestCancellation is null && outcome is null && !automaticResultShown)
                StatusText.Text = session.ReadinessProblem ?? (PermissionBox.SelectedIndex == 2
                    ? "模组适配尚未完成，请选择其他排序模式。"
                    : settings.Enabled ? "点击自动排序并测试，自动补齐必要前置并循环测试。" : "请先在 AI 设置中配置并启用 AI。");
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            settings = AiSettings.Default;
            StatusText.Text = "无法读取 AI 设置：" + ex.Message;
        }
        RefreshButtons();
    }

    private void Permission_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPermission || closed || session is null) return;
        if (requestCancellation is not null) { RefreshConfiguration(); return; }
        var level = (PermissionBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (level is null) return;
        try
        {
            var current = session.SettingsStore.Load();
            session.SettingsStore.Save(current with { PermissionLevel = level switch
            {
                "adapt" => AiPermissionLevel.Level3CodeAdaptation,
                _ => AiPermissionLevel.Level1OrderAndRequiredDependencies
            } });
            sortingPreferences.SaveMode(level, ConfirmRemovalToggle.IsChecked == true);
            RefreshConfiguration();
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            RefreshConfiguration();
            StatusText.Text = "运行模式未保存：" + ex.Message;
        }
    }

    private void RemovalPolicy_Click(object sender, RoutedEventArgs e)
    {
        try { sortingPreferences.SaveMode((PermissionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "insert", ConfirmRemovalToggle.IsChecked == true); }
        catch (Exception ex) when (IsExpectedError(ex)) { StatusText.Text = "移出确认设置未保存：" + ex.Message; }
    }

    private void Editor_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (closed || e.PropertyName is not (nameof(MainViewModel.ContentRevision) or nameof(MainViewModel.HasDocument)
            or nameof(MainViewModel.HasCatalogue) or nameof(MainViewModel.IsScanning) or nameof(MainViewModel.IsEnrichingContent) or nameof(MainViewModel.IsShowingCache))) return;
        if (outcome is not null && !session.CanApply(outcome))
            InvalidateResult("当前排序或模组资料已变化，请重新开始排序。");
        else if (outcome is null && requestCancellation is null)
        {
            if (automaticResultShown && editor.ContentRevision != automaticResultRevision)
                StatusText.Text = "当前排序或模组资料已变化，上次自动测试结果仅供参考。";
            else RefreshConfiguration();
        }
        RefreshButtons();
    }

    private void InvalidateResult(string message)
    {
        if (outcome is null) return;
        outcome = outcome with { MayApply = false };
        StatusText.Text = message;
    }

    private void RefreshButtons()
    {
        var busy = requestCancellation is not null || clearingLogs || savingLogs;
        AutomaticButton.IsEnabled = !closed && !busy && settings.Enabled && session.ReadinessProblem is null && gamePaths is not null
            && PermissionBox.SelectedIndex is 0 or 1;
        LimitPanel.IsEnabled = !busy;
        PermissionBox.IsEnabled = !busy;
        ConfirmRemovalToggle.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        EstimateButton.IsEnabled = !busy && session.ReadinessProblem is null;
        RecoveryButton.IsEnabled = !busy;
        CancelButton.IsEnabled = requestCancellation is { IsCancellationRequested: false };
        ClearLogsButton.IsEnabled = !closed && !busy;
        LogSettingsPanel.IsEnabled = !closed && !busy;
        ApplyButton.IsEnabled = !busy && outcome is not null && session.CanApply(outcome) && settings == outcome.Settings;
        ApplyButton.Visibility = outcome is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new AiSettingsWindow(session.SettingsStore.Load(), session.SettingsStore, session.Secrets) { Owner = this }.ShowDialog();
            RefreshConfiguration();
        }
        catch (Exception ex) when (IsExpectedError(ex)) { StatusText.Text = "无法打开 AI 设置：" + ex.Message; }
    }

    private void Usage_Click(object sender, RoutedEventArgs e)
    {
        if (usageWindow is not null) { usageWindow.Activate(); return; }
        usageWindow = new(session.SettingsStore, session.Secrets, session.Tasks) { Owner = this };
        usageWindow.Closed += (_, _) => usageWindow = null;
        usageWindow.Show();
    }

    private void Estimate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshConfiguration();
            if (session.ReadinessProblem is { } problem) { StatusText.Text = problem; return; }
            TokenText.Text = session.Estimate(settings).Description;
        }
        catch (Exception ex) when (IsExpectedError(ex)) { StatusText.Text = "无法预估用量：" + ex.Message; }
    }

    private void Recovery_Click(object sender, RoutedEventArgs e) => OpenTaskRecovery();
    internal void OpenTaskRecovery()
    {
        if (IsWorking || closed) return;
        try
        {
            var dialog = new TaskRecoveryWindow(session.Tasks) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedTaskId is not { } id) return;
            RestoreTask(id);
        }
        catch (Exception ex) when (IsExpectedError(ex)) { StatusText.Text = ex.Message; }
    }

    internal void RestoreTask(string id)
    {
        if (closed || IsWorking) throw new InvalidOperationException("请等待当前操作结束。");
        RefreshConfiguration();
        AiTaskKind kind;
        using (var task=session.Tasks.Open(id)) kind = task.Current.Kind;
        // Release the task lease before the automatic runner opens it again.
        if (kind == AiTaskKind.Maintenance) { _ = StartAutomaticAsync(id); return; }
        outcome = session.Recover(id, settings);
        ShowResult(outcome);
        RefreshUsage();
        RefreshButtons();
    }

#if DEBUG
    // Retain offline lifecycle regression coverage for paid suggestions created by earlier versions.
    // The shipped UI has one automatic entry; legacy results are accessible through task recovery only.
    internal async Task StartSortingAsync()
    {
        if (requestCancellation is not null || closed || ownerLifetime.IsCancellationRequested) return;
        RefreshConfiguration();
        if (!settings.Enabled || session.ReadinessProblem is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime);
        requestCancellation = cancellation;
        outcome = null;
        AcceptedList.ItemsSource = null;
        RejectedList.ItemsSource = null;
        SummaryText.Text = "";
        TokenText.Text = "";
        ResponseText.Text = "";
        var watch = Stopwatch.StartNew();
        TimeSpan? lastContent = null;
        var receiving = true;
        var progress = new Progress<AiResponseUpdate>(update =>
        {
            if (closed || !receiving || cancellation.IsCancellationRequested) return;
            if (update.ContentDelta.Length > 0)
            {
                lastContent = watch.Elapsed;
                var remaining = 256 * 1024 - ResponseText.Text.Length;
                if (remaining > 0) ResponseText.AppendText(update.ContentDelta[..Math.Min(remaining, update.ContentDelta.Length)]);
                ResponseText.ScrollToEnd();
            }
            if (update.Usage is { Valid: true } usage)
                TokenText.Text = $"服务已回报 {usage.Total:N0} token（输入 {usage.Input:N0}，输出 {usage.Output:N0}）";
        });
        using var notice = new AiWaitNotice(WaitNoticeDelay, (elapsed, _) =>
        {
            if (closed || cancellation.IsCancellationRequested) return;
            var quiet = elapsed - (lastContent ?? TimeSpan.Zero);
            StatusText.Text = lastContent is null
                ? $"等待服务响应… 已等待 {elapsed.TotalSeconds:F0} 秒"
                : $"已收到 AI 回报 · 已用时 {elapsed.TotalSeconds:F0} 秒 · 距上次内容 {quiet.TotalSeconds:F0} 秒";
            if (quiet >= WaitNoticeDelay) StatusText.Text += "；可能超时，仍在等待，可取消。";
        });
        RefreshButtons();
        try
        {
            var estimate = session.Estimate(settings);
            TokenText.Text = estimate.Description;
            StatusText.Text = "正在生成排序方案…";
            var response = await session.RequestAsync(settings, estimate, cancellation.Token, progress);
            if (closed || ownerLifetime.IsCancellationRequested) return;
            RefreshConfiguration();
            outcome = response;
            ShowResult(response);
            if (cancellation.IsCancellationRequested)
                InvalidateResult("已取消排序；服务可能已经计费，本次结果不会应用。");
            else if (response.Plan?.CanApply == true && !session.CanApply(response))
                InvalidateResult("请求期间排序或模组资料已变化，请重新开始排序。");
            else if (settings != response.Settings)
                InvalidateResult("请求期间 AI 设置已变化，请重新开始排序。");
            else if (!response.Result.Succeeded) StatusText.Text = response.Result.Message;
            else if (response.Plan?.CanApply == true) StatusText.Text = "排序方案已生成，请预览变化。";
            else StatusText.Text = response.Plan?.Rejected.Count > 0 ? "本次方案未通过校验。" : "无需调整当前排序。";
        }
        catch (OperationCanceledException)
        {
            if (!closed)
            {
                StatusText.Text = "已取消排序；服务可能已经计费。";
                TokenText.Text += "\n本次实际消耗未知。";
            }
        }
        catch (Exception ex) when (IsExpectedError(ex))
        { if (!closed) StatusText.Text = "自动排序未完成：" + ex.Message; }
        finally
        {
            receiving = false;
            requestCancellation = null;
            if (!closed) { RefreshUsage(); RefreshButtons(); }
        }
    }

#endif

    private void ShowResult(AiSortingOutcome response)
    {
        StatusText.Text = response.Result.Message;
        TokenText.Text = response.Estimate.Description + (response.Result.Usage is { } usage
            ? $"\n实际 {usage.Total:N0} token（输入 {usage.Input:N0}，输出 {usage.Output:N0}）"
            : "\n服务未返回有效用量；实际消耗未知。")
            + (response.Notice is null ? "" : "\n" + response.Notice);
        SummaryText.Text = response.Result.Response?.Summary ?? "";
        // Queued progress may arrive after completion. The final public content is authoritative.
        if (response.Result.PublicContent is { } content) ResponseText.Text = content;
        if (ResponseText.Text.Length == 0)
            ResponseText.Text = response.Result.Response?.Summary ?? "未收到可显示的 AI 内容。";
        AcceptedList.ItemsSource = response.Preview.Actions.Select(Describe).ToArray();
        RejectedList.ItemsSource = response.Preview.Rejected.Concat(response.Result.Response?.Warnings ?? []).ToArray();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        requestCancellation?.Cancel();
        StatusText.Text = "正在取消；已发送的请求可能仍会计费。";
        RefreshButtons();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        RefreshConfiguration();
        var selected = outcome;
        if (selected is null || !ApplyButton.IsEnabled) return;
        var plan = selected.Plan!;
        var preview = new OrderPreviewWindow(new OrderComparison(new LoadOrder(plan.Before), new LoadOrder(plan.After)),
            "应用 自动排序", "确认后调整当前排序；点击主界面的保存排序才写入游戏。", "应用排序") { Owner = this };
        if (preview.ShowDialog() != true) return;
        try { ApplyResult(selected); }
        catch (Exception ex) when (IsExpectedError(ex))
        { StatusText.Text = "未能应用排序：" + ex.Message; RefreshButtons(); }
    }

    internal void ApplyResult(AiSortingOutcome selected)
    {
        if (closed || requestCancellation is not null || !ReferenceEquals(outcome, selected))
            throw new InvalidOperationException("排序结果已失效，请重新开始排序。");
        session.Apply(selected);
        InvalidateResult("已应用排序，可在主界面撤销。保存排序后写入游戏。");
        RefreshButtons();
    }

    internal AiSortingOutcome? CurrentOutcome => outcome;
    internal TimeSpan WaitNoticeDelay { get; set; } = AiWaitNotice.DefaultDelay;
    private void RefreshUsage()
    {
        try { TokenText.Text += "\n" + session.UsageDescription(); }
        catch (Exception ex) when (IsExpectedError(ex)) { TokenText.Text += "\n累计用量读取失败，记录已保留。"; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (automaticRunning && requestCancellation is not null && !ownerLifetime.IsCancellationRequested)
        {
            e.Cancel = true;
            requestCancellation.Cancel();
            StatusText.Text = "正在停止测试并清理；完成后可选择保留最后顺序，再关闭窗口。";
            RefreshButtons();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        requestCancellation?.Cancel();
        editor.PropertyChanged -= Editor_Changed;
        base.OnClosed(e);
    }

    private static bool IsExpectedError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
        or JsonException or PlatformNotSupportedException or InvalidOperationException or Win32Exception;
    private static string PermissionLabel(AiPermissionLevel level) => level switch
    {
        AiPermissionLevel.Level1OrderAndRequiredDependencies => "自动排序",
        AiPermissionLevel.Level2ModManagement => "自动排序",
        _ => "模组适配（未完成）"
    };
    private static string Describe(AiAction action) => $"{action.NormalizedKind} · {action.PackageId}"
        + (action.TargetIndex is { } index ? $" → 位置 {index}" : "")
        + (string.IsNullOrWhiteSpace(action.Reason) ? "" : $"：{action.Reason}");
}
