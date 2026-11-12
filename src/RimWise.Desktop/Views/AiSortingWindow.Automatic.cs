using System.IO;
using System.Windows;
using RimWise.Desktop.Services;
using RimWise.Desktop.Presentation;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Game;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private readonly Func<GamePaths>? gamePaths;
    private readonly AutomaticSortingSession automatic;
    private readonly Func<Action>? prepareSave;
    private bool automaticResultShown;
    private bool automaticRunning;
    private bool trialDecisionShown;
    private long automaticResultRevision;

    internal void ShowDependencyReasons(IReadOnlyList<string>? reasons)
    {
        DependencyList.ItemsSource = reasons;
        DependencyDetails.Visibility = reasons is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        StatusScroll.ScrollToTop();
    }

    internal MaintenanceLimits ReadMaintenanceLimits()
    {
        var attempts = 5;
        var budget = 50_000;
        if (AttemptLimitToggle.IsChecked == true && (!int.TryParse(AttemptsInput.Text, out attempts) || attempts < 1))
            throw new ArgumentException("测试次数限制必须是大于零的整数。");
        if (TokenLimitToggle.IsChecked == true && (!int.TryParse(BudgetInput.Text, out budget) || budget < 100))
            throw new ArgumentException("token 用量限制必须是至少 100 的整数。");
        return new(attempts, budget, OutputTokensPerRequest: 16384) { ExpandOutputBudget = true,
            AttemptLimitEnabled = AttemptLimitToggle.IsChecked == true,
            TokenLimitEnabled = TokenLimitToggle.IsChecked == true, ConfirmModRemoval = ConfirmRemovalToggle.IsChecked == true };
    }

    private async void Automatic_Click(object sender,RoutedEventArgs e) => await StartAutomaticAsync();

    internal async Task StartAutomaticAsync(string? resumeId=null)
    {
        if (closed || requestCancellation is not null || ownerLifetime.IsCancellationRequested) return;
        RefreshConfiguration();
        if (resumeId is null && IsOfflineMode) { await StartOfflineAsync(); return; }
        if (gamePaths is null || !AutomaticButton.IsEnabled) return;
        if (resumeId is null && settings.PermissionLevel != AiPermissionLevel.Level1OrderAndRequiredDependencies)
        {
            settings = settings with { PermissionLevel = AiPermissionLevel.Level1OrderAndRequiredDependencies };
            session.SettingsStore.Save(settings);
        }
        var logStarted = DateTimeOffset.UtcNow;
        DiagnosticEventLog.Current?.Record("automatic-sorting", resumeId is null ? "started" : "resumed", resumeId);
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime);
        requestCancellation=cancellation;
        automaticRunning=true;
        outcome=null;
        automaticResultShown=false;
        trialDecisionShown=false;
        ResponseText.Text="正在补全前置并准备规则排序；有本轮结果后会在此持续显示。\n"; SummaryText.Text="";
        ShowDependencyReasons(null);
        ShowStartupFailure(null);
        AcceptedList.ItemsSource=null; RejectedList.ItemsSource=null;
        var receiving=true;
        var runPrepared=false;
        var aiWatch=new System.Diagnostics.Stopwatch();
        var lastContent=TimeSpan.Zero;
        using var notice=new AiWaitNotice(WaitNoticeDelay,(_,_)=>
        {
            if(closed || !receiving || !aiWatch.IsRunning || cancellation.IsCancellationRequested) return;
            var quiet=aiWatch.Elapsed-lastContent;
            StatusText.Text=$"等待 AI 回报 · 已等待 {aiWatch.Elapsed.TotalSeconds:F0} 秒"
                +(quiet>=WaitNoticeDelay?"；可能超时，仍在等待，可取消。":"");
        });
        RefreshButtons();
        try
        {
            if (AiClient.ConfigurationProblem(settings.Normalize()) is { } configurationProblem)
                throw new InvalidOperationException(configurationProblem);
            var currentGameDirectory = gamePaths().GameDirectory;
            using (var game = await Task.Run(() => GameCloseConfirmation.Capture(currentGameDirectory), cancellation.Token))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (game.Count > 0)
                {
                    if (MessageDialog.Show(this,
                        "检测到环世界正在运行。开始自动排序前将关闭游戏，请确认已经保存游戏数据。\n\n确认已保存并关闭游戏，继续排序吗？选择“否”将取消本次排序。",
                        "开始排序前关闭游戏", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        automaticResultShown = true;
                        StatusText.Text = "已取消开始排序，游戏与当前排序均未改变。";
                        return;
                    }
                    StatusText.Text = "正在等待游戏正常关闭…";
                    await game.CloseAsync(cancellation.Token);
                }
            }
            runPrepared = true;
            try
            {
                lastLogRun = new(logStarted, null, [], resumeId is null ? [] : [resumeId]);
                logStore.SaveRun(lastLogRun);
            }
            catch (Exception ex) when (IsExpectedError(ex))
            { DiagnosticEventLog.Current?.Record("automatic-sorting", "log-index-failed", ex.GetType().Name); }
            // Stop optional enrichment before the runner captures catalogue identity.
            if (prepareCatalogue is not null) await prepareCatalogue();
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ResolveDuplicateInstallations(cancellation.Token))
            {
                automaticResultShown = true;
                StatusText.Text = "尚有重复安装未处理，已返回；当前排序保留，未启动游戏或请求 AI。";
                return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            MaintenanceLimits limits;
            if(resumeId is not null)
            {
                using var task=session.Tasks.Open(resumeId);
                limits=task.Current.Limits??throw new InvalidDataException("恢复记录缺少预算。");
                AttemptsInput.Text=limits.MaximumAttempts.ToString(); BudgetInput.Text=limits.TokenBudget.ToString();
                AttemptLimitToggle.IsChecked=limits.AttemptLimitEnabled;
                TokenLimitToggle.IsChecked=limits.TokenLimitEnabled;
                ConfirmRemovalToggle.IsChecked=limits.ConfirmModRemoval;
                updatingPermission=true;
                try { PermissionBox.SelectedIndex=limits.InsertionBaselineJson is null ? 1 : 0; }
                finally { updatingPermission=false; }
            }
            else
            {
                limits=ReadMaintenanceLimits();
                sortingPreferences.SaveLimits(limits);
                if (PermissionBox.SelectedIndex == 0)
                {
                    var picker = new InsertionPickerWindow(editor) { Owner = this };
                    if (picker.ShowDialog() != true) { StatusText.Text = "已取消选择，未启动测试。"; return; }
                    if (picker.SelectedIdentifiers.Length == 0)
                    {
                        // No additions means a normal sort of the current editor contents.
                        // Do not run a baseline comparison that would accept its existing errors.
                        PermissionBox.SelectedIndex = 1;
                        sortingPreferences.SaveMode("full", ConfirmRemovalToggle.IsChecked == true);
                    }
                    else
                    {
                        var baseline = editor.ApplyInsertionSelection(picker.SelectedIdentifiers);
                        limits = limits with { InsertionBaselineJson = baseline };
                    }
                    if (!ResolveDuplicateInstallations(cancellation.Token)) return;
                }
            }
            // An export may be produced by a newer process than the recorded task.
            // Record the executing build and policy at preparation, without credentials.
            DiagnosticEventLog.Current?.Record("automatic-sorting", "prepared",
                $"version={ApplicationBuildIdentity.ProductVersion(typeof(AiSortingWindow).Assembly)}; module={typeof(AiSortingWindow).Assembly.ManifestModule.ModuleVersionId:D}; resumed={resumeId is not null}; outputLimit={limits.OutputTokensPerRequest}; expandOutput={limits.ExpandOutputBudget}; confirmRemoval={limits.ConfirmModRemoval}");
            StatusText.Text="正在准备自动排序与游戏测试…";
            var maintenanceProgress=new Progress<MaintenanceProgress>(p=>
            {
                if(closed || !receiving) return;
                if(p.AwaitingAi) { aiWatch.Restart(); lastContent=TimeSpan.Zero; } else aiWatch.Stop();
                StatusText.Text=p.Message;
                ApplyMaintenanceProgress(p);
            });
            var warnedProcesses = new HashSet<int>();
            string? lastLoggedPhase = null;
            var gameProgress=new Progress<OrdinaryGameProgress>(p=>
            {
                if(closed || !receiving) return;
                if (lastLoggedPhase != p.Phase)
                {
                    lastLoggedPhase = p.Phase;
                    DiagnosticEventLog.Current?.Record("game-test", "phase-changed", $"phase={p.Phase}; processId={p.ProcessId}; elapsedSeconds={p.ElapsedSeconds}; logBytes={p.LogBytes}");
                }
                StatusText.Text=p.Phase == "diagnostics"
                    ? "本轮游戏测试已结束，正在整理日志与模组关联证据…"
                    : p.StartupDelayWarning
                    ? "游戏启动已等待 30 分钟，可能已卡死；游戏仍在运行，可继续等待或取消测试。"
                    : (p.Phase switch {
                        "mods-reset" => "游戏加载异常并重置了模组排序，正在收集异常链以继续排障",
                        "main-menu" => "正在检查主界面和加载报错",
                        "scenario-selection" => "正在选择测试剧本",
                        "storyteller-selection" => "正在选择叙述者和难度",
                        "starting-site" => "正在选择随机落点",
                        "ideology-selection" => "正在配置测试理念",
                        "starting-pawns" => "正在检查随机开局人物",
                        "game-configuration" => "正在完成开局配置",
                        "world-generation" => "正在生成测试世界",
                        "faction-check" => "正在检查派系生成",
                        "waiting-map" => "正在生成测试地图",
                        "pawn-test" => "正在随机选择小人并检查",
                        "map-running" => $"正在验证地图和小人运行 · {p.MapTicks}/120 ticks",
                        _ => p.StartupReady ? "主界面已加载，正在检查测试阶段" : "游戏正在启动" })
                        + $" · {p.ElapsedSeconds} 秒 · 日志 {p.LogBytes/1024:N0} KB";
                if(p.StartupDelayWarning && warnedProcesses.Add(p.ProcessId))
                    MessageDialog.Show(this, "游戏在 30 分钟内尚未完成启动，可能已卡死。\n\n游戏未被关闭，自动流程正在等待。你可以继续等待，或点击“取消”停止测试。",
                        "游戏可能已卡死", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
            var responseProgress=new Progress<AiResponseUpdate>(p=>
            {
                if(closed || !receiving) return;
                if(p.ContentDelta.Length>0) lastContent=aiWatch.Elapsed;
                AppendAutomaticReport(p.ContentDelta);
                if(p.Usage is { Valid:true } usage) TokenText.Text=$"本次服务已报告 {usage.Total:N0} token";
            });
            var result=await RunWithCopyFallbackAsync(limits, maintenanceProgress, gameProgress, responseProgress,
                cancellation.Token, resumeId);
            receiving=false;
            if(closed) return;
            ShowAutomaticResult(result);
            if (!ownerLifetime.IsCancellationRequested && !closed)
            {
                var chosen = ChooseLastOrder(result);
                if (!ReferenceEquals(chosen, result)) ShowAutomaticResult(chosen);
                result = chosen;
                if (!result.LastOrderDecisionRecorded) ShowTerminalNotice(result);
            }
        }
        catch(OperationCanceledException) { DiagnosticEventLog.Current?.Record("automatic-sorting", "cancelled"); automaticResultShown=true; if(!closed) StatusText.Text="已取消，测试清理已结束；已发送请求的用量请查看任务记录。"; }
        catch(Exception ex) when(IsExpectedError(ex))
        {
            DiagnosticEventLog.Current?.Record("automatic-sorting", "failed", ex.ToString());
            automaticResultShown=true;
            receiving=false;
            if(!closed)
            {
                StatusText.Text="自动排序未完成："+ex.Message;
                AppendAutomaticReport("\n" + StatusText.Text + "\n");
                MessageDialog.Show(this, StatusText.Text + "\n\n可在设置中导出诊断日志。测试记录目录：\n"
                    + Path.Combine(dataDirectory, "Data", "GameTests"), "自动排序失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            receiving=false;
            DiagnosticEventLog.Current?.Record("automatic-sorting", "visible-result", StatusText.Text + "\n" + SummaryText.Text + "\n" + StartupFailureText.Text);
            if (runPrepared) await FinishAutomaticLogsAsync(logStarted, resumeId);
            receiving=false; requestCancellation=null; automaticRunning=false;
            if(!closed) { RefreshUsage(); RefreshButtons(); }
        }
    }

    internal void ApplyMaintenanceProgress(MaintenanceProgress progress)
    {
        DiagnosticEventLog.Current?.Record("automatic-sorting", "progress",
            $"attempt={progress.Attempt}; reportedTokens={progress.ReportedTokens}; {progress.Message}\n{progress.ReportText}");
        AppendAutomaticReport($"\n[第 {progress.Attempt} 轮] {progress.Message}\n"
            + (progress.ReportText is { Length: > 0 } text ? text + "\n" : ""));
        if (progress.DependencyReasons is { } reasons) ShowDependencyReasons(reasons);
        if (progress.PreparedOrder is { } order)
        {
            AcceptedList.ItemsSource = order.Select((id, i) => $"{i + 1}. {id}").ToArray();
            SummaryText.Text = $"当前临时测试顺序共 {order.Count} 项，尚未保存到个人排序。测试可临时排除模组；最终移出遵循确认设置。";
        }
        TokenText.Text = $"本任务已报告 {progress.ReportedTokens:N0} token · 第 {progress.Attempt} 次测试";
    }

    private void AppendAutomaticReport(string text)
    {
        if (text.Length == 0) return;
        // Bound only the live text control, never the collected or exported logs.
        const int visibleCharacters = 256 * 1024;
        if (ResponseText.Text.Length + text.Length > visibleCharacters)
        {
            var combined = ResponseText.Text + text;
            ResponseText.Text = "较早内容请查看排序日志。\n" + combined[^visibleCharacters..];
        }
        else ResponseText.AppendText(text);
        ResponseText.ScrollToEnd();
    }

    internal AutomaticMaintenanceResult ChooseLastOrder(AutomaticMaintenanceResult result)
    {
        if (automatic.LastOrderProblem(result) is { } problem)
            return result with { Maintenance = result.Maintenance with { Message = result.Maintenance.Message
                + "\n最后测试顺序未保存：" + problem + "未覆盖当前清单，测试结果保留在任务记录中。" } };
        var plan = automatic.LastOrderChoice(result);
        if (plan is null) return result;
        if (plan.Rejected.Count > 0)
        {
            AppendAutomaticReport("\n最后测试顺序未通过保存校验，原排序保持不变：" + string.Join("；", plan.Rejected));
            return result;
        }
        var needsRemovalConfirmation = automatic.NeedsStoppedRemovalConfirmation(result);
        if (!AutomaticSortingSession.HasOrderChanges(plan.Before, plan.After) && needsRemovalConfirmation)
        {
            var removalPlan = automatic.LastOrderChoice(result, approveTrialRemovals: true);
            if (removalPlan is null || removalPlan.Rejected.Count > 0)
            {
                var reason = removalPlan is null ? "没有可确认的试排除方案。"
                    : "试排除候选未通过保存校验：" + string.Join("；", removalPlan.Rejected);
                AppendAutomaticReport("\nAI 未提出排序调整；" + reason + "原排序保持不变。");
                return automatic.ResolveLastOrder(result, keep: false);
            }

            var removal = new OrderPreviewWindow(new(new(plan.After), new(removalPlan.After)), "确认试排除候选是否移出？",
                "AI 未提出排序调整，保留全部模组时顺序不会变化。下方只显示未验证的临时试排除候选；移出不证明它们就是根因，且不会删除安装文件。\n\n"
                + TrialDescription(result.Maintenance), "移出并保存", "保留模组，不更改排序") { Owner = this };
            DiagnosticEventLog.Current?.Record("automatic-sorting", "last-order-dialog", "UnchangedOrderWithTrialRemovals");
            var removeTrialMods = removal.ShowDialog() == true;
            DiagnosticEventLog.Current?.Record("automatic-sorting", "stopped-removal-decision", $"remove={removeTrialMods}");
            var unchangedResolved = automatic.ResolveLastOrder(result, removeTrialMods, removeTrialMods);
            AppendAutomaticReport("\n" + unchangedResolved.Maintenance.Message + "\n");
            DiagnosticEventLog.Current?.Record("automatic-sorting", "last-order-decision", $"keep={removeTrialMods}");
            return unchangedResolved;
        }
        if (!AutomaticSortingSession.HasOrderChanges(plan.Before, plan.After))
        {
            var unchangedResolved = automatic.ResolveLastOrder(result, keep: false);
            AppendAutomaticReport("\nAI 未提出有效排序调整；当前顺序与原排序相同，原排序保持不变。\n");
            DiagnosticEventLog.Current?.Record("automatic-sorting", "last-order-decision", "keep=False; unchanged=true");
            return unchangedResolved;
        }
        var preview = new OrderPreviewWindow(new(new(plan.Before), new(plan.After)), "保留最后一次自动排序？",
            result.Maintenance.Message + "\n\n最后顺序尚未验证通过，仍可能出现红字。下方列出位置变化、新增前置和临时移出项。"
            + (needsRemovalConfirmation
                ? "\n此预览保留试排除模组。选择保存后，会单独询问是否移出这些模组；不同意移出也会保存排序调整。"
                : "\n保留将保存该顺序（不删除模组文件）；恢复原排序则放弃本次调整。"),
            "保留并保存最后顺序", "恢复原排序") { Owner = this };
        DiagnosticEventLog.Current?.Record("automatic-sorting", "last-order-dialog", result.Maintenance.Outcome.ToString());
        var keep = preview.ShowDialog() == true;
        var approveRemovals = false;
        if (keep && needsRemovalConfirmation)
        {
            var removalPlan = automatic.LastOrderChoice(result, approveTrialRemovals: true);
            if (removalPlan is { Rejected.Count: 0 })
            {
                var removal = new OrderPreviewWindow(new(new(plan.After), new(removalPlan.After)), "是否移出试排除模组？",
                    "以下模组只在测试中临时排除，剩余组合尚未验证通过，不能认定这些模组就是根因。\n"
                    + "移出仅改变排序，保留安装文件；不移出或关闭此窗口，会放回这些模组并保存排序调整。\n\n"
                    + TrialDescription(result.Maintenance), "移出并保存", "不移出，保存排序调整") { Owner = this };
                approveRemovals = removal.ShowDialog() == true;
                DiagnosticEventLog.Current?.Record("automatic-sorting", "stopped-removal-decision", $"remove={approveRemovals}");
            }
        }
        var resolved = automatic.ResolveLastOrder(result, keep, approveRemovals);
        AppendAutomaticReport("\n" + resolved.Maintenance.Message + "\n");
        DiagnosticEventLog.Current?.Record("automatic-sorting", "last-order-decision", $"keep={keep}");
        return resolved;
    }

    internal void ShowAutomaticResult(AutomaticMaintenanceResult result)
    {
        DiagnosticEventLog.Current?.Record("automatic-sorting", "finished", $"outcome={result.Maintenance.Outcome}; candidateApplied={result.CandidateApplied}; {result.Maintenance.Message}");
        automaticResultShown=true;
        automaticResultRevision=editor.ContentRevision;
        StatusText.Text=result.Maintenance.Message;
        TokenText.Text=$"本任务实际报告 {result.Maintenance.ReportedTokens:N0} token · 测试 {result.Maintenance.Attempts + result.Maintenance.ControlAttempts} 次"
            +(result.Maintenance.HasUnknownUsage?"；部分请求未回报用量，另按预估记账。":"");
        AppendAutomaticReport("\n任务结果：" + result.Maintenance.Message + "\n"
            + (result.Maintenance.Analysis is { Length: > 0 } finalAnalysis ? "AI 分析：" + finalAnalysis + "\n"
                : result.Maintenance.Requests > 0 ? "本轮未收到可显示的 AI 最终分析；请查看上方各轮服务状态和本地试排除记录。\n"
                : "本次未请求 AI，已按本地规则及测试结果处理。\n"));
        var keptTrialMods = result.Maintenance.TrialDecisionRecorded && !result.Maintenance.TrialRemovalsApproved;
        AcceptedList.ItemsSource=(result.LastOrderKept || keptTrialMods || result.PreparationApplied ? editor.Rows.Select(r => r.PackageId) : result.Maintenance.LastAttemptedOrder)
            .Select((id,i)=>$"{i+1}. {id}").ToArray();
        RejectedList.ItemsSource=result.Subscriptions.Select(d=>$"已记录缺失模组／前置订阅：{d.PackageId} · {d.WorkshopId}")
            .Concat(result.Maintenance.ConflictApprovals.Select(a=>"已记录移出排序："+string.Join("、",a.RemovedIds)+"（安装文件保留）"))
            .Concat(result.Maintenance.TrialExclusions.Select(t=>$"第 {t.Attempt} 轮临时试排除："+string.Join("、",t.RemovedIds))).ToArray();
        SummaryText.Text=result.CandidateApplied && result.Maintenance.ExistingErrorsOnly ? "新增 Mod 已定位并保存；完整对照测试仅重现已有错误。原有红字仍然存在，并非全部无红。"
            : result.CandidateApplied && result.Maintenance.HasVulkanConfigurationWarning ? "已排序完成并保存。除已忽略的 Vulkan 配置读取错误外，本轮地图生成与短时运行测试未发现其他错误；未覆盖游戏内全部内容。"
            : result.CandidateApplied ? "已排序完成。本轮地图生成与短时运行测试未检测到红字；未覆盖游戏内全部内容。"
            : "任务记录已保存，可在恢复任务中查看。";
        if (result.LastOrderDecisionRecorded) SummaryText.Text = result.LastOrderKept
            ? "已保留最后测试顺序；该组合尚未验证通过，可在主界面撤销。" : "已恢复原排序，未保留本次调整。";
        if (keptTrialMods) SummaryText.Text = result.Maintenance.Outcome == MaintenanceOutcome.Passed
            ? "已保留模组和排序调整。通过验证的是移除候选后的组合，保留全部模组后仍可能出现原有报错。"
            : "已保留模组和排序调整；该组合尚未验证通过，仍可能出现原有报错。";
        if (result.Maintenance.Analysis is { Length: > 0 } analysis)
            SummaryText.Text += "\n\nAI 分析：\n" + analysis;
        if (result.Maintenance.HasWarnings)
            SummaryText.Text += (result.Maintenance.HasVulkanConfigurationWarning ? "\n\n" + AiRuntimeContext.VulkanWarningText
                : result.Maintenance.ExistingErrorsOnly ? "\n\n原排序的红字和黄字均保留在日志中。" : result.Maintenance.Outcome == MaintenanceOutcome.Passed
                ? "\n\n黄字警告已保留，不影响无红字的成功判定。" : "\n\n本轮黄字警告已保留；任务尚未通过验证。")
                + "\n本轮日志：\n" + string.Join("\n", result.Maintenance.LogPaths.Select(RimWise.Integration.Diagnostics.StoredEvidence.Resolve));
        ShowStartupFailure(result.Maintenance.StartupFailure, result.Maintenance.ReplyFailure);
        if (result.Maintenance.TrialExclusions.Count > 0 && result.Maintenance.Outcome != MaintenanceOutcome.Passed
            && !result.LastOrderDecisionRecorded)
            SummaryText.Text += "\n\n尚未找到通过验证的移除方案。"
                + (result.PreparationApplied ? "已放回临时试排除模组，保留必要前置与规则排序。"
                    : result.Maintenance.OriginalRestored ? "原排序已恢复。" : "尚未确认恢复，请核对恢复记录。")
                + "\n以下仅为排查记录：\n" + TrialDescription(result.Maintenance);
    }

    internal static string? CompletionNotice(AutomaticMaintenanceResult result) =>
        result.Maintenance.Outcome == MaintenanceOutcome.Passed && result.CandidateApplied
            && result.Maintenance.TrialExclusions.Count == 0
            ? result.Maintenance.Message + (result.Maintenance.HasVulkanConfigurationWarning ? "\n\n" + AiRuntimeContext.VulkanWarningText
                : result.Maintenance.HasWarnings && !result.Maintenance.ExistingErrorsOnly
                ? "\n\n仅有黄字或白字不影响排序成功，无需继续调整。" : "") : null;

    internal void ShowTerminalNotice(AutomaticMaintenanceResult result)
    {
        var notice = TerminalNotice(result, trialDecisionShown);
        if (notice is null) return;
        DiagnosticEventLog.Current?.Record("automatic-sorting", "result-dialog", notice.Value.Title);
        if (CompletionNotice(result) is not null && !result.Maintenance.ExistingErrorsOnly)
            new TrialExclusionWindow([], result.Maintenance.HasWarnings, editor.ShowModBadgeIcons,
                vulkanConfigurationWarning: result.Maintenance.HasVulkanConfigurationWarning) { Owner = this }.ShowDialog();
        else
            MessageDialog.Show(this, notice.Value.Text, notice.Value.Title, MessageBoxButton.OK, notice.Value.Image);
    }

    // A trial decision already reports the passing reduced combination. Only suppress
    // its duplicate after publication succeeds; a failed save still needs a failure notice.
    internal static (string Title, string Text, MessageBoxImage Image)? TerminalNotice(
        AutomaticMaintenanceResult result, bool decisionShown)
    {
        var maintenance = result.Maintenance;
        if (maintenance.Outcome == MaintenanceOutcome.Cancelled) return null;
        if (maintenance.Outcome == MaintenanceOutcome.Passed && result.CandidateApplied)
        {
            if (decisionShown && maintenance.TrialDecisionRecorded) return null;
            var kept = maintenance.TrialDecisionRecorded && !maintenance.TrialRemovalsApproved;
            return (kept ? "排序已保存" : "自动排序完成", maintenance.Message
                + (kept ? "\n\n保留全部模组的组合未验证通过，仍可能出现原有报错。"
                    : maintenance.HasVulkanConfigurationWarning ? "\n\n" + AiRuntimeContext.VulkanWarningText
                    : maintenance.HasWarnings && !maintenance.ExistingErrorsOnly ? "\n\n仅有黄字或白字不影响排序成功。" : ""), MessageBoxImage.Information);
        }
        var details = maintenance.StartupFailure?.DisplayText;
        var text = maintenance.Message
            + (string.IsNullOrWhiteSpace(details) ? "" : "\n\n" + details);
        if (maintenance.LogPaths.Count > 0)
            text += "\n\n本轮日志：\n" + string.Join("\n", maintenance.LogPaths.Select(StoredEvidence.Resolve).Distinct());
        text += "\n\n可在设置中导出诊断日志。";
        return (maintenance.Outcome == MaintenanceOutcome.Failed ? "自动排序失败" : "自动排序未完成", text, MessageBoxImage.Warning);
    }

    internal void ShowStartupFailure(StartupFailureDetails? failure, AiReplyFailure replyFailure = AiReplyFailure.None)
    {
        StartupFailureText.Text = failure is null ? "" : string.Join("\n\n",
            new[] { AiReplyFailureText.Describe(replyFailure), failure.DisplayText }.Where(s => s.Length > 0));
        StartupFailureText.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
        FailureDetails.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
        ResultPanel.Visibility = Visibility.Visible;
    }

    private async Task<string[]?> ChooseConflictAsync(DeclaredConflict conflict, CancellationToken token)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            if (closed || token.IsCancellationRequested) return null;
            var dialog = new ConflictChoiceWindow(conflict, ResolveConflictRow, editor.ShowModBadgeIcons) { Owner = this };
            using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (dialog.IsVisible) dialog.Close();
            })));
            return dialog.ShowDialog() == true ? dialog.SelectedIds : null;
        });
    }

    private Task<bool> ConfirmTrialsAsync(MaintenanceResult result, CancellationToken token)
    {
        if (closed || token.IsCancellationRequested) return Task.FromResult(false);
        var ids = result.TrialExclusions.SelectMany(t => t.RemovedIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = result.TrialExclusions.Select(t => t.SelectedId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = ids.Select(id =>
        {
            return new TrialRemovalRow(ResolveConflictRow(id), selected.Contains(id) ? "试排除项" : "连带依赖项");
        }).OrderBy(r => r.Mod.Index == 0 ? int.MaxValue : r.Mod.Index).ToArray();
        var dialog = new TrialExclusionWindow(rows, result.HasWarnings, editor.ShowModBadgeIcons, result.ExistingErrorsOnly,
            result.HasVulkanConfigurationWarning) { Owner = this };
        using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        })));
        var remove = dialog.ShowDialog() == true;
        trialDecisionShown = true;
        DiagnosticEventLog.Current?.Record("automatic-sorting", "removal-decision", $"remove={remove}; packages={string.Join(',', ids)}");
        return Task.FromResult(remove);
    }

    private LoadOrderRow ResolveConflictRow(string id)
    {
        var existing = editor.Rows.FirstOrDefault(r => r.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var row = new LoadOrderRow(id, 0);
        row.SetMatch(editor.CurrentCatalogue?.Resolve(id, editor.CatalogueVersion));
        return row;
    }

    private string TrialDescription(MaintenanceResult result)
    {
        string Name(string id) => editor.CurrentCatalogue?.Resolve(id, editor.CatalogueVersion)
            .UniqueInstallation?.Metadata?.Name is { Length: > 0 } name ? name + "（" + id + "）" : id;
        return string.Join("\n\n", result.TrialExclusions.Select(t => $"第 {t.Attempt} 轮：" + Name(t.SelectedId)
            + (t.RemovedIds.Length > 1 ? "\n连带依赖项：" + string.Join("、", t.RemovedIds.Where(id => id != t.SelectedId).Select(Name)) : "")));
    }
}
