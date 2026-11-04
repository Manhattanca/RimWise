using System.IO;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Integration.AI;
using RimWise.Integration.Databases;
using RimWise.Integration.Configuration;
using RimWise.Integration.Game;
using RimWise.Integration.Safety;

namespace RimWise.Desktop.Services;

/// <summary>UI boundary: snapshot inputs on the dispatcher, run the trusted loop off it,
/// then publish a tested candidate only if the editor and settings still match.</summary>
internal sealed class AutomaticSortingSession(MainViewModel editor, string directory, AiSortingSession sorting,
    IAiClient? client)
{
    private StoppedOrderContext? stoppedOrder;
    private sealed record StoppedOrderContext(AiRequest Request, AiSettings Settings, Func<GamePaths> GetPaths,
        GamePaths Paths, long Revision, ModCatalogue Catalogue, DatabaseSnapshot? Database,
        Action? SaveOrder, MaintenanceResult Result, bool ConfirmModRemoval);

    internal async Task<AutomaticMaintenanceResult> RunAsync(Func<GamePaths> getPaths, AiSettings settings,
        MaintenanceLimits limits, IProgress<MaintenanceProgress> progress, IProgress<OrdinaryGameProgress> gameProgress,
        IProgress<AiResponseUpdate> responseProgress, CancellationToken token, string? resumeId = null,
        Func<DeclaredConflict, CancellationToken, Task<string[]?>>? chooseConflict = null,
        Func<Action>? prepareSave = null,
        Func<MaintenanceResult, CancellationToken, Task<bool>>? confirmTrials = null)
    {
        stoppedOrder = null;
        if (AiClient.ConfigurationProblem(settings.Normalize()) is { } configurationProblem)
            throw new InvalidOperationException(configurationProblem);
        if (sorting.ReadinessProblem is { } problem) throw new InvalidOperationException(problem);
        var paths=getPaths();
        var revision=editor.ContentRevision;
        var catalogue=editor.CurrentCatalogue!;
        var rules=InsertionScope.Rules(editor.CustomRules, limits.InsertionBaselineJson).ToArray();
        var request=AiSortingSession.CreateRequest(editor,settings,sorting.Secrets.Read(settings.SecretName))
            with { ResponseProgress=responseProgress, OutputTokenLimit=limits.OutputTokensPerRequest };
        request = request with { OrderRules = rules, RetainedMods = request.RetainedMods
            .Concat(InsertionScope.Read(limits.InsertionBaselineJson)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        if (resumeId is null) InsertionScope.ValidateOrder(limits.InsertionBaselineJson, request.CurrentOrder);
        else
        {
            using var saved = sorting.Tasks.Open(resumeId);
            InsertionScope.ValidateOrder(limits.InsertionBaselineJson, request.CurrentOrder,
                saved.Current.ConflictApprovals.SelectMany(a => a.RemovedIds));
        }
        var version=GameVersion.Parse(request.GameVersion) ?? throw new InvalidOperationException("游戏版本未知。");
        var options=new OrdinaryGameTestOptions(paths.GameDirectory,CurrentOrderLocation.Resolve(paths),
            Path.Combine(directory,"Data","GameTests"),version) { UseFixedHelper=true };
        var saveOrder = prepareSave?.Invoke();
        using var ownedClient=client is null ? new AiClient() : null;
        DatabaseSnapshot? database = null;
        var result=await Task.Run(async () =>
        {
            var databases = new DatabaseStore(directory);
            string? pinnedId = null;
            if (resumeId is not null)
            {
                using var saved = sorting.Tasks.Open(resumeId);
                pinnedId = saved.Current.DatabaseSnapshotId;
            }
            // Legacy paid tasks retain their original metadata-only behavior.
            database = pinnedId == "" ? null : pinnedId is null ? databases.Load().Snapshot : databases.LoadPinned(pinnedId);
            if (database is not null) databases.Preserve(database);
            request = request with { DatabaseSnapshotId = database?.Id ?? "" };
            if (resumeId is not null)
            {
                using var task=sorting.Tasks.Open(resumeId);
                request = RestorePreparationRequest(request, task.Current, catalogue, rules, version, database);
                if (task.Current.CandidatePendingApply)
                {
                    // A previous publish may have applied the editor or saved the file before a crash.
                    // Accept only the exact original or chosen publication order; validate the original context again.
                    var originalRequest = request with { CurrentOrder = task.Current.OriginalOrder };
                    IReadOnlyList<string> publicationOrder = task.Current.CurrentOrder;
                    if (task.Current.TrialDecisionRecorded && !task.Current.TrialRemovalsApproved)
                    {
                        var constraints = database?.WithRequiredDependencies(catalogue, originalRequest.CurrentOrder, version) ?? catalogue;
                        var publicationRules = database?.Rules(constraints,
                            AiOrderPlanner.ExpandRequiredOrder(originalRequest.CurrentOrder, constraints, version, allowMissing: true),
                            version, rules) ?? rules;
                        var kept = AiOrderPlanner.RetainedTrialOrder(originalRequest.CurrentOrder, publicationOrder, catalogue,
                            request.RetainedMods, version, publicationRules, task.Current.ConflictApprovals,
                            task.Current.TrialExclusions, constraints);
                        if (kept.Rejected.Count > 0) throw new InvalidDataException("保留模组后的排序未通过恢复校验。");
                        publicationOrder = kept.After;
                    }
                    InsertionScope.ValidateOrder(limits.InsertionBaselineJson, publicationOrder,
                        task.Current.ConflictApprovals.SelectMany(a => a.RemovedIds));
                    if (!request.CurrentOrder.SequenceEqual(task.Current.OriginalOrder, StringComparer.Ordinal)
                        && !request.CurrentOrder.SequenceEqual(publicationOrder, StringComparer.Ordinal))
                        throw new InvalidOperationException("当前排序与原排序及已测试结果均不同，已保留测试结果。");
                    if (task.Current.ContextHash!=AutomaticGameMaintenance.RecoveryContext(originalRequest,catalogue,rules,version,task.Current.InitialMissingIds))
                        throw new InvalidOperationException("原排序、安装资料、设置或规则已变化，已保留测试结果。");
                    request = originalRequest;
                    return new AutomaticMaintenanceResult(new(MaintenanceOutcome.Passed,"已恢复通过测试的排序，无需再次请求 AI。",
                        task.Current.Attempts,task.Current.Requests,task.Current.ReportedTokens,task.Current.UnknownUsage,
                        task.Current.CurrentOrder,task.Current.TrialExclusions.Length > 0) { TaskId=resumeId,
                            ControlAttempts=task.Current.InsertionControlAttempts, InsertionBaselineJson=limits.InsertionBaselineJson,
                            ConflictApprovals=task.Current.ConflictApprovals, TrialExclusions=task.Current.TrialExclusions,
                            TrialRemovalsApproved=task.Current.TrialRemovalsApproved,
                            TrialDecisionRecorded=task.Current.TrialDecisionRecorded,
                            HasWarnings=task.Current.Feedback?.RuntimeContext is not null,
                            ExistingErrorsOnly=task.Current.Feedback?.ExistingErrorsOnly == true,
                            LogPaths=task.Current.Feedback?.LogPaths ?? [] },null,task.Current.RequiredDownloads)
                        { DatabaseSnapshotId = database?.Id ?? "" };
                }
            }
            return await AutomaticGameMaintenance.RunAsync(client??ownedClient!,new(directory),options,paths,request,
                catalogue,rules,limits,progress,gameProgress,token,resumeId,chooseConflict,database);
        },token);
        if (result.Maintenance is { Outcome: MaintenanceOutcome.Passed, TrialExclusions.Count: > 0,
                TrialRemovalsApproved: false, TrialDecisionRecorded: false })
        {
            // Tests have ended and the adapter restored the original before asking the player.
            // Merely obtaining a green test result never grants permission to remove from their order.
            if (token.IsCancellationRequested || revision != editor.ContentRevision || getPaths() != paths
                || sorting.SettingsStore.Load() != settings || !ReferenceEquals(catalogue, editor.CurrentCatalogue))
                return result with { Maintenance = result.Maintenance with { Message = "试排除结果已保留；当前清单或设置已变化，原排序保持不变。" } };
            if (limits.ConfirmModRemoval && confirmTrials is null) return result;
            var approved = !limits.ConfirmModRemoval || await confirmTrials!(result.Maintenance, token);
            token.ThrowIfCancellationRequested();
            if (result.Maintenance.TaskId is { } id)
            {
                using var task = sorting.Tasks.Open(id);
                if (!task.Current.CandidatePendingApply || task.Current.TrialExclusions.Length == 0
                    || !task.Current.CurrentOrder.SequenceEqual(result.Maintenance.LastAttemptedOrder, StringComparer.Ordinal))
                    throw new InvalidDataException("试排除结果已经变化，不能确认移出。");
                // Persist either decision before saving. A failed save must resume the decision,
                // rather than asking again or treating a declined removal as a new test task.
                task.Save(task.Current with { TrialRemovalsApproved = approved, TrialDecisionRecorded = true,
                    CandidatePendingApply = true,
                    Message = approved ? (limits.ConfirmModRemoval ? "玩家已确认移除模组，等待保存。" : "按自动移出设置记录已验证方案，等待保存。") : "玩家选择保留模组，等待保存已完成的排序调整。" });
            }
            result = result with { Maintenance = result.Maintenance with { TrialRemovalsApproved = approved, TrialDecisionRecorded = true,
                Message = approved ? result.Maintenance.Message : "保留试排除模组，继续保存已完成的排序调整。" } };
        }
        if (result.Maintenance.Outcome != MaintenanceOutcome.Passed)
        {
            RememberStoppedOrder(result, request, settings, getPaths, paths, revision, catalogue, saveOrder, database, limits.ConfirmModRemoval);
            return result;
        }
        return PublishCandidate(result, request, settings, getPaths, paths, revision, catalogue, token, saveOrder, database);
    }

    internal void RememberStoppedOrder(AutomaticMaintenanceResult result, AiRequest request, AiSettings settings,
        Func<GamePaths> getPaths, GamePaths paths, long revision, ModCatalogue catalogue,
        Action? saveOrder = null, DatabaseSnapshot? database = null, bool confirmModRemoval = true) =>
        stoppedOrder = new(request, settings, getPaths, paths, revision, catalogue, database, saveOrder, result.Maintenance, confirmModRemoval);

    internal bool NeedsStoppedRemovalConfirmation(AutomaticMaintenanceResult result) =>
        stoppedOrder is { ConfirmModRemoval: true } && result.Maintenance.TrialExclusions.Count > 0;

    internal AiOrderPlan? LastOrderChoice(AutomaticMaintenanceResult result, bool approveTrialRemovals = false)
    {
        var state = stoppedOrder;
        if (state is null || !NeedsLastOrderChoice(result.Maintenance)
            || result.Maintenance.TaskId != state.Result.TaskId
            || !result.Maintenance.LastAttemptedOrder.SequenceEqual(state.Result.LastAttemptedOrder, StringComparer.Ordinal)) return null;
        if (sorting.ReadinessProblem is not null || state.Revision != editor.ContentRevision
            || !ReferenceEquals(state.Catalogue, editor.CurrentCatalogue) || sorting.SettingsStore.Load() != state.Settings
            || state.GetPaths() != state.Paths)
            throw new InvalidOperationException("当前排序、安装资料或设置已变化，未覆盖人工修改；最后测试顺序保留在任务记录中。");
        var active = result.Scan?.Catalogue ?? state.Catalogue;
        var version = GameVersion.Parse(state.Request.GameVersion) ?? throw new InvalidOperationException("游戏版本未知。");
        var constraints = state.Database?.WithRequiredDependencies(active, state.Request.CurrentOrder, version) ?? active;
        var target = result.Maintenance.LastAttemptedOrder;
        InsertionScope.ValidateOrder(result.Maintenance.InsertionBaselineJson, target,
            result.Maintenance.ConflictApprovals.SelectMany(a => a.RemovedIds));
        var rules = state.Database?.Rules(constraints, target, version, state.Request.OrderRules) ?? state.Request.OrderRules;
        // Keeping a stopped order is not consent to publish its private trial removals.
        if (NeedsStoppedRemovalConfirmation(result) && !approveTrialRemovals)
        {
            rules = state.Database?.Rules(constraints,
                AiOrderPlanner.ExpandRequiredOrder(state.Request.CurrentOrder, constraints, version, allowMissing: true),
                version, state.Request.OrderRules) ?? state.Request.OrderRules;
            return AiOrderPlanner.RetainedTrialOrder(state.Request.CurrentOrder, target, active, state.Request.RetainedMods,
                version, rules, result.Maintenance.ConflictApprovals, result.Maintenance.TrialExclusions, constraints);
        }
        return result.Maintenance.TrialExclusions.Count > 0
            ? AiOrderPlanner.ConfirmedTrialOrder(state.Request.CurrentOrder, target, active, state.Request.RetainedMods,
                version, rules, result.Maintenance.ConflictApprovals, result.Maintenance.TrialExclusions, true, constraints)
            : AiOrderPlanner.PreparedOrder(state.Request.CurrentOrder, target, active, state.Request.RetainedMods,
                version, rules, result.Maintenance.ConflictApprovals, constraints);
    }

    internal static bool NeedsLastOrderChoice(MaintenanceResult result) =>
        result.Outcome is MaintenanceOutcome.Cancelled or MaintenanceOutcome.Failed or MaintenanceOutcome.Stopped
        && result.OriginalRestored && result.Attempts > 0 && result.LastAttemptedOrder.Count > 0;

    internal AutomaticMaintenanceResult ResolveLastOrder(AutomaticMaintenanceResult result, bool keep, bool approveTrialRemovals = false)
    {
        var plan = LastOrderChoice(result, approveTrialRemovals) ?? throw new InvalidOperationException("没有可保留的最后测试顺序。");
        var state = stoppedOrder!;
        if (!keep)
        {
            SaveStoppedDecision(result, "restore");
            stoppedOrder = null;
            return result with { LastOrderDecisionRecorded = true, Maintenance = result.Maintenance with
                { Message = result.Maintenance.Message + "\n已选择恢复原排序，未保存本次试排除或排序调整。" } };
        }
        if (plan.Rejected.Count > 0) throw new InvalidOperationException("最后顺序未通过保留项及前置校验：" + string.Join("；", plan.Rejected));
        InsertionScope.ValidateOrder(result.Maintenance.InsertionBaselineJson, plan.After,
            result.Maintenance.ConflictApprovals.SelectMany(a => a.RemovedIds));
        var active = result.Scan?.Catalogue ?? state.Catalogue;
        var version = GameVersion.Parse(state.Request.GameVersion)!;
        var constraints = state.Database?.WithRequiredDependencies(active, state.Request.CurrentOrder, version) ?? active;
        var rules = state.Database?.Rules(constraints, plan.After, version, state.Request.OrderRules) ?? state.Request.OrderRules;
        var keepTrialMods = NeedsStoppedRemovalConfirmation(result) && !approveTrialRemovals;
        var removalMessage = result.Maintenance.TrialExclusions.Count == 0 ? "" : keepTrialMods
            ? "试排除模组已放回，保留排序调整。" : "已按移出选择保存临时排除项，安装文件保留。";
        new AiAuditStore(directory).Append(new(DateTimeOffset.UtcNow, "automatic-stopped-order-choice",
            state.Settings.PermissionLevel, true, "玩家明确选择保留最后测试顺序；该组合未验证通过。" + removalMessage, plan.After.Count, []));
        if (result.Scan is not null) editor.ApplyScan(result.Scan);
        if (plan.CanApply)
        {
            editor.ValidateAiPlan(plan, state.Revision, rules, state.Request.RetainedMods);
            var journal = new ExecutionJournal(directory);
            var id = journal.Prepare(plan, state.Settings.PermissionLevel);
            editor.ApplyAiPlan(plan, state.Revision, rules, state.Request.RetainedMods);
            journal.Complete(id);
        }
        state.SaveOrder?.Invoke();
        SaveStoppedDecision(result, "keep", result.Maintenance.TrialExclusions.Count > 0 ? !keepTrialMods : null);
        stoppedOrder = null;
        return result with { LastOrderDecisionRecorded = true, LastOrderKept = true, Maintenance = result.Maintenance with
            { TrialDecisionRecorded = result.Maintenance.TrialExclusions.Count > 0,
                TrialRemovalsApproved = result.Maintenance.TrialExclusions.Count > 0 && !keepTrialMods,
                Message = result.Maintenance.Message + "\n" + removalMessage + (state.SaveOrder is null
                ? "\n已保留最后测试顺序，可撤销；尚未写入游戏，该组合未验证通过。"
                : "\n已保留并保存最后测试顺序，原配置已备份，可撤销；该组合未验证通过。") } };
    }

    private void SaveStoppedDecision(AutomaticMaintenanceResult result, string decision, bool? approvedRemovals = null)
    {
        if (result.Maintenance.TaskId is not { } id) return;
        using var task = sorting.Tasks.Open(id);
        task.Save(task.Current with { StoppedOrderDecision = decision, MayApply = false, Actions = [],
            TrialDecisionRecorded = approvedRemovals.HasValue || task.Current.TrialDecisionRecorded,
            TrialRemovalsApproved = approvedRemovals ?? task.Current.TrialRemovalsApproved });
    }

    // Preserve deterministic preparation even when a later experiment cannot pass.
    // Never publish the last speculative reorder or temporary exclusions on this path.
    internal AutomaticMaintenanceResult PublishPreparation(AutomaticMaintenanceResult result, AiRequest request,
        AiSettings settings, Func<GamePaths> getPaths, GamePaths paths, long revision,
        ModCatalogue catalogue, CancellationToken token, DatabaseSnapshot? database = null)
    {
        if (result.Maintenance.Outcome is not (MaintenanceOutcome.Stopped or MaintenanceOutcome.Failed)
            || result.PreparedOrder.Count == 0 || result.Maintenance.Attempts == 0
            || token.IsCancellationRequested || sorting.ReadinessProblem is not null
            || revision != editor.ContentRevision || !ReferenceEquals(catalogue, editor.CurrentCatalogue)
            || sorting.SettingsStore.Load() != settings || getPaths() != paths
            || result.DatabaseSnapshotId != (database?.Id ?? "") || request.DatabaseSnapshotId != (database?.Id ?? "")) return result;
        // An unsuccessful experiment never publishes removals from the original editor.
        if (request.CurrentOrder.Except(result.PreparedOrder, StringComparer.OrdinalIgnoreCase).Any()) return result;
        InsertionScope.ValidateOrder(result.Maintenance.InsertionBaselineJson, result.PreparedOrder);
        var active = result.Scan?.Catalogue ?? catalogue;
        var version = GameVersion.Parse(request.GameVersion) ?? throw new InvalidOperationException("游戏版本未知。");
        var constraints = database?.WithRequiredDependencies(active, request.CurrentOrder, version) ?? active;
        var rules = database?.Rules(constraints, result.PreparedOrder, version, request.OrderRules) ?? request.OrderRules;
        var plan = AiOrderPlanner.PreparedOrder(request.CurrentOrder, result.PreparedOrder, active,
            request.RetainedMods, version, rules, [], constraints);
        if (plan.Rejected.Count > 0) throw new InvalidDataException("初始前置与规则排序未通过复核。");
        if (result.Scan is not null) editor.ApplyScan(result.Scan);
        if (plan.CanApply)
        {
            editor.ValidateAiPlan(plan, revision, rules, request.RetainedMods);
            new AiAuditStore(directory).Append(new(DateTimeOffset.UtcNow, "automatic-preparation-apply",
                settings.PermissionLevel, true, "保留必要前置与规则排序；测试尚未通过，未写入游戏。", plan.After.Count, []));
            var journal = new ExecutionJournal(directory);
            var id = journal.Prepare(plan, settings.PermissionLevel);
            editor.ApplyAiPlan(plan, revision, rules, request.RetainedMods);
            journal.Complete(id);
        }
        return result with { PreparationApplied = true, Maintenance = result.Maintenance with
        { Message = result.Maintenance.Message.Replace("已恢复原排序", "已放回临时试排除模组", StringComparison.Ordinal)
            + "\n必要前置和规则排序已保留，可撤销；测试尚未通过，未自动保存。" } };
    }

    // Recompute the visible baseline and verify the original context before resuming.
    // A trial candidate or a manually modified order cannot stand in for preparation.
    internal static AiRequest RestorePreparationRequest(AiRequest request, AiTaskCheckpoint saved,
        ModCatalogue catalogue, IReadOnlyList<CustomOrderRule> rules, GameVersion version, DatabaseSnapshot? database)
    {
        if (request.CurrentOrder.SequenceEqual(saved.OriginalOrder, StringComparer.Ordinal)) return request;
        var original = request with { CurrentOrder = saved.OriginalOrder };
        if (saved.ContextHash != AutomaticGameMaintenance.RecoveryContext(original, catalogue, rules, version, saved.InitialMissingIds))
            throw new InvalidOperationException("原排序、安装资料、设置或规则已变化，已保留任务记录。");
        var constraints = database?.WithRequiredDependencies(catalogue, saved.OriginalOrder, version) ?? catalogue;
        var expanded = AiOrderPlanner.ExpandRequiredOrder(saved.OriginalOrder, constraints, version, allowMissing: true);
        var effectiveRules = database?.Rules(constraints, expanded, version, rules) ?? rules;
        var baseline = new LoadOrderSorter().Sort(constraints, expanded, version, version, effectiveRules, deferAdvisoryCycles: true);
        if (baseline.Complete && saved.Attempts > 0 && request.CurrentOrder.SequenceEqual(baseline.Order, StringComparer.Ordinal))
            return original;
        // Completed publications have separate original/target checks in the caller.
        if (saved.CandidatePendingApply) return request;
        throw new InvalidOperationException("当前排序与原排序及初始前置排序不同，不能继续旧任务。");
    }

    internal AutomaticMaintenanceResult PublishCandidate(AutomaticMaintenanceResult result, AiRequest request,
        AiSettings settings, Func<GamePaths> getPaths, GamePaths paths, long revision,
        ModCatalogue catalogue, CancellationToken token, Action? saveOrder = null, DatabaseSnapshot? database = null)
    {
        if (result.Maintenance.Outcome!=MaintenanceOutcome.Passed) return result;
        if (result.Maintenance.TrialExclusions.Count > 0 && !result.Maintenance.TrialRemovalsApproved
            && !result.Maintenance.TrialDecisionRecorded) return result;
        if (token.IsCancellationRequested || sorting.ReadinessProblem is not null || revision!=editor.ContentRevision
            || !ReferenceEquals(catalogue,editor.CurrentCatalogue) || sorting.SettingsStore.Load()!=settings || getPaths()!=paths)
            return result with { Maintenance=result.Maintenance with { Message="测试通过；期间编辑或设置已变化，结果已保留，请从恢复任务中处理。" } };
        if (result.DatabaseSnapshotId != (database?.Id ?? "") || request.DatabaseSnapshotId != (database?.Id ?? ""))
            throw new InvalidDataException("应用结果缺少原数据库快照。");
        var keepTrialMods = result.Maintenance.TrialExclusions.Count > 0 && !result.Maintenance.TrialRemovalsApproved;
        // Publish new dependency metadata only after verifying the original snapshot and editor.
        if (result.Scan is not null) editor.ApplyScan(result.Scan);
        var activeCatalogue=editor.CurrentCatalogue!;
        var version=GameVersion.Parse(request.GameVersion) ?? throw new InvalidOperationException("游戏版本未知。");
        var target=result.Maintenance.LastAttemptedOrder;
        var editorOrder = editor.Rows.Select(row => row.PackageId).ToArray();
        var validationCatalogue=database?.WithRequiredDependencies(activeCatalogue,request.CurrentOrder,version) ?? activeCatalogue;
        var rules=database?.Rules(validationCatalogue,target,version,request.OrderRules) ?? request.OrderRules;
        AiOrderPlan? retainedPlan = null;
        if (keepTrialMods)
        {
            // Include rules for restored rows as well as for the reduced tested combination.
            rules = database?.Rules(validationCatalogue,
                AiOrderPlanner.ExpandRequiredOrder(request.CurrentOrder, validationCatalogue, version, allowMissing: true),
                version, request.OrderRules) ?? request.OrderRules;
            retainedPlan = AiOrderPlanner.RetainedTrialOrder(editorOrder, target, activeCatalogue,
                request.RetainedMods, version, rules, result.Maintenance.ConflictApprovals,
                result.Maintenance.TrialExclusions, validationCatalogue);
            if (retainedPlan.Rejected.Count > 0) throw new InvalidOperationException("保留模组后的排序未通过当前规则复核。");
            target = retainedPlan.After;
        }
        InsertionScope.ValidateOrder(result.Maintenance.InsertionBaselineJson, target,
            result.Maintenance.ConflictApprovals.SelectMany(a => a.RemovedIds));
        if (!target.SequenceEqual(editorOrder,StringComparer.Ordinal))
        {
            var plan=retainedPlan ?? (result.Maintenance.TrialExclusions.Count == 0
                ? AiOrderPlanner.PreparedOrder(editorOrder,target,activeCatalogue,request.RetainedMods,version,rules,
                    result.Maintenance.ConflictApprovals,validationCatalogue)
                : AiOrderPlanner.ConfirmedTrialOrder(editorOrder,target,activeCatalogue,request.RetainedMods,version,rules,
                    result.Maintenance.ConflictApprovals,result.Maintenance.TrialExclusions,
                    result.Maintenance.TrialRemovalsApproved,validationCatalogue));
            if (!plan.CanApply) throw new InvalidOperationException("测试结果未通过当前规则及用户选择复核。");
            if (!target.SequenceEqual(editor.Rows.Select(row => row.PackageId), StringComparer.Ordinal))
            {
                editor.ValidateAiPlan(plan,revision,rules,request.RetainedMods);
                // Audit before editing; a failed audit leaves the tested candidate recoverable.
                new AiAuditStore(directory).Append(new(DateTimeOffset.UtcNow, "automatic-sorting-apply",
                    settings.PermissionLevel, true, keepTrialMods ? "玩家保留试排除模组，应用排序调整；完整组合未验证通过。"
                        : "地图测试通过，应用已校验的排序。", target.Count, plan.Rejected));
                var journal=new ExecutionJournal(directory);
                var journalId=journal.Prepare(plan,settings.PermissionLevel);
                editor.ApplyAiPlan(plan,revision,rules,request.RetainedMods);
                journal.Complete(journalId);
            }
            else
                new AiAuditStore(directory).Append(new(DateTimeOffset.UtcNow, "automatic-sorting-save-resume",
                    settings.PermissionLevel, true, "已恢复测试结果，继续保存。", 0, []));
        }
        else
            new AiAuditStore(directory).Append(new(DateTimeOffset.UtcNow, "automatic-sorting-unchanged",
                settings.PermissionLevel, true, keepTrialMods ? "玩家保留试排除模组，当前排序无需调整；完整组合未验证通过。"
                    : "地图测试通过，当前排序无需修改。", 0, []));
        // Reuse the existing backup/atomic-save transaction. It still expects the file captured before testing.
        // A save failure keeps the candidate pending and never launches another paid request.
        saveOrder?.Invoke();
        CompleteCandidate(result.Maintenance.TaskId, keepTrialMods ? "已保留模组并保存排序调整；完整组合未验证通过。" : "测试结果已应用到编辑清单。");
        result = result with { CandidateApplied = true };
        if (keepTrialMods) return result with { Maintenance = result.Maintenance with { Message = saveOrder is null
            ? "已保留模组和排序调整，可撤销。点击保存排序写入游戏；原有报错可能仍会出现。"
            : "已保留模组并保存排序调整，可撤销；原有报错可能仍会出现。" } };
        if (result.Maintenance.ExistingErrorsOnly) return result with { Maintenance=result.Maintenance with { Message=
            "新增 Mod 已定位；完整测试仅重现原排序中的错误，未观察到新增红字。原有模组保留，仅按需求调整相关位置。"
            + (saveOrder is null ? "排序已应用，点击保存排序写入游戏。" : "排序已保存，原配置已备份。") } };
        return result with { Maintenance=result.Maintenance with { Message=saveOrder is null
            ? "已排序完成：本轮测试无红字，排序已应用，可撤销。点击保存排序写入游戏。"
            : "已排序完成：本轮测试无红字，已自动保存；原配置已备份，可撤销。" } };
    }

    private void CompleteCandidate(string? id, string message)
    {
        if (id is null) return;
        using var task = sorting.Tasks.Open(id);
        task.Save(task.Current with { CandidatePendingApply = false, Message = message });
    }
}
