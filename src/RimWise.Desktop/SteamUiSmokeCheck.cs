#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Game;
using RimWise.Integration.Steam;

namespace RimWise.Desktop;

// Synthetic transport only. No Steam mutations, AI calls, or player profile access.
internal static class SteamUiSmokeCheck
{
    internal static void Attach(MainWindow owner, string output)
    {
        owner.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                await RunAsync(owner, output);
                owner.ViewModel.MarkExported();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                owner.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task RunAsync(MainWindow owner, string output)
    {
        owner.LoadSample();
        var checks = new List<string>();
        await WorkshopRefreshSmokeCheck.RunAsync(owner, output, checks);
        var release = new TaskCompletionSource<IReadOnlyList<WorkshopSearchItem>>();
        var cancelled = false;
        var search = new WorkshopSearchWindow("中文 模组", async (_, token) =>
        {
            using var registration = token.Register(() => cancelled = true);
            return await release.Task;
        }) { Owner = owner };
        search.Show(); await Idle(search);
        Require(!search.SearchButton.IsEnabled && !search.ChooseButton.IsEnabled && search.StatusText.Text.Contains("正在"), "Search not visibly pending");
        search.Close();
        release.SetResult([new("123", "迟到结果", 294100)]);
        await search.PendingSearch;
        Require(cancelled && search.ResultsGrid.ItemsSource is null && !search.IsVisible, "Late result accepted after close");
        checks.Add("Pending Steam search stays responsive; close cancels and ignores late results");

        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            var chosen = new WorkshopSearchWindow("中文 模组", (_, _) => Task.FromResult<IReadOnlyList<WorkshopSearchItem>>(
                [new("123", "中文 模组 — Workshop result", 294100)])) { Owner = owner };
            Exception? failure = null;
            chosen.ContentRendered += async (_, _) =>
            {
                try
                {
                    await chosen.PendingSearch;
                    chosen.ResultsGrid.SelectedIndex = 0;
                    await Idle(chosen);
                    Require(chosen.PageButton.IsEnabled && chosen.ChooseButton.IsEnabled, "Selection actions disabled");
                    PrototypeSmokeCheck.SavePreview(chosen, Path.Combine(output, $"steam-search-{theme}.png"));
                    chosen.ChooseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { failure = ex; chosen.Close(); }
            };
            Require(chosen.ShowDialog() == true && failure is null && chosen.SelectedItem?.Id == "123", "Candidate selection failed: " + failure);
            checks.Add($"{theme} Steam search selection returns the chosen ID without subscribing");
        }
        foreach (var failed in new[] { false, true })
        {
            var empty = new WorkshopSearchWindow("none", (_, _) => failed
                ? Task.FromException<IReadOnlyList<WorkshopSearchItem>>(new IOException("AccessDenied"))
                : Task.FromResult<IReadOnlyList<WorkshopSearchItem>>([])) { Owner = owner };
            empty.Show(); await Idle(empty); await empty.PendingSearch;
            Require(empty.SearchButton.IsEnabled && !empty.ChooseButton.IsEnabled
                && empty.StatusText.Text.Contains(failed ? "AccessDenied" : "未找到"), "Failure/no-match lost");
            empty.Close();
        }
        checks.Add("Empty and failed searches retain a retryable window with explicit reasons");
        var row = new MissingModDownloadRow("missing.mod", "", "unknown");
        Require(row.CanEditSource && !row.HasSource && !row.CanDownload, "Missing source state");
        row.WorkshopInput = "123";
        Require(row.HasSource && row.CanDownload, "Selected source state");
        row.SetBusy(true);
        Require(!row.CanEditSource && !row.CanDownload, "Source editable during subscription");
        Require(!new MissingModDownloadRow("dlc", "", "", false).CanEditSource, "DLC search enabled");
        checks.Add("Missing rows accept selected sources; active downloads and DLC keep source editing disabled");

        await MissingModDownloadSmokeCheck.RunAsync(owner, output, checks);
        var success = new AutomaticMaintenanceResult(new(MaintenanceOutcome.Passed, "测试通过，已保存", 1, 0, 0, false, [], true), null, [])
            { CandidateApplied = true };
        Require(AiSortingWindow.TerminalNotice(success, false)?.Title == "自动排序完成", "Success notice absent");
        Require(AiSortingWindow.TerminalNotice(success with { Maintenance = success.Maintenance with { HasWarnings = true } }, false)?.Text.Contains("黄字") == true,
            "Warnings changed success");
        foreach (var outcome in new[] { MaintenanceOutcome.Failed, MaintenanceOutcome.Stopped, MaintenanceOutcome.Unavailable, MaintenanceOutcome.RecoveryRequired })
        {
            var failed = success with { CandidateApplied = false, Maintenance = success.Maintenance with
                { Outcome = outcome, Message = "合成停止原因", LogPaths = [Path.Combine(output, "本轮日志.txt")] } };
            var notice = AiSortingWindow.TerminalNotice(failed, true);
            Require(notice?.Image == MessageBoxImage.Warning && notice.Value.Text.Contains("合成停止原因")
                && notice.Value.Text.Contains("本轮日志.txt"), "Failure notice suppressed: " + outcome);
        }
        Require(AiSortingWindow.TerminalNotice(success with { CandidateApplied = false }, true)?.Image == MessageBoxImage.Warning, "Unapplied result announced success");
        var decision = success with { Maintenance = success.Maintenance with { TrialDecisionRecorded = true, TrialRemovalsApproved = false } };
        Require(AiSortingWindow.TerminalNotice(decision, true) is null, "Duplicate trial dialog");
        Require(AiSortingWindow.TerminalNotice(decision, false)?.Text.Contains("未验证通过") == true, "Restored keep decision announced no errors");
        Require(AiSortingWindow.TerminalNotice(success with { Maintenance = success.Maintenance with { Outcome = MaintenanceOutcome.Cancelled } }, false) is null,
            "Manual cancellation treated as failure");
        checks.Add("Terminal notices cover success, warnings, all failure states, unapplied candidates and restored decisions without duplicate removal prompts");

        var sorting = new AiSortingWindow(owner.ViewModel, Path.Combine(output, "sorting-profile")) { Owner = owner };
        sorting.Show(); await Idle(sorting);
        foreach (var passed in new[] { true, false })
        {
            Window? dialog = null;
            _ = sorting.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.Owner == sorting && w.IsVisible);
                if (dialog is not null)
                {
                    PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, passed ? "sorting-success.png" : "sorting-failure.png"));
                    dialog.Close();
                }
            }));
            sorting.ShowTerminalNotice(passed ? success : success with { CandidateApplied = false,
                Maintenance = success.Maintenance with { Outcome = MaintenanceOutcome.Failed, Message = "合成启动失败；本轮日志已保留" } });
            Require(passed ? dialog is TrialExclusionWindow : dialog is MessageDialogWindow, "Wrong or absent result popup");
        }
        sorting.Close();
        checks.Add("Actual success and failure modal windows open with the automatic-sort window as owner");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static Task Idle(Window window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
