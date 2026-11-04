using System.IO;
using System.Windows;
using RimWise.Desktop.Presentation;
using RimWise.Integration.AI;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Game;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private async Task<AutomaticMaintenanceResult> RunWithCopyFallbackAsync(MaintenanceLimits limits,
        IProgress<MaintenanceProgress> progress, IProgress<OrdinaryGameProgress> gameProgress,
        IProgress<AiResponseUpdate> responseProgress, CancellationToken token, string? resumeId)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attempts = 0; var tokens = 0; var requests = 0; var unknown = false;
        while (true)
        {
            var result = await automatic.RunAsync(gamePaths!, settings, limits, progress, gameProgress,
                responseProgress, token, resumeId, ChooseConflictAsync, prepareSave, ConfirmTrialsAsync);
            var current = result.Maintenance;
            var combined = result with { Maintenance = current with
            {
                Attempts = checked(attempts + current.Attempts), Requests = checked(requests + current.Requests),
                ReportedTokens = checked(tokens + current.ReportedTokens), HasUnknownUsage = unknown || current.HasUnknownUsage
            } };
            if (current.Outcome is not (MaintenanceOutcome.Failed or MaintenanceOutcome.Stopped)
                || current.Attempts == 0 || !current.OriginalRestored || token.IsCancellationRequested) return combined;

            // A different installation requires a new recovery context. Carry the remaining budget into
            // that new task; never replay a paid response or reset a limit after switching files.
            var reserve = 0;
            MaintenanceLimits? remaining;
            if (current.TaskId is { } taskId)
            {
                using var task = session.Tasks.Open(taskId);
                remaining = DuplicateCopyRetry.Remaining(limits, task.Current);
                reserve = task.Current.ReservedTokens;
            }
            else return combined;
            if (remaining is null) return combined;
            try { if (!await TrySwitchFailedCopyAsync(result, tried, token)) return combined; }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return combined with { Maintenance = combined.Maintenance with { Outcome = MaintenanceOutcome.Cancelled } }; }

            attempts = combined.Maintenance.Attempts; requests = combined.Maintenance.Requests;
            tokens = combined.Maintenance.ReportedTokens; unknown = combined.Maintenance.HasUnknownUsage;
            limits = remaining;
            DiagnosticEventLog.Current?.Record("duplicate-copies", "continue-with-selected-copy",
                $"previousTask={current.TaskId}; attempts={attempts}; reportedTokens={tokens}; reservedTokens={reserve}");
            resumeId = null;
        }
    }

    private async Task<bool> TrySwitchFailedCopyAsync(AutomaticMaintenanceResult result, HashSet<string> tried, CancellationToken token)
    {
        if (gamePaths is null || closed || GameProcessService.IsAnyRunning()) return false;
        var paths = gamePaths();
        var revision = editor.ContentRevision;
        var store = new DuplicateCopyBackups(dataDirectory, paths);
        var used = editor.CurrentCatalogue?.Installations.Where(m => result.PreparedOrder.Concat(result.Maintenance.LastAttemptedOrder)
            .Any(id => editor.CurrentCatalogue.Resolve(id, editor.CatalogueVersion).UniqueInstallation?.Directory == m.Directory))
            .Select(m => m.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var backup = store.List().FirstOrDefault(record => used.Contains(record.Selected)
            && record.Moves.Any(m => !tried.Contains(m.Original) && Directory.Exists(m.Backup)));
        if (backup is null) return false;
        tried.Add(backup.Selected);
        var detail = "本轮测试尚未通过。所用模组还有已备份的其他副本，可以恢复后改选并继续测试。"
            + "这只是对照测试，尚不能确定该副本是报错原因。\n\n当前副本：\n" + backup.Selected
            + "\n\n可恢复的副本：\n" + string.Join("\n", backup.Moves.Select(m => m.Original))
            + "\n\n恢复会改变游戏扫描到的安装；随后会再次让你选择保留哪份。取消选择时将保留恢复后的副本并停止测试。旧任务保留用量、不再恢复。是否恢复并重新选择？";
        if (MessageDialog.Show(this, detail, "是否换用其他副本测试", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        token.ThrowIfCancellationRequested();
        if (paths != gamePaths() || revision != editor.ContentRevision) throw new IOException("路径或排序已变化，请重新开始。");
        using (var oldTask = session.Tasks.Open(result.Maintenance.TaskId!)) DuplicateCopyRetry.Retire(oldTask);
        await Task.Run(() => store.Restore(backup.Id, token), token);
        await RefreshDuplicateInstallationsAsync(token);
        if (!ResolveDuplicateInstallations(token))
        {
            MessageDialog.Show(this, "已恢复副本；尚未选择新的副本，测试已停止。当前排序保留，下次开始时会再次提示选择。",
                "副本选择已取消", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var selected = store.List().FirstOrDefault(record => record.Id != backup.Id
            && record.Moves.Any(m => m.Original.Equals(backup.Selected, StringComparison.OrdinalIgnoreCase)))?.Selected;
        if (selected is null || !tried.Add(selected))
        {
            DiagnosticEventLog.Current?.Record("duplicate-copies", "repeat-copy-not-retested", selected);
            return false;
        }
        return true;
    }
}
