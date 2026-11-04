#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Views;
using RimWise.Integration.Scanning;
using RimWise.Integration.Configuration;
using RimWise.Integration.Steam;

namespace RimWise.Desktop;

internal static class MissingModDownloadSmokeCheck
{
    internal static async Task RunAsync(MainWindow owner, string output, List<string> checks)
    {
        var plan = new ImportDownloadPlan([new("first.mod", "123"), new("old.mod", "456") { Notice = "数据库未声明支持当前游戏版本" }], [])
        { Issues = [new("unknown.mod", "未找到可确认的工坊来源"), new("ludeon.rimworld.dlc", "DLC 不能从工坊订阅", false)] };
        var lookupEntered = new TaskCompletionSource();
        var lookupRelease = new TaskCompletionSource<ImportDownloadPlan>();
        var subscriptions = 0;
        var searching = new MissingModsWindow(["first.mod", "unknown.mod"], async (progress, token) =>
        {
            progress.Report("缺少模组，正在检索工坊来源…");
            lookupEntered.SetResult();
            return await lookupRelease.Task.WaitAsync(token);
        }, (_, _, _) => { subscriptions++; return Task.CompletedTask; }, default) { Owner = owner };
        searching.Show(); await lookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Idle(searching);
        Assert(searching.IsVisible && searching.SummaryText.Text.Contains("缺少 2")
            && searching.ProgressText.Text.Contains("正在检索") && searching.Rows.Count == 2,
            "Missing window did not show before delayed source lookup completed");
        Assert(!searching.DownloadAllButton.IsEnabled && !searching.DownloadSelectedButton.IsEnabled
            && searching.Rows.All(row => !row.CanDownload), "Source lookup enabled premature subscriptions");
        Click(searching.DownloadAllButton);
        Assert(subscriptions == 0, "Loading dialog accepted a download click");
        PrototypeSmokeCheck.SavePreview(searching, Path.Combine(output, "缺失模组-检索中.png"));
        lookupRelease.SetResult(plan); await searching.PendingResolution; await Idle(searching);
        Assert(searching.IsVisible && searching.Rows.Count == 4 && searching.DownloadAllButton.IsEnabled
            && searching.ProgressText.Text.Contains("检索完成"), "Source results did not update the same visible window");
        searching.Close();
        checks.Add("Missing-Mod window appears during delayed source lookup; progress and disabled downloads update in the same window");

        var stoppedLookup = new TaskCompletionSource();
        var lateLookup = new TaskCompletionSource<ImportDownloadPlan>();
        var searchCancelled = false;
        var cancelledSearch = new MissingModsWindow(["first.mod"], async (_, token) =>
        {
            using var registration = token.Register(() => searchCancelled = true);
            stoppedLookup.SetResult();
            // Simulate a read-only lookup that returns late despite cancellation.
            return await lateLookup.Task;
        }, (_, _, _) => { subscriptions++; return Task.CompletedTask; }, default) { Owner = owner };
        cancelledSearch.Show(); await stoppedLookup.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancelledSearch.Close(); lateLookup.SetResult(plan); await cancelledSearch.PendingResolution;
        Assert(searchCancelled && !cancelledSearch.IsVisible && cancelledSearch.Rows.Count == 1 && subscriptions == 0,
            "Closing source lookup accepted a late result or sent a subscription");
        checks.Add("Closing source lookup cancels work and rejects late results without subscription or another popup");

        var failedSearch = new MissingModsWindow(["first.mod"], (_, _) => throw new IOException("合成检索失败"),
            (_, _, _) => Task.CompletedTask, default) { Owner = owner };
        failedSearch.Show(); await Idle(failedSearch); await failedSearch.PendingResolution;
        Assert(failedSearch.IsVisible && failedSearch.ResolutionFailure == "合成检索失败"
            && failedSearch.ProgressText.Text.Contains("排序已保留") && !failedSearch.DownloadAllButton.IsEnabled,
            "Source lookup failure was hidden or enabled downloads");
        failedSearch.Close();
        var noMissing = new MissingModsWindow([], (_, _) => Task.FromResult(new ImportDownloadPlan([], [])),
            (_, _, _) => Task.CompletedTask, default) { Owner = owner };
        noMissing.Show(); await Idle(noMissing); await noMissing.PendingResolution;
        Assert(!noMissing.IsVisible, "Completed catalogue verification left an empty missing-Mod dialog open");
        checks.Add("Lookup failure stays visible with preserved-order notice; no-missing verification closes the empty window");

        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            var sent = new List<string>();
            var dialog = new MissingModsWindow(plan, (selected, _, _) =>
            {
                sent.Add(selected.Downloads.Single().WorkshopId);
                if (sent[^1] == "456") throw new IOException("合成下载失败，可重试");
                return Task.CompletedTask;
            }, default) { Owner = owner, Width = 760, Height = 400 };
            dialog.Show(); await Idle(dialog);
            Assert(dialog.MissingGrid.ActualHeight > 90 && dialog.Rows.Count == 4, "Missing list clipped or missing unresolved rows");
            Assert(!dialog.Rows[2].CanDownload && !dialog.Rows[3].CanEditSource, "Unknown source or official item can auto subscribe");
            dialog.Rows[1].Selected = false;
            Click(dialog.DownloadSelectedButton); await dialog.PendingDownloads;
            Assert(sent.SequenceEqual(["123"]) && dialog.CompletedCount == 1, "Selected subscription included unselected rows");
            dialog.Rows[2].WorkshopInput = "https://steamcommunity.com/sharedfiles/filedetails/?id=789";
            Click(dialog.DownloadAllButton); await dialog.PendingDownloads; await Idle(dialog);
            Assert(sent.SequenceEqual(["123", "456", "789"]) && dialog.CompletedCount == 2
                && dialog.Rows[1].Status.Contains("未完成") && dialog.Rows[2].Status.Contains("已完成"), "All downloads stopped after individual failure or retried completed row");
            PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, theme + "-缺失模组下载.png"));
            dialog.Close();
        }
        checks.Add("Missing download window day/night minimum layout, selected/all Steam subscriptions, unresolved source entry and per-row failure");

        var entered = new TaskCompletionSource(); var cancelled = false;
        var waiting = new MissingModsWindow(plan, async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        }, default) { Owner = owner };
        waiting.Show(); Click(waiting.DownloadSelectedButton); await entered.Task;
        waiting.Close(); await waiting.PendingDownloads;
        Assert(cancelled, "Closing download window left background requests running");
        checks.Add("Closing missing-Mod dialog cancels waiting and sends no subsequent subscription");

        var batchPlan = new ImportDownloadPlan([new("first.mod", "123"), new("timeout.mod", "456"),
            new("last.mod", "789"), new("refused.mod", "101")], [])
        { Issues = [new("unknown.mod", "未找到来源")] };
        var enqueued = new List<string>(); var verified = new List<string>();
        var firstWait = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var batch = new MissingModsWindow(batchPlan, async (selected, _, token) =>
        {
            var id = selected.Downloads.Single().WorkshopId;
            verified.Add(id);
            if (id == "123") { firstWait.SetResult(); await release.Task.WaitAsync(token); }
            if (id == "456") throw new TaskCanceledException("synthetic individual timeout");
        }, default, (selected, _, _) =>
        {
            var id = selected.Downloads.Single().WorkshopId; enqueued.Add(id);
            if (id == "101") throw new IOException("synthetic subscription refusal");
            return Task.CompletedTask;
        }) { Owner = owner };
        batch.Show(); Click(batch.DownloadAllButton);
        await firstWait.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(enqueued.SequenceEqual(["123", "456", "789", "101"]) && batch.SubscribedCount == 3
            && verified.SequenceEqual(["123"]) && batch.IsVisible, "First installation blocked later subscriptions");
        var initialStatus = batch.Rows[0].Status;
        var heartbeatDeadline = System.Diagnostics.Stopwatch.StartNew();
        while (batch.Rows[0].Status == initialStatus && heartbeatDeadline.Elapsed < TimeSpan.FromSeconds(5))
        { await Task.Delay(100); await Idle(batch); }
        Assert(batch.Rows[0].Status != initialStatus && batch.Rows[0].Status.Contains("本步已等待"),
            "Waiting row has no live heartbeat: " + batch.Rows[0].Status);
        release.SetResult(); await batch.PendingDownloads;
        Assert(verified.SequenceEqual(["123", "456", "789"]) && batch.CompletedCount == 2
            && batch.Rows[1].Status.Contains("超时") && batch.ProgressText.Text.Contains("本次失败 2 项")
            && batch.ProgressText.Text.Contains("1 项需填写"), "Individual timeout/failure stopped batch or summary hid unknown source");
        batch.Close();
        checks.Add("All Steam subscriptions precede installation waits; heartbeat, per-item timeout, refusal and unknown source remain visible");

        entered = new(); enqueued.Clear();
        var stopped = new MissingModsWindow(batchPlan, (_, _, _) => throw new InvalidOperationException("Wait must not start"), default,
            async (selected, _, token) =>
            {
                enqueued.Add(selected.Downloads.Single().WorkshopId); entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }) { Owner = owner };
        stopped.Show(); Click(stopped.DownloadAllButton); await entered.Task;
        stopped.Close(); await stopped.PendingDownloads;
        Assert(enqueued.SequenceEqual(["123"]), "Closing during enqueue sent further subscriptions");
        checks.Add("Closing during submission cancels batch before further subscription requests");

        // The context-menu executor must not import a new document or consume undo history.
        var vm = owner.ViewModel;
        var original = vm.Rows.Select(r => r.PackageId).ToArray();
        var revision = vm.DocumentRevision;
        var root = Path.Combine(output, "右键 下载 [中文] &");
        Directory.CreateDirectory(root);
        var installed = original.Select((id, index) => new ModInstallation(Path.Combine(root, id), id, ModSource.Workshop,
            index == 0 ? "123" : "456", new(id, "fixture", id, ["1.7"], "", ModRequirements.Empty, false), [])).ToArray();
        foreach (var mod in installed) Directory.CreateDirectory(mod.Directory);
        var scan = 0;
        await owner.DownloadMissingModsAsync([original[1]], (selected, _, _) =>
        {
            Assert(selected.Downloads.Count == 1 && selected.Downloads[0].Identifier == original[1], "Context download ignored selected missing IDs");
            return Task.FromResult<IReadOnlyList<WorkshopItemState>>([new("456", true, true, installed[1].Directory)]);
        }, _ => Task.FromResult(new ModScanResult(new(null, "", ""), new(1, 7), new(++scan == 1 ? [installed[0]] : installed), [])));
        Assert(vm.DocumentRevision == revision && vm.Rows.Select(r => r.PackageId).SequenceEqual(original), "Context download replaced current order");
        checks.Add("Right-click download executor targets selected missing Mod and refreshes without a document replacement");
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task Idle(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
#endif
