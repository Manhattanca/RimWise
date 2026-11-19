using RimWise.Core;

namespace RimWise.Integration.AI;

public sealed record MaintenanceLimits(int MaximumAttempts = 5, int TokenBudget = 50_000,
    // Kept for older checkpoints only; the controller no longer enforces a total time limit.
    int OutputTokensPerRequest = 4096, int MaximumMinutes = 0)
{
    // Missing fields in older checkpoints keep their original bounded behavior.
    // New UI tasks explicitly supply the user's switches (both off by default).
    public bool AttemptLimitEnabled { get; init; } = true;
    public bool TokenLimitEnabled { get; init; } = true;
    public bool ConfirmModRemoval { get; init; } = true;
    // Opt-in for new tasks; old recovery records keep their original request cap.
    public bool ExpandOutputBudget { get; init; }
    // Scalar JSON preserves record equality when checkpoints are deserialized.
    public string? InsertionBaselineJson { get; init; }
    public bool AttemptLimitReached(int completed) => AttemptLimitEnabled && completed >= MaximumAttempts;
    public bool TokenLimitReached(long tokens) => TokenLimitEnabled && tokens >= TokenBudget;
    public bool WouldExceedTokenLimit(long tokens) => TokenLimitEnabled && tokens > TokenBudget;

    internal void Validate()
    {
        InsertionScope.Read(InsertionBaselineJson);
        if (AttemptLimitEnabled && MaximumAttempts < 1 || TokenLimitEnabled && TokenBudget < 100
            || OutputTokensPerRequest is < 1 or > 32768 || MaximumMinutes is < 0 or > 120)
            throw new ArgumentException("自动维护次数或 token 预算无效，或旧任务记录不兼容。");
    }
}

public enum MaintenanceOutcome { Unavailable, Passed, Stopped, Cancelled, Failed, RecoveryRequired }
public sealed record MaintenanceProgress(int Attempt, string Message, int ReportedTokens)
{
    public bool AwaitingAi { get; init; }
    public IReadOnlyList<string>? PreparedOrder { get; init; }
    public IReadOnlyList<string>? DependencyReasons { get; init; }
    // Display-only round report; never interpreted as executable model actions.
    public string? ReportText { get; init; }
}
public sealed record MaintenanceResult(MaintenanceOutcome Outcome, string Message, int Attempts, int Requests,
    int ReportedTokens, bool HasUnknownUsage, IReadOnlyList<string> LastAttemptedOrder, bool OriginalRestored)
{
    public int ControlAttempts { get; init; }
    public string? InsertionBaselineJson { get; init; }
    public bool ExistingErrorsOnly { get; init; }
    public string? TaskId { get; init; }
    public IReadOnlyList<ConflictRemovalApproval> ConflictApprovals { get; init; } = [];
    public IReadOnlyList<TrialExclusion> TrialExclusions { get; init; } = [];
    public bool TrialRemovalsApproved { get; init; }
    public bool TrialDecisionRecorded { get; init; }
    public StartupFailureDetails? StartupFailure { get; init; }
    public AiReplyFailure ReplyFailure { get; init; }
    public string? Analysis { get; init; }
    public bool HasWarnings { get; init; }
    public bool HasVulkanConfigurationWarning { get; init; }
    public IReadOnlyList<string> LogPaths { get; init; } = [];
}

/// <summary>Not a settings toggle: a verified broker and game-test adapter are required.</summary>
public static class AutomaticMaintenanceAvailability
{
    public const string Reason = "自动维护暂不可用：AI 信息边界、受控执行器及真实游戏测试闭环尚未完成验收。";
    public static bool Available => false;
}

internal sealed record MaintenanceGameReport(bool StartupVerified, IReadOnlyList<string> Errors)
{
    public string[] ErrorSignatures { get; init; } = [];
    public string? FailureFingerprint { get; init; }
    public bool ExistingErrorsOnly { get; init; }
    public InsertionEvidence? InsertionEvidence { get; init; }
    public AiLogContext? LogContext { get; init; }
    public AiRuntimeContext? RuntimeContext { get; init; }
    public IReadOnlyList<string> LogPaths { get; init; } = [];
    public StartupFailureDetails? StartupFailure { get; init; }
    public IReadOnlyList<AiDiagnosticFact> DiagnosticFacts { get; init; } = [];
    public bool CanDiagnose { get; init; }
}

/// <summary>
/// Trusted adapter only. Begin journals before writing; Finish stops its owned test process before
/// restoring or committing. Preflight verifies the broker's data and operation boundaries, not game isolation.
/// No shell, arbitrary path, or process adapter is supplied by an AI. Checks use synthetic adapters only.
/// </summary>
internal interface ILevelOneGameSession
{
    Task<bool> VerifySafetyAsync(CancellationToken token);
    Task BeginAsync(CancellationToken token);
    Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token);
    Task ApplyAsync(AiOrderPlan plan, CancellationToken token);
    Task FinishAsync(bool acceptTestedOrder, CancellationToken token);
}

internal sealed record MaintenancePreparation(AiRequest Request, ModCatalogue Catalogue, IReadOnlyList<string> InitialOrder)
{
    internal IReadOnlyList<CustomOrderRule>? Rules { get; init; }
}

/// <summary>Trusted preparation may download declared dependencies. It journals intent before account changes.</summary>
internal interface IMaintenancePreparation
{
    string ContextHash(AiRequest request, ModCatalogue catalogue, IReadOnlyList<CustomOrderRule> rules);
    string RecoveryContextHash(AiRequest request, ModCatalogue catalogue, IReadOnlyList<CustomOrderRule> rules,
        IReadOnlyList<string> initiallyMissing) => ContextHash(request, catalogue, rules);
    Task<MaintenancePreparation> PrepareAsync(AiRequest request, ModCatalogue catalogue,
        AiTaskRecoveryStore.AiTaskLease task, CancellationToken token);
}

internal interface IMaintenanceConflictResolver
{
    Task<AiOrderPlan?> ResolveAsync(StructuredAiResponse response, MaintenanceGameReport report,
        IReadOnlyList<string> current, AiTaskRecoveryStore.AiTaskLease task, CancellationToken token);
}

/// <summary>Bounded level-one controller. The shipped path refuses execution until a real adapter is verified.</summary>
public sealed class LevelOneMaintenance
{
    private readonly IAiClient client;
    private readonly AiTokenEstimator estimator;
    private readonly ILevelOneGameSession? session;
    private readonly AiTaskRecoveryStore tasks;
    private readonly string? resumeTaskId;
    private readonly bool testOriginalFirst;
    private int active;

    public LevelOneMaintenance(IAiClient client, AiTokenEstimator estimator) : this(client, estimator, null) { }
    internal LevelOneMaintenance(IAiClient client, AiTokenEstimator estimator, ILevelOneGameSession? session,
        string? resumeTaskId = null, bool testOriginalFirst = false)
        => (this.client, this.estimator, this.session, tasks, this.resumeTaskId, this.testOriginalFirst) =
            (client, estimator, session, new(estimator.ApplicationDirectory), resumeTaskId, testOriginalFirst);

