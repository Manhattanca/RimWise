using System.Windows;
using System.IO;
using System.Diagnostics;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private readonly AutomaticSortingLogStore logStore;
    private bool savingLogs;
    private AutomaticLogRun? lastLogRun;
    internal Action<string> OpenLogFolder { get; set; } = directory =>
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });

    private void LoadLogSettings()
    {
        try
        {
            var value = logStore.LoadSettings();
            AutoSaveLogsToggle.IsChecked = value.AutoSave;
            lastLogRun = logStore.LoadLastRun();
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            AutoSaveLogsToggle.IsChecked = false;
            LogStatusText.Text = "日志设置读取失败：" + ex.Message;
        }
    }

    private void AutoSaveLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            logStore.SaveSettings(logStore.LoadSettings() with { AutoSave = AutoSaveLogsToggle.IsChecked == true });
            LogStatusText.Text = AutoSaveLogsToggle.IsChecked == true ? "任务成功、失败或取消后自动保存完整排障日志 ZIP。" : "已关闭自动保存；成功、失败或取消均可导出完整排障日志。";
        }
        catch (Exception ex) when (IsExpectedError(ex)) { LoadLogSettings(); LogStatusText.Text = "设置未保存：" + ex.Message; }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking) return;
        try
        {
            var directory = logStore.ValidateDirectory(logStore.LoadSettings().Directory);
            Directory.CreateDirectory(directory);
            OpenLogFolder(directory);
        }
        catch (Exception ex) when (IsExpectedError(ex)) { LogStatusText.Text = "日志文件夹未能打开：" + ex.Message; }
    }

    private async void SaveLastLogs_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking || closed) return;
        await SaveLastLogsAsync(openFolder: true);
    }

    internal async Task SaveLastLogsAsync(bool openFolder)
    {
        if (savingLogs) return;
        savingLogs = true;
        if (!closed) { RefreshButtons(); LogStatusText.Text = "正在压缩并保存日志…"; }
        string? savedPath = null;
        try
        {
            var snapshot = editor.CaptureDiagnosticSnapshot();
            var result = await Services.CompleteDiagnosticExport.ExportAsync(dataDirectory, snapshot);
            savedPath = result.Path;
            if (!closed) LogStatusText.Text = $"已导出完整排障日志：{result.Path}"
                + (result.OmissionCount == 0 ? "" : "\n" + result.IncompleteNotice)
                + (result.RecoveredRecords == 0 ? "" : "\n" + result.RecoveryNotice);
            DiagnosticEventLog.Current?.Record("automatic-sorting", "logs-saved", $"files={result.Files}; bytes={result.Bytes}; omissions={result.OmissionCount}; recovered={result.RecoveredRecords}");
            if (!closed && result.OmissionCount > 0)
                MessageDialog.Show(DialogOwner, result.IncompleteNotice + "\n\n已保存：" + result.Path,
                    "诊断包未完整导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (openFolder && !closed)
                OpenLogFolder(Path.GetDirectoryName(result.Path)!);
        }
        catch (Exception ex) when (IsExpectedError(ex) || ex is System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            if (!closed) LogStatusText.Text = savedPath is null ? "日志保存失败，排序结果不受影响：" + ex.Message
                : "日志已保存，但文件夹未能打开：" + savedPath;
            DiagnosticEventLog.Current?.Record("automatic-sorting", "logs-save-failed", ex.GetType().Name);
        }
        finally { savingLogs = false; if (!closed) RefreshButtons(); }
    }

    private async Task FinishAutomaticLogsAsync(DateTimeOffset started, string? resumeId)
    {
        try
        {
            lastLogRun = await Task.Run(() =>
            {
                var run = logStore.Capture(started, DateTimeOffset.UtcNow, resumeId);
                logStore.SaveRun(run);
                return run;
            });
            if (AutoSaveLogsToggle.IsChecked == true) await SaveLastLogsAsync(openFolder: false);
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            if (!closed) LogStatusText.Text = "本次排障日志索引保存失败，排序结果不受影响：" + ex.Message;
            DiagnosticEventLog.Current?.Record("automatic-sorting", "log-index-failed", ex.GetType().Name);
        }
    }

    private async void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        if (closed || IsWorking) return;
        clearingLogs = true;
        RefreshButtons();
        StatusText.Text = "正在清除历史日志…";
        try
        {
            var result = await Task.Run(() => TestDataRetention.MaintainProfile(dataDirectory, clearLogs: true));
            if (closed) return;
            StatusText.Text = result.Busy ? "游戏或测试正在运行，请结束后再清除日志。"
                : $"已清理 {(result.BeforeBytes - result.AfterBytes) / 1_000_000d:F1} MB；保留上次排序日志、待恢复任务和 token 记录。"
                    + (result.WithinLimit ? "" : "剩余数据包含上次日志、恢复记录或其他文件，不会仅因超出日志目标阻止排序。");
            DiagnosticEventLog.Current?.Record("test-storage", "clear-logs", StatusText.Text);
        }
        catch (Exception ex) when (IsExpectedError(ex))
        {
            if (!closed) StatusText.Text = "部分日志未能清除：" + ex.Message;
            DiagnosticEventLog.Current?.Record("test-storage", "clear-failed", ex.Message);
        }
        finally { clearingLogs = false; if (!closed) RefreshButtons(); }
    }

    private async void DeleteAllLogs_Click(object sender, RoutedEventArgs e)
    {
        if (closed || IsWorking) return;
        try
        {
            var directory = logStore.ValidateDirectory(logStore.LoadSettings().Directory);
            if (MessageDialog.Show(DialogOwner, "删除全部可清理的排序日志（含上次日志），以及以下目录中的 RimWise 完整排障日志 ZIP 和旧排序日志 ZIP？\n" + directory
                + "\n待恢复任务、正在使用的记录、token 用量、设置和其他文件会保留。删除后无法撤销。",
                "删除全部日志", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            await DeleteAllLogsAsync(directory);
        }
        catch (Exception ex) when (IsExpectedError(ex)) { LogStatusText.Text = "日志未删除：" + ex.Message; }
    }

    internal async Task DeleteAllLogsAsync(string? selectedDirectory = null)
    {
        if (closed || IsWorking) return;
        clearingLogs = true;
        RefreshButtons();
        LogStatusText.Text = "正在删除日志…";
        try
        {
            var directory = logStore.ValidateDirectory(selectedDirectory ?? logStore.LoadSettings().Directory);
            var result = await Task.Run(() => TestDataRetention.MaintainProfile(dataDirectory, clearLogs: true, preserveLastRun: false));
            if (result.Busy) { LogStatusText.Text = "游戏或测试正在运行，请结束后再删除日志。"; return; }
            var exports = await Task.Run(() => logStore.DeleteExportedLogs(directory));
            LogStatusText.Text = $"已删除 {result.RemovedFiles} 个日志／缓存文件、{exports.Removed} 个排障日志 ZIP。"
                + "保留待恢复任务、正在使用的记录与 token 用量。"
                + (exports.Skipped > 0 ? $"{exports.Skipped} 个文件因占用或无法验证而保留。" : "");
        }
        catch (Exception ex) when (IsExpectedError(ex)) { LogStatusText.Text = "部分日志未能删除：" + ex.Message; }
        finally { clearingLogs = false; if (!closed) RefreshButtons(); }
    }
}
