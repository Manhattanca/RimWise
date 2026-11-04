using System.IO;
using System.Text.Json;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;

namespace RimWise.Desktop.Services;

/// <summary>One-shot editor sorting. No game launcher, arbitrary file operations, or automatic execution.</summary>
internal sealed class AiSortingSession(MainViewModel editor, string dataDirectory,
    IAiClient? client = null, ISecretStore? secretStore = null)
{
    internal AiSettingsStore SettingsStore { get; } = new(dataDirectory);
    internal ISecretStore Secrets { get; } = secretStore ?? new LocalEncryptedSecretStore(dataDirectory);
    private readonly AiTokenEstimator estimator = new(dataDirectory);
    private readonly AiAuditStore audit = new(dataDirectory);
    private readonly ExecutionJournal journal = new(dataDirectory);
    internal AiTaskRecoveryStore Tasks { get; } = new(dataDirectory);

    // Persist the same metadata projection as previous releases; alias mapping changes only
    // the outbound prompt, not whether a paid response can be recovered safely.
    private string Context(AiRequest request) => AiTaskRecoveryStore.ContextHash(request with
        { ModRequirements = AiModRequirementFact.FromCatalogue(editor.CurrentCatalogue!) },
        AiOrderPlanner.Fingerprint(request.CurrentOrder, editor.CurrentCatalogue!, request.RetainedMods,
            editor.CustomRules, GameVersion.Parse(request.GameVersion)));

    internal string? ReadinessProblem => !editor.HasDocument ? "请先载入排序。"
        : !editor.ColorRetentionAvailable ? "颜色或保留规则读取失败，请修复记录后重启软件。"
        : (editor.IsScanning && !editor.IsEnrichingContent) || editor.IsShowingCache ? "请等待刷新完成。"
        : editor.CurrentCatalogue is null ? "请先刷新模组资料。" : null;

    internal AiTokenEstimate Estimate(AiSettings settings) => estimator.Estimate(CreateRequest(editor, settings, null));

    internal string UsageDescription()
    {
        return AiUsageTotals.Read(Tasks).Description;
    }

    internal static AiRequest CreateRequest(MainViewModel editor, AiSettings settings, string? key) => new(settings, key,
        editor.EffectiveGameVersion?.ToString() ?? editor.VersionLabel, editor.Rows.Select(row => row.PackageId).ToArray(),
        editor.Rows.Select(row => row.PackageId).Concat(editor.AvailableMods.Select(row => row.PackageId))
            .Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), editor.RetainedPackageIds.ToArray(), [])
    {
        DiagnosticFacts = editor.Diagnostics.Select(issue => new AiDiagnosticFact(issue.Diagnostic.Code, issue.Identifier)).ToArray(),
        OrderRules = editor.CustomRules.ToArray(),
        ModRequirements = editor.CurrentCatalogue is { } catalogue ? AiModRequirementFact.FromCatalogue(catalogue,
            editor.Rows.Select(row => row.PackageId).ToArray(), editor.EffectiveGameVersion) : []
    };

    internal async Task<AiSortingOutcome> RequestAsync(AiSettings settings, AiTokenEstimate estimate, CancellationToken token,
        IProgress<AiResponseUpdate>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (ReadinessProblem is { } problem) throw new InvalidOperationException(problem);
        if (!settings.Enabled) throw new InvalidOperationException("请在 AI 设置中启用 AI。");
        var revision = editor.ContentRevision;
        var catalogue = editor.CurrentCatalogue!;
        var rules = editor.CustomRules.ToArray();
        var request = CreateRequest(editor, settings, Secrets.Read(settings.SecretName)) with { ResponseProgress = progress };
        using var requestSlot = Tasks.AcquireRequestSlot();
        using var task = Tasks.Create(new(AiTaskKind.Sorting, AiTaskPhase.Prepared, Context(request),
            request.CurrentOrder.ToArray(), request.CurrentOrder.ToArray(), DateTimeOffset.UtcNow));
        using var ownedClient = client is null ? new AiClient() : null;
        using var usagePeriod = task.TrackUsagePeriod(request);
        // Publish the possibly-billed state BEFORE handing control to the transport.
        task.Save(task.Current with { Phase = AiTaskPhase.RequestPending, Requests = 1, UnknownUsage = true,
            ReservedTokens = estimate.Upper, Message = "请求已准备发送；若中断，须先核对用量。" });
        var result = await AiUsageTracking.SendAsync(task, client ?? ownedClient!, request, token);
        var recoveryPlan = result.Response is null ? null : AiOrderPlanner.Plan(result.Response.Actions, settings.PermissionLevel,
            request.CurrentOrder, catalogue, request.RetainedMods, GameVersion.Parse(request.GameVersion), rules);
        var hasUsage = result.Usage is { Valid: true };
        task.Save(task.Current with { Phase = AiTaskPhase.ReplySaved, UnknownUsage = !hasUsage,
            ReservedTokens = hasUsage ? 0 : estimate.Upper, LastUsage = hasUsage ? result.Usage : null,
            ReportedTokens = hasUsage ? result.Usage!.Total : 0,
            MayApply = result.Succeeded && recoveryPlan?.CanApply == true && !token.IsCancellationRequested,
            Actions = recoveryPlan?.CanApply == true ? result.Response!.Actions.Select(a => a with { Reason = null, TargetPath = null }).ToArray() : [],
            Message = "响应检查点已保存；恢复时会重新校验。" });
        string? notice = null;
        if (result.Usage is { } usage)
        {
            try { estimator.Observe(estimate, usage); }
            catch (Exception ex) when (IsStorageError(ex)) { notice = "本次用量记录未能保存，预估校准未更新。"; }
        }
        // Preserve returned billing evidence, but a cancelled request can never become an applicable plan.
        var plan = result.Response is null ? null : AiOrderPlanner.Plan(result.Response.Actions, settings.PermissionLevel,
            request.CurrentOrder, catalogue, request.RetainedMods, GameVersion.Parse(request.GameVersion), rules);
        var validation = plan is null ? new AiValidationResult([], [result.Message])
            : new AiValidationResult(plan.Rejected.Count == 0 ? result.Response!.Actions : [], plan.Rejected);
        var preview = new AiExecutionPreview().Create(validation);
        var audited = true;
        try
        {
            audit.Append(new AiAuditEntry(DateTimeOffset.UtcNow, "sorting", settings.PermissionLevel,
                result.Succeeded, result.Message, preview.Actions.Count, preview.Rejected));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            audited = false;
            notice = string.Join("\n", new[] { notice, "审计记录未能保存，本次结果不可应用。" }.Where(text => text is not null));
        }
        task.Save(task.Current with { MayApply = task.Current.MayApply && audited,
            Phase = token.IsCancellationRequested ? AiTaskPhase.Cancelled : task.Current.Phase });
        return new(result, preview, plan, revision, catalogue, settings, estimate, notice, audited && !token.IsCancellationRequested) { TaskId = task.Id };
    }

    internal bool CanApply(AiSortingOutcome outcome) => outcome.MayApply && outcome.Result.Succeeded && outcome.Plan?.CanApply == true
        && ReadinessProblem is null && outcome.Revision == editor.ContentRevision
        && ReferenceEquals(outcome.Catalogue, editor.CurrentCatalogue);

    internal void Apply(AiSortingOutcome outcome)
    {
        if (!CanApply(outcome) || SettingsStore.Load() != outcome.Settings)
            throw new InvalidOperationException("排序、安装资料或 AI 设置已变化，请重新开始 自动排序。");
        editor.ValidateAiPlan(outcome.Plan!, outcome.Revision);
        using var task = outcome.TaskId is null ? null : Tasks.Open(outcome.TaskId);
        if (task is null || task.Current.Discarded || task.Current.Phase != AiTaskPhase.ReplySaved)
            throw new InvalidOperationException("任务已经应用、放弃或不再处于待应用状态。");
        task.Save(task.Current with { Phase = AiTaskPhase.Applying, CurrentOrder = outcome.Plan!.After.ToArray() });
        var id = journal.Prepare(outcome.Plan!, outcome.Settings.PermissionLevel);
        editor.ApplyAiPlan(outcome.Plan!, outcome.Revision);
        journal.Complete(id);
        task.Save(task.Current with { Phase = AiTaskPhase.Applied, MayApply = false, Message = "排序已应用到编辑清单。" });
    }

    internal AiSortingOutcome Recover(string id, AiSettings settings)
    {
        if (ReadinessProblem is { } problem) throw new InvalidOperationException(problem);
        using var task = Tasks.Open(id);
        var saved = task.Current;
        if (saved.Kind != AiTaskKind.Sorting || saved.Finished
            || saved.Phase is not (AiTaskPhase.ReplySaved or AiTaskPhase.Applying or AiTaskPhase.Applied)
            || saved.Phase == AiTaskPhase.ReplySaved && !saved.MayApply)
            throw new InvalidOperationException("该任务没有可恢复的排序方案；请求中断不会自动补发。");
        var request = CreateRequest(editor, settings, null);
        if (saved.Phase is AiTaskPhase.Applying or AiTaskPhase.Applied
            && request.CurrentOrder.SequenceEqual(saved.CurrentOrder, StringComparer.Ordinal))
        {
            task.Save(saved with { Phase = AiTaskPhase.Completed, MayApply = false, Message = "已核对该排序已在编辑清单中，没有重复应用。" });
            throw new InvalidOperationException("该排序已经在当前清单中，无需重复应用。");
        }
        if (saved.ContextHash != Context(request)) throw new InvalidOperationException("排序、模组资料、保留规则或 AI 设置已变化，旧方案不能继续应用。");
        var plan = AiOrderPlanner.Plan(saved.Actions, settings.PermissionLevel, request.CurrentOrder, editor.CurrentCatalogue!,
            request.RetainedMods, GameVersion.Parse(request.GameVersion), editor.CustomRules);
        if (!plan.CanApply) throw new InvalidOperationException("恢复方案未通过当前校验。");
        // A crash may have occurred between saving the reply and its original audit entry.
        // Recovery must successfully audit again before returning an applicable preview.
        audit.Append(new AiAuditEntry(DateTimeOffset.UtcNow, "sorting-recovery", settings.PermissionLevel,
            true, "恢复已保存方案，重新校验通过。", saved.Actions.Length, []));
        if (saved.Phase != AiTaskPhase.ReplySaved)
            task.Save(saved with { Phase = AiTaskPhase.ReplySaved, CurrentOrder = saved.OriginalOrder, MayApply = true });
        var result = new AiClientResult(true, "已恢复保存的方案，未发送 AI 请求。", new(1, "", saved.Actions, []), null) { Usage = saved.LastUsage };
        return new(result, AiChangePreview.From(new(saved.Actions, [])), plan, editor.ContentRevision,
            editor.CurrentCatalogue!, settings, estimator.Estimate(request), null, true) { TaskId = id };
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
}

internal sealed record AiSortingOutcome(AiClientResult Result, AiChangePreview Preview, AiOrderPlan? Plan,
    long Revision, ModCatalogue Catalogue, AiSettings Settings, AiTokenEstimate Estimate, string? Notice, bool MayApply)
{
    internal string? TaskId { get; init; }
}
