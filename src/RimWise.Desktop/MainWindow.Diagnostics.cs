using System.Windows;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Views;
using Microsoft.Win32;
using RimWise.Integration.Diagnostics;
using System.IO;
using System.Text.Json;
using System.ComponentModel;
using System.Windows.Controls;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private OrderIssuesWindow? issuesWindow;
    private void Warnings_Click(object sender, RoutedEventArgs e) => OpenIssues(DiagnosticSeverity.Warning);
    private void Errors_Click(object sender, RoutedEventArgs e) => OpenIssues(DiagnosticSeverity.Error);

    private bool exportingDiagnostics;
    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (exportingDiagnostics) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断日志",
            Filter = "RimWise 诊断包 (*.zip)|*.zip",
            FileName = "RimWise-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
            AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        exportingDiagnostics = true;
        if (sender is MenuItem menu) menu.IsEnabled = false;
        try
        {
            DiagnosticEventLog.Current?.Record("ui", "diagnostic-export-start");
            var result = await ExportDiagnosticsAsync(dialog.FileName);
            DiagnosticEventLog.Current?.Record("ui", "diagnostic-export-complete", $"files={result.Files};bytes={result.Bytes}");
            ViewModel.Status = $"已导出诊断日志：{Path.GetFileName(dialog.FileName)}；已回报 {result.ReportedTokens:N0} token"
                + (result.UnknownTasks > 0 ? $"，另有 {result.UnknownTasks} 项用量未回报" : "");
            MessageDialog.Show(this, "诊断包已导出。\n\n其中不包含 AI 密钥、加密密钥文件或原始 AI 设置。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or Win32Exception or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            DiagnosticEventLog.Current?.Record("ui", "diagnostic-export-failed", ex.GetType().Name);
            ViewModel.Status = "诊断日志导出失败，未生成新的诊断包。";
            MessageDialog.Show(this, "导出失败。请检查保存位置、用户数据和本地密钥是否可读。\n\n错误类型：" + ex.GetType().Name, "导出诊断日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { exportingDiagnostics = false; if (sender is MenuItem item) item.IsEnabled = true; }
    }

    internal async Task<DiagnosticExportResult> ExportDiagnosticsAsync(string destination)
    {
        var directory = UserStorage.DirectoryPath;
        var snapshot = new { ViewModel.Status, ViewModel.HasChanges, ViewModel.IsScanning,
            ViewModel.GameVersionLabel, configurationVersion = ViewModel.VersionLabel,
            effectiveVersion = ViewModel.EffectiveGameVersion?.ToString(), targetVersionOverride = paths.TargetVersionOverride,
            installedVersion = ViewModel.CurrentDetectedVersion?.ToString(),
            ViewModel.WarningCount, ViewModel.ErrorCount,
            diagnostics = ViewModel.Diagnostics.Select(row => row.Diagnostic).ToArray(),
            order = ViewModel.Rows.Select(r => new { r.PackageId, r.Name }).ToArray() };
        ViewModel.Status = "正在导出诊断日志…";
        if (DiagnosticEventLog.Current is { } log) await log.FlushAsync();
        return await Task.Run(() => DiagnosticExportService.Export(directory, destination, snapshot));
    }

    internal OrderIssuesWindow OpenIssues(DiagnosticSeverity severity)
    {
        if (issuesWindow is null)
        {
            issuesWindow = new OrderIssuesWindow(ViewModel, LocateDiagnostic) { Owner = this };
            issuesWindow.Closed += (_, _) => issuesWindow = null;
        }
        issuesWindow.ShowSeverity(severity);
        issuesWindow.Show();
        issuesWindow.Activate();
        return issuesWindow;
    }

    private void LocateDiagnostic(DiagnosticRow issue)
    {
        var identifiers = issue.Diagnostic.AffectedIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matching = ViewModel.Rows.Where(row => identifiers.Contains(row.PackageId)).ToArray();
        if (matching.Length == 0) return;
        // Re-resolve from current rows; never hold an index across imports, moves or rescans.
        var next = (Array.IndexOf(matching, ViewModel.SelectedRow) + 1) % matching.Length;
        ViewModel.SearchText = string.Empty;
        ViewModel.SelectedRow = matching[next];
        OrderGrid.ScrollIntoView(matching[next]);
        OrderGrid.Focus();
    }
}
