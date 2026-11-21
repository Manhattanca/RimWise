using System.IO;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Game;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private async Task<AutomaticMaintenanceResult> RunWithCopyFallbackAsync(MaintenanceLimits limits,
        IProgress<MaintenanceProgress> progress, IProgress<OrdinaryGameProgress> gameProgress,
        IProgress<AiResponseUpdate> responseProgress, CancellationToken token, string? resumeId, string? checkedModelScope)
    {
        var attempts = 0; var controls = 0; var tokens = 0; var requests = 0; var unknown = false;
        while (true)
        {
            var result = await automatic.RunAsync(gamePaths!, settings, limits, progress, gameProgress,
                responseProgress, token, resumeId, ChooseConflictAsync, prepareSave is null ? null : PrepareInstallationAwareSave,
                ConfirmTrialsAsync, checkedModelScope,
                automaticCopies?.FallbackPackages);
            var current = result.Maintenance;
            var combined = result with { Maintenance = current with
            {
                Attempts = checked(attempts + current.Attempts), ControlAttempts = checked(controls + current.ControlAttempts),
                Requests = checked(requests + current.Requests), ReportedTokens = checked(tokens + current.ReportedTokens),
                HasUnknownUsage = unknown || current.HasUnknownUsage
            } };
            if (current.Outcome is not (MaintenanceOutcome.Failed or MaintenanceOutcome.Stopped)
                || current.CopyFallbackPackageId is null || current.ReplyFailure != AiReplyFailure.None || current.Attempts == 0
                || !current.OriginalRestored || token.IsCancellationRequested || automaticCopies is null) return combined;

            MaintenanceLimits? remaining;
            if (current.TaskId is { } taskId)
            {
                using var task = session.Tasks.Open(taskId);
                remaining = DuplicateCopyRetry.Remaining(limits, task.Current);
            }
            else return combined;
            if (remaining is null)
            {
                installationNotices.Add("剩余次数或用量不足，未更换或剔除尚未验证的副本；可调整限制后重新开始。");
                return combined;
            }
            token.ThrowIfCancellationRequested();
            if (gamePaths is null || closed || GameProcessService.IsAnyRunning()) return combined;
            var paths = gamePaths();
            var revision = editor.ContentRevision;
            var catalogue = editor.CurrentCatalogue!;
            var order = editor.Rows.Select(r => r.PackageId).ToArray();
            // A different installation must have a new recovery context. Retire the paid task
            // before any file movement, preserving its evidence and never resending its response.
            using (var oldTask = session.Tasks.Open(current.TaskId!)) DuplicateCopyRetry.Retire(oldTask);
            try
            {
                var prepared = await Task.Run(() => automaticCopies.Retry(catalogue, order, current.CopyFallbackPackageId, token), token);
                if (closed || paths != gamePaths() || revision != editor.ContentRevision || !ReferenceEquals(catalogue, editor.CurrentCatalogue))
                    throw new IOException("路径或排序已变化，备份记录保留，未应用副本重试结果。");
                await RefreshDuplicateInstallationsAsync(token);
                editor.ApplyInstallationPreparation(prepared, editor.ContentRevision);
                installationExpectedRevision = editor.ContentRevision;
                await PrepareSortingInstallationsAsync(token);
                remaining = remaining with { InsertionBaselineJson = RemapInsertionBaseline(remaining.InsertionBaselineJson, catalogue) };
            }
            finally { RecordInstallationNotices(); }
            attempts = combined.Maintenance.Attempts; controls = combined.Maintenance.ControlAttempts;
            requests = combined.Maintenance.Requests; tokens = combined.Maintenance.ReportedTokens;
            unknown = combined.Maintenance.HasUnknownUsage;
            limits = remaining;
            DiagnosticEventLog.Current?.Record("duplicate-copies", "automatic-fallback",
                $"previousTask={current.TaskId}; attempts={attempts}; controls={controls}; reportedTokens={tokens}");
            resumeId = null;
        }
    }

    internal Action PrepareInstallationAwareSave()
    {
        var save = prepareSave!();
        return () =>
        {
            save();
            // Saving the game configuration is the commit point, not a later bookkeeping
            // write. A checkpoint failure after a successful save must not undo its files.
            installationSelectionCommitted = true;
        };
    }

    private string? RemapInsertionBaseline(string? json, ModCatalogue previous)
    {
        if (json is null) return null;
        var version = editor.EffectiveGameVersion!;
        var active = editor.Rows.Select(row => row.PackageId).ToArray();
        var mapped = InsertionScope.Read(json).Select(id =>
        {
            var package = previous.Resolve(id, version).UniqueInstallation?.PackageId ?? id;
            return active.FirstOrDefault(candidate => candidate.Equals(id, StringComparison.OrdinalIgnoreCase)
                || editor.CurrentCatalogue!.Resolve(candidate, version).UniqueInstallation?.PackageId.Equals(package, StringComparison.OrdinalIgnoreCase) == true);
        }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        return InsertionScope.Capture(mapped);
    }
}
