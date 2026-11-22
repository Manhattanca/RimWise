using System.IO;
using RimWise.Core;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Scanning;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private AutomaticCopySelection? automaticCopies;
    private readonly List<string> installationNotices = [];
    private string[]? installationOriginalOrder;
    private long installationExpectedRevision;
    private bool installationSelectionCommitted;
    private GamePaths? installationPaths;

    internal async Task PrepareSortingInstallationsAsync(CancellationToken token, bool reset = false)
    {
        if (gamePaths is null) throw new InvalidOperationException("游戏路径尚未配置。");
        if (session.ReadinessProblem is { } readiness) throw new InvalidOperationException(readiness);
        await RefreshDuplicateInstallationsAsync(token);
        if (editor.ScanIssues.Any(i => i.Diagnostic.Code is "ScanRootUnreadable" or "ScanLimit" or "NoScanPaths"))
            throw new IOException("模组扫描未完成，不能将未读取到的模组当作未安装。请检查路径后刷新。");
        RecordMetadataReadNotices();
        var paths = gamePaths();
        var version = editor.EffectiveGameVersion ?? throw new InvalidOperationException("请先确认游戏版本。");
        if (!reset && automaticCopies is not null && paths != installationPaths)
            throw new InvalidOperationException("游戏路径在模组处理期间发生变化，未继续切换副本。");
        if (reset || automaticCopies is null)
        {
            automaticCopies = new(dataDirectory, paths, version);
            installationPaths = paths;
            installationOriginalOrder = editor.Rows.Select(r => r.PackageId).ToArray();
            installationExpectedRevision = editor.ContentRevision;
        }
        var copies = automaticCopies;
        var revision = editor.ContentRevision;
        var catalogue = editor.CurrentCatalogue!;
        var order = editor.Rows.Select(row => row.PackageId).ToArray();
        StatusText.Text = "正在检查缺失模组并自动选择可用的优先副本…";
        SortingInstallationOrder prepared;
        try { prepared = await Task.Run(() => copies.Prepare(catalogue, order, token), token); }
        finally { RecordInstallationNotices(); }
        token.ThrowIfCancellationRequested();
        if (closed || paths != gamePaths() || revision != editor.ContentRevision || !ReferenceEquals(catalogue, editor.CurrentCatalogue))
            throw new InvalidOperationException("路径或排序已变化，副本备份保留，未应用清理结果。");
        // Reflect the actual on-disk selection, not a fictional catalogue-only preference.
        await RefreshDuplicateInstallationsAsync(token);
        editor.ApplyInstallationPreparation(prepared, editor.ContentRevision);
        installationExpectedRevision = editor.ContentRevision;
    }

    internal async Task RollbackUncommittedInstallationsAsync()
    {
        if (installationSelectionCommitted || automaticCopies is null) return;
        try
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Task.Run(() => automaticCopies.Rollback(recovery.Token));
            if (closed || ownerLifetime.IsCancellationRequested) return;
            if (gamePaths?.Invoke() != installationPaths)
            {
                installationNotices.Add("游戏路径已改变，仅恢复原路径内的本轮副本操作，未覆盖当前清单。");
                return;
            }
            var unchanged = editor.ContentRevision == installationExpectedRevision;
            await RefreshDuplicateInstallationsAsync(recovery.Token);
            if (unchanged && installationOriginalOrder is { Length: > 0 } original)
            {
                editor.ApplyInstallationPreparation(new(original, [], []), editor.ContentRevision);
                installationNotices.Add("本次没有保存排序，已还原开始前的清单；上方剔除与副本选择是本轮尝试记录。");
            }
            else installationNotices.Add(installationOriginalOrder is null
                ? "未覆盖恢复任务的原清单；副本目录已保持恢复任务开始前的状态。"
                : "清单已有其他编辑，未覆盖这些编辑；请根据已恢复的安装状态核对模组来源。");
        }
        catch (Exception ex) when (IsExpectedError(ex) || ex is OperationCanceledException)
        {
            installationNotices.Add("副本恢复尚未完成，请在“副本备份”处理；未覆盖或删除文件：" + ex.Message);
            DiagnosticEventLog.Current?.Record("sorting-installations", "rollback-incomplete", ex.ToString());
        }
    }

    private void RecordInstallationNotices()
    {
        var known = installationNotices.ToHashSet(StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var message in automaticCopies?.Messages ?? [])
            if (known.Add(message))
            {
                installationNotices.Add(message);
                added.Add(message);
                DiagnosticEventLog.Current?.Record("sorting-installations", "decision", message);
            }
        // Batch live-text updates; thousands of missing rows must not force thousands of layouts.
        if (!closed && added.Count > 0) AppendAutomaticReport("\n" + string.Join("\n", added) + "\n");
    }

    private void RecordMetadataReadNotices()
    {
        var added = new List<string>();
        foreach (var row in editor.ScanIssues.Where(i => i.Diagnostic.Code == "MetadataUnreadable"))
        {
            var issue = row.Diagnostic;
            var notice = "模组读取异常（不阻断自动排序）：" + issue.Message
                + "\n位置：" + issue.Location
                + "\n请展开主界面模组列表下方的“读取记录”，检查该模组文件、下载状态及访问权限。"
                + "当前排序中的异常项仍保留；进行游戏测试时，仅本轮报错关联到该项才定向排除，安装文件保留。";
            if (installationNotices.Contains(notice, StringComparer.Ordinal)) continue;
            installationNotices.Add(notice);
            added.Add(notice);
            DiagnosticEventLog.Current?.Record("sorting-installations", "metadata-read-warning", notice);
        }
        if (added.Count == 0) return;
        MetadataReadWarning.Visibility = System.Windows.Visibility.Visible;
        MetadataReadWarningText.Text = $"检测到 {editor.ScanIssues.Count(i => i.Diagnostic.Code == "MetadataUnreadable")} 项模组读取异常。"
            + (IsOfflineMode ? "离线排序继续并保留异常项；本模式不进行游戏测试，也不会据此剔除模组。"
                : "自动排序继续；异常项先参加游戏测试，仅在本轮红字有关联时定向排除。")
            + "请检查对应模组，详细原因已记录在下方回报中。";
        AppendAutomaticReport("\n" + string.Join("\n\n", added) + "\n");
        MetadataReadNotice?.Invoke(added);
    }

    // Notification only: it never asks for consent to continue or creates removal authority.
    internal Action<IReadOnlyList<string>>? MetadataReadNotice { get; set; }
    internal Action? OpenReadIssues { get; set; }
    private void ReadIssues_Click(object sender, System.Windows.RoutedEventArgs e) => OpenReadIssues?.Invoke();

    private string InstallationSummary => installationNotices.Count == 0 ? "" : "\n\n模组处理汇总：\n"
        + string.Join("\n", installationNotices.Take(80))
        + (installationNotices.Count > 80 ? $"\n其余 {installationNotices.Count - 80} 条见完整排障日志。" : "");
}
