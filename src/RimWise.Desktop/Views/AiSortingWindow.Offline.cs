using System.IO;
using System.Windows;
using RimWise.Integration.Game;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    internal Action<string, bool>? OfflineResultNotice { get; set; }

    internal async Task StartOfflineAsync()
    {
        if (closed || IsWorking || !IsOfflineMode || ownerLifetime.IsCancellationRequested) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime);
        requestCancellation = cancellation;
        outcome = null;
        automaticResultShown = false;
        AcceptedList.ItemsSource = null;
        RejectedList.ItemsSource = null;
        SummaryText.Text = "";
        TokenText.Text = "本次离线排序：0 token（未调用 AI）。";
        ResponseText.Text = "正在读取本地依赖、规则及程序集关系…";
        StatusText.Text = "正在离线排序…";
        ShowDependencyReasons(null);
        ShowStartupFailure(null);
        RefreshButtons();
        var started = DateTimeOffset.UtcNow;
        var message = "";
        var succeeded = false;
        DiagnosticEventLog.Current?.Record("offline-sorting", "started", "single-pass; no AI; no game test");
        try
        {
            if (prepareCatalogue is not null) await prepareCatalogue();
            cancellation.Token.ThrowIfCancellationRequested();
            if (session.ReadinessProblem is { } problem) throw new InvalidOperationException(problem);
            if (editor.ScanIssues.Any(issue => issue.Diagnostic.Code is "ScanLimit" or "ScanRootUnreadable" or "NoScanPaths"))
                throw new InvalidOperationException("模组目录未完整读取，请检查读取记录并刷新后再排序。");
            var catalogue = editor.CurrentCatalogue!;
            var version = editor.EffectiveGameVersion ?? throw new InvalidOperationException("请先确认目标游戏版本。");
            var revision = editor.ContentRevision;
            var original = editor.Rows.Select(row => row.PackageId).ToArray();
            var retained = editor.RetainedPackageIds.ToArray();
            var rules = editor.CustomRules.ToArray();
            var database = editor.CommunityDatabase;
            var result = await Task.Run(() => OfflineSorting.Sort(catalogue, original, retained, version,
                rules, database, Path.Combine(dataDirectory, "Data", "OfflineSorting"), cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closed) return;
            if (revision != editor.ContentRevision || !ReferenceEquals(catalogue, editor.CurrentCatalogue) || editor.IsScanning)
                throw new InvalidOperationException("排序期间清单或模组资料已变化，结果未应用，请重新排序。");
            RejectedList.ItemsSource = result.Messages;
            if (result.Complete && result.Plan is { } plan)
            {
                if (plan.CanApply) editor.ApplyAiPlan(plan, revision, result.Rules);
                succeeded = true;
                message = plan.CanApply ? "离线排序完成，已更新当前清单。" : "离线排序完成，当前顺序无需调整。";
                message += "\n未进行游戏测试，不代表运行时无红字。可在主界面保存排序或撤销调整。";
                if (result.Messages.Count > 0) message += "\n仍有需要留意的事项，请查看右侧警告列表。";
                AcceptedList.ItemsSource = plan.After.Select((id, i) => $"{i + 1}. {catalogue.Resolve(id, version).UniqueInstallation?.Metadata?.Name ?? id} ({id})").ToArray();
                ShowDependencyReasons(plan.Notices);
            }
            else message = "离线排序未完成，当前清单保持不变。\n" + string.Join("\n", result.Messages.Take(5));
            ResponseText.Text = $"已分析 {result.AssemblyCount} 个程序集，缓存命中 {result.CacheHits} 个。\n"
                + "规则来源：模组元数据、玩家规则" + (database is null ? "。" : "、本地社区数据库。")
                + "\n" + message;
        }
        catch (OperationCanceledException)
        { message = "已取消离线排序，当前清单保持不变；未调用 AI。"; if (!closed) ResponseText.Text = message; }
        catch (Exception ex) when (IsExpectedError(ex))
        { message = "离线排序未完成：" + ex.Message; if (!closed) ResponseText.Text = message; }
        finally
        {
            DiagnosticEventLog.Current?.Record("offline-sorting", succeeded ? "completed" : "not-applied", message);
            try
            {
                // This run owns no AI task or game session, even if another window uses AI.
                lastLogRun = new(started, DateTimeOffset.UtcNow, [], []);
                logStore.SaveRun(lastLogRun);
                if (!closed && AutoSaveLogsToggle.IsChecked == true) await SaveLastLogsAsync(openFolder: false);
            }
            catch (Exception ex) when (IsExpectedError(ex))
            { if (!closed) LogStatusText.Text = "日志索引未保存：" + ex.Message; }
            requestCancellation = null;
            if (!closed)
            {
                StatusText.Text = message;
                automaticResultShown = true;
                automaticResultRevision = editor.ContentRevision;
                RefreshButtons();
            }
        }
        if (closed || ownerLifetime.IsCancellationRequested || cancellation.IsCancellationRequested) return;
        if (OfflineResultNotice is { } notice) notice(message, succeeded);
        else MessageDialog.Show(this, message, succeeded ? "离线排序完成" : "离线排序未完成",
            MessageBoxButton.OK, succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