    public async Task<MaintenanceResult> RunAsync(AiRequest request, ModCatalogue catalogue,
        IReadOnlyList<CustomOrderRule> rules, MaintenanceLimits limits,
        IProgress<MaintenanceProgress>? progress, CancellationToken token)
    {
        limits.Validate();
        request = request with { CurrentOrder = request.CurrentOrder.ToArray(), InstalledMods = request.InstalledMods.ToArray(),
            RetainedMods = request.RetainedMods.ToArray(), Diagnostics = request.Diagnostics.ToArray(), OutputTokenLimit = limits.OutputTokensPerRequest };
        var baseDiagnostics = request.Diagnostics;
        var baseFacts = request.DiagnosticFacts.ToArray();
        var current = request.CurrentOrder.ToArray();
        rules = rules.ToArray();
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return new(MaintenanceOutcome.Stopped, "已有自动维护正在运行。", 0, 0, 0, false, Array.AsReadOnly(current), false);
        var attempts = 0;
        var requests = 0;
        var spent = 0;
        var unknown = false;
        var unknownReserve = 0;
        var pendingReserve = 0;
        var requestInFlight = false;
        var began = false;
        var passed = false;
        var existingErrorsOnly = false;
        var restored = false;
        var nonOrderableBlockerDetected = false;
        var outcome = MaintenanceOutcome.Failed;
        var message = "自动维护尚未完成。";
        string? trialStopReason = null;
        string? nonOrderableBlockerSummary = null;
        AiTaskRecoveryStore.AiTaskLease? recoveryTask = null;
        IDisposable? usagePeriod = null;
        var persistFinal = false;
        bool AttemptLimitReached(int completed) => limits.AttemptLimitReached(completed + (recoveryTask?.Current.InsertionControlAttempts ?? 0));
        var initialAttempt = 1;
        var seen = new List<string[]> { current };
        ConflictRemovalApproval[] approvals = [];
        TrialExclusion[] trials = [];
        IReadOnlyList<string[]> cycleGroups = [];
        string[] associated = [];
        string[] lastSwappedOwners = [];
        string? lastSwapFailure = null;
        bool cyclePlanBlocked = false;
        StartupFailureDetails? startupFailure = null;
        var replyFailure = AiReplyFailure.None;
        string? analysis = null;
        bool hasWarnings = false;
        bool hasVulkanConfigurationWarning = false;
        IReadOnlyList<string> logPaths = [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Legacy time fields remain readable in recovery records. Only user cancellation,
        // attempt limits and token budgets stop product tasks; slow startup is a warning.
        try
        {
            if (session is null)
                return Result(MaintenanceOutcome.Unavailable, AutomaticMaintenanceAvailability.Reason);
            if (!request.Settings.Enabled || request.Settings.PermissionLevel != AiPermissionLevel.Level1OrderAndRequiredDependencies)
                return Result(MaintenanceOutcome.Stopped, "请启用 AI 并选择自动排序或新增 Mod 模式。");
            if (AiClient.ConfigurationProblem(request.Settings.Normalize()) is { } configurationProblem)
                return Result(MaintenanceOutcome.Stopped, configurationProblem);
            deadline.Token.ThrowIfCancellationRequested();
            if (!await session.VerifySafetyAsync(deadline.Token))
                return Result(MaintenanceOutcome.Unavailable, "本次 AI 信息与执行边界检查未通过，未写入测试配置或发送 AI 请求。");
            deadline.Token.ThrowIfCancellationRequested();
            var preparation = session as IMaintenancePreparation;
            if (resumeTaskId is not null) recoveryTask = tasks.Open(resumeTaskId);
            var initiallyMissing = recoveryTask?.Current.InitialMissingIds ?? request.CurrentOrder
                .Where(id => catalogue.Resolve(id, GameVersion.Parse(request.GameVersion)).Candidates.Count == 0).ToArray();
            var contextHash = preparation?.RecoveryContextHash(request, catalogue, rules, initiallyMissing) ?? AiTaskRecoveryStore.ContextHash(request,
                AiOrderPlanner.Fingerprint(request.CurrentOrder, catalogue, request.RetainedMods, rules, GameVersion.Parse(request.GameVersion)));
            if (resumeTaskId is null)
                recoveryTask = tasks.Create(new(AiTaskKind.Maintenance, AiTaskPhase.Prepared, contextHash,
                    current, current, DateTimeOffset.UtcNow) { Limits = limits, TestOriginalFirst = testOriginalFirst, DatabaseSnapshotId = request.DatabaseSnapshotId,
                    DeadlineUtc = DateTimeOffset.MaxValue, SeenOrders = [current], InitialMissingIds = initiallyMissing });
            else
            {
                var saved = recoveryTask!.Current;
                approvals = saved.ConflictApprovals;
                trials = saved.TrialExclusions;
                startupFailure = saved.Feedback?.StartupFailure?.CopyValidated();
                hasWarnings = saved.Feedback?.RuntimeContext is not null;
                hasVulkanConfigurationWarning = saved.Feedback?.RuntimeContext?.HasVulkanConfigurationWarning == true;
                replyFailure = saved.Feedback?.ReplyFailure ?? AiReplyFailure.None;
                if (saved.Kind != AiTaskKind.Maintenance || saved.Finished || saved.ContextHash != contextHash || saved.Limits != limits)
                    return Result(MaintenanceOutcome.RecoveryRequired, "任务、安装资料、规则或预算已变化，不能继续旧维护任务。");
                current = saved.CurrentOrder.ToArray(); attempts = saved.Attempts; requests = saved.Requests;
                spent = saved.ReportedTokens; unknown = saved.UnknownUsage;
                unknownReserve = saved.ReservedTokens;
                if (saved.HasUnresolvedRequest)
                    return Result(MaintenanceOutcome.RecoveryRequired, "上次请求未确认完成；已保留记录，不自动重发该请求。");
                if (saved.TestOriginalFirst != testOriginalFirst)
                    return Result(MaintenanceOutcome.RecoveryRequired, "此任务使用旧版排障策略。原记录和用量已保留，不会重发请求；请新建任务以原排序进行首轮测试。");
                var savedPass = saved.Feedback is { Passed: true };
                if (AttemptLimitReached(saved.Attempts) && saved.Feedback is null
                    || limits.TokenLimitReached((long)spent + unknownReserve) && !savedPass)
                    return Result(MaintenanceOutcome.Stopped, "原任务次数或 token 预算已用尽；候选排序仍保留，恢复不会重置预算。");
                seen = saved.SeenOrders.Select(order => order.ToArray()).ToList();
                initialAttempt = saved.Attempts + 1;
            }
            persistFinal = true;
            usagePeriod = recoveryTask.TrackUsagePeriod(request);
            // A saved AI error is terminal too: do not prepare, replay a candidate, or
            // begin a local experiment merely because the user reopened the task.
            if (recoveryTask.Current.Feedback is { } rejectedFeedback
                && (rejectedFeedback.ReplyFailure != AiReplyFailure.None || rejectedFeedback.ServiceError is not null))
            {
                replyFailure = rejectedFeedback.ReplyFailure == AiReplyFailure.None
                    ? AiReplyFailure.ServiceFailure : rejectedFeedback.ReplyFailure;
                logPaths = rejectedFeedback.LogPaths;
                restored = true;
                Stop(AiErrorStop());
                return Result(outcome, message);
            }
            string[]? initialOrder = null;
            if (preparation is not null)
            {
                Report(testOriginalFirst ? "正在准备按当前顺序测试，不改动模组及顺序…" : "正在补全必要前置并完成初始排序…");
                var prepared = await preparation.PrepareAsync(request, catalogue, recoveryTask, deadline.Token);
                approvals = recoveryTask.Current.ConflictApprovals;
                request = prepared.Request;
                baseFacts = request.DiagnosticFacts.ToArray();
                catalogue = prepared.Catalogue;
                rules = prepared.Rules ?? rules;
                // A stop/crash in preparation can persist a user choice before any candidate is applied.
                // Rebuild that first candidate on resume instead of attempting to test the original conflict.
                if (resumeTaskId is null || attempts == 0 && current.SequenceEqual(request.CurrentOrder, StringComparer.Ordinal)
                    || approvals.SelectMany(a => a.RemovedIds).Any(id => current.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    initialOrder = prepared.InitialOrder.ToArray();
            }
            if (!testOriginalFirst && GameVersion.Parse(request.GameVersion) is { } cycleVersion)
            {
                var cycleOrder = DeclaredConflictResolution.Replay(catalogue,
                    AiOrderPlanner.ExpandRequiredOrder(request.CurrentOrder, catalogue, cycleVersion, allowMissing: true),
                    request.RetainedMods, cycleVersion, approvals);
                cycleGroups = new LoadOrderSorter().Sort(catalogue, cycleOrder, cycleVersion, cycleVersion,
                    rules, deferAdvisoryCycles: true).DeferredCycles;
            }
            began = true; // A partially failed Begin still needs recovery.
            await session.BeginAsync(deadline.Token);
            // The new adapter begins with the original order. Rebuild the persisted candidate
            // through the same planner and executor, never by trusting a serialized plan.
            var target = initialOrder ?? current;
            if (trials.Length > 0)
            {
                if (session is not IMaintenanceTrialSession trialSession)
                    throw new InvalidOperationException("测试器不支持恢复临时试排除。");
                var version = GameVersion.Parse(request.GameVersion) ?? throw new InvalidDataException("游戏版本未知。");
                var baseline = DeclaredConflictResolution.Replay(catalogue,
                    AiOrderPlanner.ExpandRequiredOrder(request.CurrentOrder, catalogue, version, allowMissing: true),
                    request.RetainedMods, version, approvals);
                var remaining = TrialExclusionPlanning.Replay(catalogue, baseline, request.RetainedMods, version, trials);
                trialSession.AuthorizeTrialExclusions(trials);
                // A crash may happen after journalling the trial but before applying it. Rebuild its
                // reduced set without replaying the paid reply or restoring excluded mods between rounds.
                target = current.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(remaining) ? current
                    : new LoadOrderSorter().Sort(catalogue, remaining, version, version, rules, deferAdvisoryCycles: true).Order.ToArray();
            }
            if (!request.CurrentOrder.SequenceEqual(target, StringComparer.Ordinal))
            {
                var seed = trials.Length == 0
                    ? AiOrderPlanner.PreparedOrder(request.CurrentOrder, target, catalogue, request.RetainedMods,
                        GameVersion.Parse(request.GameVersion) ?? throw new InvalidDataException("游戏版本未知。"), rules, approvals)
                    : TrialExclusionPlanning.Plan(request.CurrentOrder, target, catalogue, request.RetainedMods,
                        GameVersion.Parse(request.GameVersion)!, rules, trials, approvals);
                if (!seed.CanApply || !seed.After.SequenceEqual(target, StringComparer.Ordinal))
                    throw new InvalidOperationException("候选排序未通过当前安装校验：" +
                        (seed.Rejected.Count == 0 ? "候选结果与目标不一致。" : string.Join("；", seed.Rejected.Take(3))));
                await session.ApplyAsync(seed, deadline.Token);
                current = target;
                if (!seen.Any(order => order.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))) seen.Add(current);
                if (recoveryTask.Current.Feedback is { } oldFeedback
                    && !oldFeedback.TestedOrder.SequenceEqual(current, StringComparer.Ordinal))
                    recoveryTask.Save(recoveryTask.Current with { CurrentOrder = current.ToArray(), Feedback = null });
                // Keep a paid saved response applicable until its own restoration below.
                if (resumeTaskId is null) Save(AiTaskPhase.Paused);
            }
            if (recoveryTask.Current.Feedback is { } savedFeedback && savedFeedback.ContextHash != FeedbackContext())
                throw new InvalidOperationException("已测试的模组资料或规则已变化，不能继续使用旧测试结果或回复。");
            if (resumeTaskId is not null && (recoveryTask.Current.Phase is AiTaskPhase.ReplySaved or AiTaskPhase.Applying)
                && recoveryTask.Current.MayApply
                // Older versions could save reorder actions for warnings alone. A completed,
                // error-free test takes precedence over those pending actions on recovery too.
                && recoveryTask.Current.Feedback is not { Passed: true })
            {
                var savedPlan = AiOrderPlanner.Plan(recoveryTask.Current.Actions, request.Settings.PermissionLevel,
                    current, catalogue, request.RetainedMods, GameVersion.Parse(request.GameVersion), rules);
                if (!savedPlan.CanApply || seen.Any(order => order.SequenceEqual(savedPlan.After, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("保存的方案未通过当前排序校验。");
                await session.ApplyAsync(savedPlan, deadline.Token);
                current = savedPlan.After.ToArray(); seen.Add(current);
                Save(AiTaskPhase.Paused, clearFeedback: true);
            }
            // A completed test is reusable only for the identical candidate after current safety/context checks.
            // Legacy checkpoints without feedback retain the previous fresh-test recovery behavior.
            initialAttempt = recoveryTask.Current.Feedback?.Attempt ?? attempts + 1;
            for (var attempt = initialAttempt; !AttemptLimitReached(attempt - 1); attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                attempts = attempt;
                MaintenanceGameReport report;
                if (recoveryTask.Current.Feedback is { } feedback)
                {
                    report = feedback.Restore();
                    Report($"已恢复第 {attempt} 次测试结果，正在继续处理。");
                }
                else
                {
                    Save(AiTaskPhase.Testing, clearFeedback: true);
                    progress?.Report(new(attempts, $"正在测试第 {attempt} 次排序", spent)
                        { PreparedOrder = Array.AsReadOnly(current.ToArray()) });
                    report = await session.TestAsync(Array.AsReadOnly(current), deadline.Token);
                    Save(AiTaskPhase.TestSaved, feedback: MaintenanceFeedback.Capture(attempt, current, report, FeedbackContext()));
                }
                deadline.Token.ThrowIfCancellationRequested();
                startupFailure = report.StartupFailure?.CopyValidated();
                hasWarnings = report.RuntimeContext is not null;
                hasVulkanConfigurationWarning = report.RuntimeContext?.HasVulkanConfigurationWarning == true;
                logPaths = report.LogPaths;
                analysis = recoveryTask.Current.Feedback?.Analysis ?? analysis;
                replyFailure = recoveryTask.Current.Feedback?.ReplyFailure ?? AiReplyFailure.None;
                if (replyFailure != AiReplyFailure.None || recoveryTask.Current.Feedback?.ServiceError is not null)
                { Stop(AiErrorStop()); break; }
                if (startupFailure?.Context.Stage == "LaunchFailed")
                {
                    Stop("未能确认本轮游戏测试启动，已停止自动排序。请检查 Steam 登录和启动确认窗口；未继续请求 AI 或试排除模组。"
                        + "\n" + startupFailure.LocalReason());
                    break;
                }
                if (startupFailure?.Context.Evidence.Any(e => e.Code == "GraphicsManifestUnavailable") == true)
                // Legacy checkpoints used a broader match. Do not turn unverified historical
                // failures into passes; fresh logs use the exact approved parser exclusions.
                { Stop(startupFailure.LocalReason()); break; }
                // A verified run without red errors is terminal, including on the last attempt.
                // Warnings remain in the evidence but cannot trigger paid requests or further edits.
                if (report.StartupVerified && (report.Errors.Count == 0 || report.ExistingErrorsOnly
                    && limits.InsertionBaselineJson is not null
                    && InsertionEvidence.Matches(recoveryTask.Current.InsertionControl, report.InsertionEvidence)))
                {
                    existingErrorsOnly = report.ExistingErrorsOnly;
                    outcome = MaintenanceOutcome.Passed;
                    message = existingErrorsOnly ? "新增 Mod 定位成功：完整测试仅重现原排序已有错误，未观察到新增红字；原有红字仍需处理。"
                        : hasVulkanConfigurationWarning ? "排序成功：除已忽略的 Vulkan 配置读取错误外，分阶段测试未发现其他错误。" + AiRuntimeContext.VulkanWarningText
                        : hasWarnings ? "排序成功：本轮测试无红字；黄字警告已保留，不影响成功判定。"
                        : "排序成功：本轮分阶段测试无红字；未覆盖游戏内全部内容。";
                    passed = true;
                    break;
                }
                if (!testOriginalFirst && MaintenanceFailureSignature.KnownNonOrderableRuntimeFailure(report) is { } runtimeFailure)
                {
                    nonOrderableBlockerDetected = true;
                    nonOrderableBlockerSummary = runtimeFailure;
                    var fingerprint = MaintenanceFailureSignature.Create(report);
                    var alreadyTried = fingerprint is not null
                        && trials.Any(t => t.FailureFingerprint == fingerprint);
                    var blockerTrialAlreadyAttempted = trials.Any(t => t.DiagnosticCodes
                        .Contains("GameTestBlockingFailure", StringComparer.Ordinal));
                    var blockerOwners = report.DiagnosticFacts.Where(f => f.Code == "GameTestBlockingFailure"
                            && !string.IsNullOrWhiteSpace(f.PackageId))
                        .Select(f => f.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (!alreadyTried && !blockerTrialAlreadyAttempted && blockerOwners.Length == 1
                        && await TryTrialAsync(report))
                    {
                        Report("已确认运行时阻断关联项；仅对该关联模组执行一次临时排除复测，不请求 AI，也不会自动从最终排序移出。");
                        continue;
                    }
                    Stop(alreadyTried
                        ? runtimeFailure + " 同一阻断已经做过一次定向复测，仍未解决；不再重复测试。"
                        : blockerTrialAlreadyAttempted
                            ? runtimeFailure + " 本任务已经进行过一次阻断模组定向排除；不再排除其他模组。"
                        : blockerOwners.Length != 1
                            ? runtimeFailure + " 阻断模组未能唯一确认；不进行猜测性排除。"
                        : runtimeFailure);
                    break;
                }
                if (nonOrderableBlockerDetected)
                { Stop("已针对明确的运行时阻断完成一次定向复测，但仍有其他未解决错误；按安全边界不再请求 AI 或排除其他模组。原排序已恢复，请先更新或处理阻断模组及剩余兼容问题。"); break; }
                if (startupFailure is null && ((!testOriginalFirst && !report.StartupVerified && !report.CanDiagnose) || report.Errors.Count == 0))
                { Stop("游戏未完成启动验证，不能将空日志判定为通过。"); break; }
                if (!testOriginalFirst && GameVersion.Parse(request.GameVersion) is { } cycleVersionForReport)
                {
                    var legacyOwners = OrderingCycleTrials.Associated(report, catalogue, current, cycleVersionForReport);
                    var related = OrderingCycleTrials.Related(cycleGroups, current, legacyOwners);
                    if (related.Count > 0)
                    {
                        if (AttemptLimitReached(attempts))
                        { Stop("循环组出现关联红字，但测试次数已用尽；未应用无法复测的换位或试排除。"); break; }
                        var swapped = false;
                        foreach (var candidate in OrderingCycleTrials.Swaps(related, current, legacyOwners, seen, deadline.Token))
                        {
                            var swapPlan = AiOrderPlanner.PreparedOrder(current, candidate, catalogue, request.RetainedMods,
                                cycleVersionForReport, rules, []);
                            if (!swapPlan.CanApply) continue;
                            // Journal the target before touching the private test configuration.
                            // Recovery rebuilds it with the same trusted planner and no AI request.
                            recoveryTask.Save(recoveryTask.Current with { Phase = AiTaskPhase.Paused,
                                CurrentOrder = candidate.ToArray(), SeenOrders = seen.Append(candidate).ToArray(),
                                Feedback = null, MayApply = false, Actions = [], PendingConflict = null });
                            await session.ApplyAsync(swapPlan, deadline.Token);
                            current = candidate; seen.Add(current);
                            Save(AiTaskPhase.Paused, clearFeedback: true);
                            Report("本轮红字关联到循环组，正在交换组内位置复测："
                                + string.Join("、", related.SelectMany(group => group).Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Select(id => ModDisplayName.Resolve(catalogue, cycleVersionForReport, id)))
                                + "。保留模组及安装文件，尚未确认报错解决。");
                            swapped = true;
                            break;
                        }
                        if (swapped) continue;
                        if (await TryTrialAsync(report, related.SelectMany(group => group).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
                            continue;
                        Stop("循环组的可用换位未解决红字，且没有进一步满足保留及依赖保护的单项试排除；已保留日志，未将剩余错误判定为通过。");
                        break;
                    }
                }
                var failureFingerprint = MaintenanceFailureSignature.Create(report);
                if (!testOriginalFirst && recoveryTask.Current.Feedback is not { ReplyReceived: true }
                    && MaintenanceErrorMemory.Covered(report, recoveryTask.Current.DiagnosedErrors))
                {
                    // Reuse analysis only after a trial actually removed an observed error.
                    // An unchanged set cannot justify walking through incidental stack owners.
                    var previous = trials.LastOrDefault()?.ErrorSignatures ?? [];
                    if (trials.Length == 0 || previous.Length > report.ErrorSignatures.Length
                        && report.ErrorSignatures.All(s => previous.Contains(s, StringComparer.Ordinal)))
                    {
                        Report(trials.Length == 0
                            ? "重排后仍有已分析的错误；复用诊断，核对一次本地试排除方案，不重复请求 AI。"
                            : "本轮剩余错误已分析，且复测确认部分错误消失；复用诊断，继续核对本地试排除候选，不重复请求 AI。");
                        if (await TryTrialAsync(report)) continue;
                    }
                    Stop("复测仍有已分析的错误，未找到进一步有效且满足保护规则的调整；已停止重复请求并保留日志。剩余红字尚未解决。");
                    break;
                }
                if (!testOriginalFirst && recoveryTask.Current.Feedback is not { ReplyReceived: true } && failureFingerprint is not null
                    && recoveryTask.Current.DiagnosedFailures.Contains(failureFingerprint, StringComparer.Ordinal))
                {
                    // One failed reorder may still justify a locally evidenced exclusion. A failed
                    // exclusion for this same error does not justify removing unrelated mods in turn.
                    if (!trials.Any(t => t.FailureFingerprint == failureFingerprint) && await TryTrialAsync(report)) continue;
                    Stop("复测仍停在相同错误，上一轮调整未解决阻塞；已停止重复 AI 请求和无依据的连续排除。请查看本轮日志与诊断。");
                    break;
                }
                if (recoveryTask.Current.Feedback is { ReplyReceived: true })
                {
                    if (recoveryTask.Current.LastUsage?.Output > (recoveryTask.Current.LastRequestOutputLimit ?? limits.OutputTokensPerRequest))
                        Stop("服务未遵守输出上限，已停止；恢复不会绕过用量限制。");
                    else if (await TryTrialAsync(report)) continue;
                    else Stop("上轮 AI 回复已保存，但没有通过校验的待执行调整；已停止，避免重复付费请求。");
                    break;
                }
                // Even the final failed launch deserves a diagnosis, but cannot authorize an untestable change.
                if (testOriginalFirst && GameVersion.Parse(request.GameVersion) is { } graphVersion)
                {
                    cycleGroups = new LoadOrderSorter().Sort(catalogue, current, graphVersion, graphVersion, rules,
                        deferAdvisoryCycles: true).DeferredCycles;
                    associated = OrderingCycleTrials.Associated(report, catalogue, current, graphVersion, includeVerifiedBlocker: true);
                }
                request = request with
                {
                    CurrentOrder = current,
                    StartupContext = startupFailure?.Context,
                    RuntimeContext = report.RuntimeContext,
                    LogContext = report.LogContext,
                    Diagnostics = baseDiagnostics.Concat(report.Errors.Take(100)).ToArray(),
                    DiagnosticFacts = baseFacts.Concat(report.DiagnosticFacts.Count > 0 ? report.DiagnosticFacts.Take(100)
                        : report.Errors.Count > 0 ? [new AiDiagnosticFact("GameLogError", "")] : [])
                        .Concat(testOriginalFirst ? cycleGroups.SelectMany(g => g).Select(id => new AiDiagnosticFact("OrderingCycleDeferred", id)) : []).Distinct().ToArray()
                };
                var outputLimit = AiOutputBudget.Next(limits, recoveryTask.Current.LastRequestOutputLimit,
                    recoveryTask.Current.OutputBudgetExhausted);
                if (outputLimit is null)
                {
                    replyFailure = AiReplyFailure.OutputLimit;
                    Stop(AiErrorStop());
                    break;
                }
                if (outputLimit > (recoveryTask.Current.LastRequestOutputLimit ?? limits.OutputTokensPerRequest))
                    Report($"上次 AI 输出被截断，下一轮输出额度增至 {outputLimit} token；仍遵守总用量预算。");
                request = request with { OutputTokenLimit = outputLimit };
                var estimate = estimator.Estimate(request, outputLimit.Value);
                if (limits.WouldExceedTokenLimit((long)spent + unknownReserve + estimate.Upper))
                { Stop("预计下一次请求可能超过 token 预算，已停止。预算为估算控制，实际计数以服务返回为准。"); break; }
                Report(estimate.Description);
                using var requestSlot = tasks.AcquireRequestSlot(recoveryTask.Id);
                requests++;
                requestInFlight = true;
                pendingReserve = estimate.Upper;
                Save(AiTaskPhase.RequestPending);
                progress?.Report(new(attempts, startupFailure is null ? "本轮检测到报错，正在等待 AI 分析并调整排序…"
                    : "本轮测试未通过，正在等待 AI 分析失败阶段和当轮日志线索…",spent) { AwaitingAi=true });
                AiOrderPlan? plan = null;
                var reply = await AiUsageTracking.SendAsync(recoveryTask, client, request, deadline.Token,
                    (checkpoint, response) => CommitReply(checkpoint, response, report, out plan));
                requestInFlight = false;
                spent = recoveryTask.Current.ReportedTokens;
                unknown = recoveryTask.Current.UnknownUsage;
                unknownReserve = recoveryTask.Current.ReservedTokens;
                pendingReserve = 0;
                var usage = reply.Usage is { Valid: true } validUsage ? validUsage : null;
                if (usage is null) Report("服务未回报有效 token 用量，已保留预估占用，未视为零消耗。");
                startupFailure = recoveryTask.Current.Feedback?.StartupFailure;
                replyFailure = recoveryTask.Current.Feedback?.ReplyFailure ?? AiReplyFailure.None;
                analysis = reply.Succeeded && reply.Response is { } liveResponse
                    ? BoundedAnalysis(liveResponse) : recoveryTask.Current.Feedback?.Analysis;
                var replyText = replyFailure == AiReplyFailure.None && reply.Succeeded && reply.Response is { } displayResponse
                    ? BoundedAnalysis(displayResponse)
                    : ReplyFailureDescription()
                        + "\n已停止自动排序，不再进行本地换位、试排除或后续请求。请检查 AI 设置及错误原因后重新开始。";
                progress?.Report(new(attempts, replyFailure == AiReplyFailure.None
                    ? "已收到 AI 响应，正在校验。" : "AI 返回错误，已停止自动排序。", spent)
                    { ReportText = replyText });
                // A valid zero-input report still counts toward the budget, but cannot calibrate input size.
                if (usage is { Input: > 0 }) estimator.Observe(estimate, usage);
                if (limits.TokenLimitReached((long)spent + unknownReserve) || usage?.Output > request.OutputTokenLimit)
                { Stop("已回报用量与未回报预估已达到预算，或服务未遵守输出上限，已停止后续请求。"); break; }
                deadline.Token.ThrowIfCancellationRequested();
                if (!reply.Succeeded || reply.Response is null || replyFailure != AiReplyFailure.None || reply.ServiceError is not null)
                {
                    Stop(AiErrorStop());
                    break;
                }
                if (testOriginalFirst && !SafeToAct(report))
                { Stop("已完成 AI 分析；当前证据不允许通过排序调整或移出模组，原排序及日志已保留。"); break; }
                if (cyclePlanBlocked)
                { Stop("AI 方案改变了与本轮红字无关联的循环组内顺序；未执行该方案，也未移出模组。"); break; }
                if (startupFailure is not null)
                {
                    if (!AllowsStartupActions(report, reply.Response))
                    {
                        if (await TryTrialAsync(report)) continue;
                        Stop(startupFailure.Context.EnvironmentFailure || startupFailure.Diagnosis?.Category == "environment"
                            ? "本轮失败涉及电脑或系统环境，已停止自动调整；请查看原因、建议及本轮日志路径。"
                            : "暂未找到可安全自动处理的失败原因，已停止；请携带日志寻求进一步帮助。");
                        break;
                    }
                    if (AttemptLimitReached(attempt)) { Stop("已完成本轮失败分析，但测试次数已用尽；未应用无法复测的调整。"); break; }
                }
                if (AttemptLimitReached(attempt))
                { Stop("本轮报错已完成分析，但测试次数已用尽；未应用无法复测的调整。"); break; }
                if (plan is null) throw new InvalidOperationException("AI 排序方案未完成校验。");
                if (testOriginalFirst && !plan.CanApply && reply.Response.Actions.Count == 0
                    && failureFingerprint is not null && failureFingerprint == lastSwapFailure && lastSwappedOwners.Length > 0)
                {
                    var suggested = reply.Response.Conflicts.SelectMany(c => c.PackageIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var eligible = lastSwappedOwners.Intersect(associated, StringComparer.OrdinalIgnoreCase).Where(suggested.Contains).ToArray();
                    if (eligible.Length > 0 && await TryTrialAsync(report, eligible))
                    { lastSwapFailure = null; lastSwappedOwners = []; continue; }
                }
                // Prefer a new valid reorder; otherwise try a locally associated, reversible exclusion.
                // Only explicit player choices can publish those removals to their personal order.
                if ((!plan.CanApply || seen.Any(o => o.SequenceEqual(plan.After, StringComparer.OrdinalIgnoreCase)))
                    && await TryTrialAsync(report)) continue;
                if (!plan.CanApply && reply.Response.Actions.Count == 0 && reply.Response.Conflicts.Count > 0
                    && !testOriginalFirst && trials.Length == 0 && session is not IMaintenanceTrialSession
                    && session is IMaintenanceConflictResolver conflicts)
                {
                    var selectedPlan = await conflicts.ResolveAsync(reply.Response, report, current, recoveryTask, deadline.Token);
                    if (selectedPlan is not null)
                    {
                        approvals = recoveryTask.Current.ConflictApprovals;
                        Save(AiTaskPhase.Applying);
                        await session.ApplyAsync(selectedPlan, deadline.Token);
                        current = selectedPlan.After.ToArray();
                        seen.Add(current);
                        Save(AiTaskPhase.Paused, clearFeedback: true);
                        Report("已按选择移出排序，正在准备复测；尚未确认报错解决。");
                        continue;
                    }
                }
                if (!plan.CanApply) { Stop("AI 未提出有效调整。"); break; }
                if (seen.Any(order => order.SequenceEqual(plan.After, StringComparer.OrdinalIgnoreCase)))
                { Stop("检测到重复排序，已停止循环重试。"); break; }
                Save(AiTaskPhase.Applying);
                await session.ApplyAsync(plan, deadline.Token);
                if (testOriginalFirst)
                {
                    // Only a retest of an isolated within-group reorder can justify escalation.
                    var changedGroup = cycleGroups.FirstOrDefault(g => OrderingCycleTrials.Changed(g, current, plan.After));
                    var members = changedGroup?.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var isolated = members is not null && current.Length == plan.After.Count && current.Zip(plan.After)
                        .All(p => p.First == p.Second || members.Contains(p.First) && members.Contains(p.Second));
                    lastSwappedOwners = isolated ? associated.Where(members!.Contains).ToArray() : [];
                    lastSwapFailure = isolated ? failureFingerprint : null;
                }
                current = plan.After.ToArray();
                seen.Add(current);
                Save(AiTaskPhase.Paused, clearFeedback: true);
            }
        }
        catch (MaintenancePauseException ex) { Stop(ex.Message); }
        catch (OperationCanceledException)
        {
            // A cancelled/timed-out HTTP request may have been billed even without a reply.
            unknown |= requestInFlight;
            outcome = token.IsCancellationRequested ? MaintenanceOutcome.Cancelled : MaintenanceOutcome.Stopped;
            message = "已取消自动维护。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or InvalidOperationException or System.Text.Json.JsonException
            or System.ComponentModel.Win32Exception or TimeoutException or NotSupportedException or HttpRequestException)
        {
            outcome = MaintenanceOutcome.Failed;
            if (requestInFlight)
            {
                replyFailure = AiReplyFailure.ServiceFailure;
                message = AiErrorStop();
            }
            else message = "自动维护失败：" + ex.Message;
            Diagnostics.DiagnosticEventLog.Current?.Record("automatic-sorting", "failure-location",
                $"{ex.GetType().Name}: {ex.StackTrace}");
        }
        finally
        {
            unknown |= requestInFlight;
            try
            {
                if (began)
                {
                    // Cancellation must not cancel recovery too. The adapter must fail visibly if recovery times out.
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await session!.FinishAsync(passed && trials.Length == 0, recovery.Token);
                    restored = !passed || trials.Length > 0;
                    if (trials.Length > 0)
                        message += passed
                            ? " 已恢复原排序；试排除后的组合通过测试，需由玩家确认是否移出这些模组。"
                            : " 已恢复原排序；临时排除项已汇总，剩余组合尚未通过验证。";
                    if (passed && nonOrderableBlockerSummary is not null)
                        message += "\n" + nonOrderableBlockerSummary;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException
                or System.ComponentModel.Win32Exception or TimeoutException)
            {
                outcome = MaintenanceOutcome.RecoveryRequired;
                message = "维护结束时未能确认恢复，已停止；请核对恢复记录。";
            }
            finally
            {
                try
                {
                    if (recoveryTask is not null && persistFinal)
                        Save(outcome == MaintenanceOutcome.Passed ? AiTaskPhase.Completed
                            : outcome == MaintenanceOutcome.Cancelled ? AiTaskPhase.Cancelled
                            : recoveryTask.Current.MayApply ? recoveryTask.Current.Phase : AiTaskPhase.Paused);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { outcome = MaintenanceOutcome.RecoveryRequired; message = "维护记录未能保存；保留上次检查点，禁止自动重复请求。"; }
                finally
                {
                    try { usagePeriod?.Dispose(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    { outcome = MaintenanceOutcome.RecoveryRequired; message = "任务结束时间未能保存，已保留原用量凭证。"; }
                    finally
                    {
                        if (recoveryTask is not null) approvals = recoveryTask.Current.ConflictApprovals;
                        recoveryTask?.Dispose(); Volatile.Write(ref active, 0);
                    }
                }
            }
        }
        return Result(outcome, message);

        void Report(string text) => progress?.Report(new(attempts, text, spent)
            { PreparedOrder = Array.AsReadOnly(current.ToArray()) });
        string FeedbackContext()
        {
            var version = GameVersion.Parse(request.GameVersion);
            // Include downloaded dependencies, without invalidating a task for unrelated inactive installs.
            var testedCatalogue = new ModCatalogue(current.SelectMany(id => catalogue.Resolve(id, version).Candidates)
                .Distinct().ToArray());
            return AiOrderPlanner.Fingerprint(current, testedCatalogue, request.RetainedMods, rules, version);
        }
        AiTaskCheckpoint CommitReply(AiTaskCheckpoint checkpoint, AiClientResult reply, MaintenanceGameReport report, out AiOrderPlan? plan)
        {
            var usage = reply.Usage is { Valid: true } validUsage ? validUsage : null;
            var nextSpent = checked(checkpoint.ReportedTokens + (usage?.Total ?? 0));
            var nextReserve = checked(unknownReserve + (usage is null ? pendingReserve : 0));
            var version = GameVersion.Parse(request.GameVersion);
            var actions = reply.Succeeded && reply.Response is { } response && reply.Failure == AiReplyFailure.None && reply.ServiceError is null
                ? AiChatCompatibility.NormalizePlanActions(request.Settings.Model, response.Actions, current, catalogue, version) : null;
            var validated = actions is null ? null : AiOrderPlanner.Plan(actions, AiPermissionLevel.Level1OrderAndRequiredDependencies,
                current, catalogue, request.RetainedMods, version, rules);
            plan = validated;
            cyclePlanBlocked = false;
            if (testOriginalFirst && validated is { CanApply: true })
            {
                IEnumerable<string[]> groups = cycleGroups;
                // Adding a missing prerequisite can reveal a cycle the incomplete graph could not inspect.
                if (current.Length != validated.After.Count)
                {
                    var previous = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var updated = new LoadOrderSorter().Validate(catalogue, validated.After, version, version, rules,
                        deferAdvisoryCycles: true).DeferredCycles;
                    groups = groups.Concat(updated.Select(g => g.Where(previous.Contains).ToArray()));
                }
                cyclePlanBlocked = groups.Any(g => OrderingCycleTrials.Changed(g, current, validated.After)
                    && !g.Intersect(associated, StringComparer.OrdinalIgnoreCase).Any());
            }
            var canApply = validated is { CanApply: true }
                && (!testOriginalFirst || SafeToAct(report) && !cyclePlanBlocked)
                && !limits.TokenLimitReached((long)nextSpent + nextReserve) && (usage is null || usage.Output <= request.OutputTokenLimit)
                && AllowsStartupActions(report, reply.Response!)
                && report.Errors.Count > 0
                && !AttemptLimitReached(attempts)
                && !seen.Any(order => order.SequenceEqual(validated.After, StringComparer.OrdinalIgnoreCase));
            RuntimeConflictEvidence? conflict = null;
            if (!testOriginalFirst && session is not IMaintenanceTrialSession && validated is { CanApply: false, Rejected.Count: 0 }
                && reply.Response is { Actions.Count: 0 } conflicts && AllowsStartupActions(report, conflicts)
                && !AttemptLimitReached(attempts)
                && version is not null && session is IMaintenanceConflictResolver)
                conflict = conflicts.Conflicts.Select(c => RuntimeConflictResolution.FromReport(c, report,
                    catalogue, current, request.RetainedMods, version)?.RuntimeEvidence).FirstOrDefault(c => c is not null);
            var signature = MaintenanceFailureSignature.Create(report);
            var failureKind = reply.ServiceError is not null ? AiReplyFailure.ServiceFailure
                : reply.Failure != AiReplyFailure.None ? reply.Failure
                : actions is null ? AiReplyFailure.Unspecified
                : validated is { Rejected.Count: > 0 } && !(testOriginalFirst && actions.Count == 0) ? AiReplyFailure.InvalidPlan : AiReplyFailure.None;
            return checkpoint with { Phase = AiTaskPhase.ReplySaved, ReportedTokens = nextSpent,
                DiagnosedFailures = signature is null ? checkpoint.DiagnosedFailures
                    : checkpoint.DiagnosedFailures.Append(signature).Distinct(StringComparer.Ordinal).ToArray(),
                DiagnosedErrors = reply.Succeeded && reply.Response is not null
                    ? checkpoint.DiagnosedErrors.Concat(report.ErrorSignatures).Distinct(StringComparer.Ordinal).Take(20000).ToArray()
                    : checkpoint.DiagnosedErrors,
                OutputBudgetExhausted = reply.Failure == AiReplyFailure.OutputLimit,
                UnknownUsage = unknown || usage is null, ReservedTokens = nextReserve, LastUsage = usage,
                Feedback = checkpoint.Feedback! with { ReplyReceived = true,
                    ServiceError = reply.ServiceError?.CopyValidated(),
                    ReplyFailure = failureKind,
                    PlanIssue = failureKind == AiReplyFailure.InvalidPlan ? validated?.Issue ?? AiPlanIssue.None : AiPlanIssue.None,
                    // Display-only summary survives a recovery checkpoint; execution still
                    // requires separately validated actions and technical evidence.
                    Analysis = reply.Succeeded && reply.Response is { } text
                        ? BoundedAnalysis(text) : null,
                    StartupFailure = report.StartupFailure is { } failure ? failure with
                    { Diagnosis = reply.Succeeded ? reply.Response?.StartupDiagnosis?.CopyValidated() : null } : null }, MayApply = canApply,
                Actions = canApply ? actions!.Select(a => a with { TargetPath = null, Reason = null }).ToArray() : [],
                PendingConflict = conflict?.Copy() };
        }
        void Save(AiTaskPhase phase,
            bool clearFeedback = false, MaintenanceFeedback? feedback = null)
        {
            if (recoveryTask is null) return;
            recoveryTask.Save(recoveryTask.Current with { Phase = phase, CurrentOrder = current.ToArray(),
                LastRequestOutputLimit = phase == AiTaskPhase.RequestPending ? request.OutputTokenLimit : recoveryTask.Current.LastRequestOutputLimit,
                SeenOrders = seen.Select(order => order.ToArray()).ToArray(), Attempts = attempts, Requests = requests,
                ReportedTokens = spent, UnknownUsage = unknown || requestInFlight,
                ReservedTokens = checked(unknownReserve + (requestInFlight ? pendingReserve : 0)),
                Message = message,
                Feedback = clearFeedback ? null : feedback ?? recoveryTask.Current.Feedback,
                MayApply = (phase is AiTaskPhase.ReplySaved or AiTaskPhase.Applying) && recoveryTask.Current.MayApply,
                CandidatePendingApply = phase == AiTaskPhase.Completed && session is IMaintenancePreparation });
        }
        void Stop(string text) { outcome = MaintenanceOutcome.Stopped; message = text
            + (trialStopReason is null ? "" : "\n" + trialStopReason); }
        string ReplyFailureDescription() => (replyFailure == AiReplyFailure.OutputLimit ? null
            : recoveryTask?.Current.Feedback?.ServiceError?.DisplayText)
            ?? AiReplyFailureText.Describe(replyFailure, recoveryTask?.Current.Feedback?.PlanIssue ?? AiPlanIssue.None);
        string AiErrorStop() => ReplyFailureDescription()
            + (recoveryTask?.Current.LastUsage?.Output > (recoveryTask?.Current.LastRequestOutputLimit ?? limits.OutputTokensPerRequest)
                ? " 服务未遵守输出上限。" : "")
            + " 已停止自动排序，不再进行本地换位、试排除、游戏复测或后续 AI 请求。原排序及本轮日志已保留；请检查 AI 设置及错误原因后重新开始。已回报用量按实记录，未回报用量仍记为未知，不自动重复请求。";
        async Task<bool> TryTrialAsync(MaintenanceGameReport report, IReadOnlyList<string>? cycleMembers = null)
        {
            deadline.Token.ThrowIfCancellationRequested();
            trialStopReason = null;
            if (testOriginalFirst && cycleMembers is null) return false;
            if (session is not IMaintenanceTrialSession trialSession || AttemptLimitReached(attempts)
                || recoveryTask is null || requestInFlight || GameVersion.Parse(request.GameVersion) is not { } version)
            {
                trialStopReason = AttemptLimitReached(attempts) ? "本次设置的测试次数已用尽，未执行无法复测的试排除。"
                    : "当前会话尚不满足可恢复试排除的执行条件。";
                return false;
            }
            var candidates = TrialExclusionPlanning.Candidates(report, catalogue, current, request.RetainedMods, version, attempts, cycleMembers);
            TrialExclusion? trial = null;
            AiOrderPlan? plan = null;
            foreach (var candidate in candidates)
            {
                if (testOriginalFirst && (candidate.RemovedIds.Length != 1 || !cycleMembers!.Contains(candidate.SelectedId, StringComparer.OrdinalIgnoreCase))) continue;
                var remaining = current.Except(candidate.RemovedIds, StringComparer.OrdinalIgnoreCase).ToArray();
                var sorted = testOriginalFirst ? new LoadOrderSorter().Validate(catalogue, remaining, version, version, rules, deferAdvisoryCycles: true)
                    : new LoadOrderSorter().Sort(catalogue, remaining, version, version, rules, deferAdvisoryCycles: true);
                if (!sorted.Complete) continue;
                var proposed = TrialExclusionPlanning.Plan(current, testOriginalFirst ? remaining : sorted.Order, catalogue, request.RetainedMods, version, rules, [candidate]);
                if (!proposed.CanApply || seen.Any(o => o.SequenceEqual(proposed.After, StringComparer.OrdinalIgnoreCase))) continue;
                trial = candidate; plan = proposed; break;
            }
            if (trial is null || plan is null)
            {
                trialStopReason = !report.CanDiagnose ? "本轮日志或实际加载清单未通过完整性校验，无法据此试排除。"
                    : report.StartupFailure?.Context.EnvironmentFailure == true ? "检测到运行环境错误，试排除不能修复该环境问题。"
                    : candidates.Count == 0 ? "未找到可排除的本轮关联模组；关联项可能受到保留或依赖保护。"
                    : "已检查全部关联候选，均违反排序约束或已测试过，未重复执行。";
                return false;
            }
            var batch = cycleMembers is null
                ? TrialExclusionPlanning.Batch(report, catalogue, current, request.RetainedMods, version, trial) : [trial];
            if (batch.Length > 1)
            {
                var remaining = current.Except(batch.SelectMany(t => t.RemovedIds), StringComparer.OrdinalIgnoreCase).ToArray();
                var sorted = new LoadOrderSorter().Sort(catalogue, remaining, version, version, rules, deferAdvisoryCycles: true);
                var combined = sorted.Complete ? TrialExclusionPlanning.Plan(current, sorted.Order, catalogue,
                    request.RetainedMods, version, rules, batch) : null;
                if (combined is { CanApply: true } && !seen.Any(o => o.SequenceEqual(combined.After, StringComparer.OrdinalIgnoreCase)))
                    plan = combined;
                else batch = [trial];
            }
            trials = trials.Concat(batch).ToArray();
            // Persist authority before the test adapter can apply it. This is deliberately separate
            // from user approvals and cannot authorize publication to the personal game configuration.
            recoveryTask.Save(recoveryTask.Current with { TrialExclusions = trials, PendingConflict = null,
                MayApply = false, Actions = [], Message = "已记录临时试排除，等待复测；尚未确认根因。" });
            trialSession.AuthorizeTrialExclusions(trials);
            Save(AiTaskPhase.Applying);
            await session.ApplyAsync(plan, deadline.Token);
            current = plan.After.ToArray();
            seen.Add(current);
            Save(AiTaskPhase.Paused, clearFeedback: true);
            Report((batch.Length > 1 ? $"合并试排除 {batch.Length} 组独立错误关联项，一次复测：" : "临时试排除：")
                + string.Join("、", batch.SelectMany(t => t.RemovedIds).Select(id => ModDisplayName.Resolve(catalogue, version, id)))
                + "；尚未证明各项根因。结束后恢复原排序，移除仍按用户设置确认。");
            return true;
        }
        MaintenanceResult Result(MaintenanceOutcome state, string text) =>
            new(state, text, attempts, requests, spent, unknown, Array.AsReadOnly(current.ToArray()), restored)
                { TaskId = recoveryTask?.Id, ControlAttempts = recoveryTask?.Current.InsertionControlAttempts ?? 0,
                    InsertionBaselineJson = limits.InsertionBaselineJson, ConflictApprovals = Array.AsReadOnly(approvals),
                    TrialExclusions = Array.AsReadOnly(trials), StartupFailure = startupFailure,
                    ReplyFailure = replyFailure,
                    ExistingErrorsOnly = existingErrorsOnly, Analysis = analysis, HasWarnings = hasWarnings,
                    HasVulkanConfigurationWarning = hasVulkanConfigurationWarning, LogPaths = logPaths };
    }

    private static bool SafeToAct(MaintenanceGameReport report) => report.CanDiagnose
        && MaintenanceFailureSignature.KnownNonOrderableRuntimeFailure(report) is null;

    private static bool AllowsStartupActions(MaintenanceGameReport report, StructuredAiResponse response) =>
        report.StartupFailure is null || report.CanDiagnose && report.StartupFailure.Context.CanAdjustOrder
            && response.StartupDiagnosis?.Category == "mod";

    private static string BoundedAnalysis(StructuredAiResponse response)
    {
        var text = string.Join(Environment.NewLine, new[] { response.Summary }.Concat(response.Warnings));
        return text[..Math.Min(text.Length, 10000)];
    }
}
