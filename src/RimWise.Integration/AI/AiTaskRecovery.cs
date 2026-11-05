using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.AI;

public enum AiTaskKind { Sorting, Maintenance, Connection, LogDiagnosis }
public enum AiTaskPhase { Prepared, RequestPending, ReplySaved, Applying, Applied, Testing, TestSaved, Paused, Completed, Cancelled }
public sealed record RequiredModDownload(string PackageId, string WorkshopId)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool DiscoverPackageId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>Local recovery data only. No credentials, raw prompts, logs or complete provider replies;
/// startup feedback may retain bounded display-only advice.</summary>
public sealed record AiTaskCheckpoint(AiTaskKind Kind, AiTaskPhase Phase, string ContextHash,
    string[] OriginalOrder, string[] CurrentOrder, DateTimeOffset StartedUtc)
{
    public AiAction[] Actions { get; init; } = [];
    public string[][] SeenOrders { get; init; } = [];
    public int Attempts { get; init; }
    public int Requests { get; init; }
    public int ReportedTokens { get; init; }
    public int ReservedTokens { get; init; }
    public bool UnknownUsage { get; init; }
    public bool Discarded { get; init; }
    public bool MayApply { get; init; }
    public AiTokenUsage? LastUsage { get; init; }
    public int? LastRequestOutputLimit { get; init; }
    public bool OutputBudgetExhausted { get; init; }
    public MaintenanceLimits? Limits { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public string Message { get; init; } = "任务已保存。";
    public RequiredModDownload[] RequiredDownloads { get; init; } = [];
    public string[] InitialMissingIds { get; init; } = [];
    public bool CandidatePendingApply { get; init; }
    public ConflictRemovalApproval[] ConflictApprovals { get; init; } = [];
    public RuntimeConflictEvidence? PendingConflict { get; init; }
    public TrialExclusion[] TrialExclusions { get; init; } = [];
    public string[] DiagnosedFailures { get; init; } = [];
    public bool TrialRemovalsApproved { get; init; }
    public bool TrialDecisionRecorded { get; init; }
    public AiUsageRequest[] UsageRequests { get; init; } = [];
    public AiUsagePeriod[] UsagePeriods { get; init; } = [];
    public string DatabaseSnapshotId { get; init; } = "";
    public MaintenanceFeedback? Feedback { get; init; }
    public InsertionEvidence? InsertionControl { get; init; }
    public int InsertionControlAttempts { get; init; }
    // A stopped task's explicit publication decision does not certify a passing game test.
    public string? StoppedOrderDecision { get; init; }

    // Billing uncertainty alone does not mean a reply was lost. Older interrupted
    // records without this receipt remain non-replayable until explicitly reconciled.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasUnresolvedRequest => Phase == AiTaskPhase.RequestPending
        || UnknownUsage && UsageRequests.LastOrDefault() is not { ResponseReceived: true };

    internal void Validate()
    {
        if (LastRequestOutputLimit is < 1 or > 32768 || OutputBudgetExhausted && LastRequestOutputLimit is null)
            throw new InvalidDataException("AI 输出额度记录无效。");
        InsertionControl?.Validate();
        if (InsertionControlAttempts is < 0 or > 1 || InsertionControlAttempts > 0 && Limits?.InsertionBaselineJson is null)
            throw new InvalidDataException("原排序对照次数无效。");
        if (Limits?.InsertionBaselineJson is { } scope)
        {
            InsertionScope.ValidateOrder(scope, OriginalOrder);
            InsertionScope.ValidateOrder(scope, CurrentOrder,
                (ConflictApprovals ?? []).SelectMany(a => a?.RemovedIds ?? []));
        }
        if (InsertionControl is not null && (Limits?.InsertionBaselineJson is null || InsertionControlAttempts != 1))
            throw new InvalidDataException("非新增任务不能使用原排序对照证据。");
        if (StoppedOrderDecision is not (null or "keep" or "restore")
            || StoppedOrderDecision is not null && (Kind != AiTaskKind.Maintenance || CandidatePendingApply))
            throw new InvalidDataException("Stopped order decision is invalid.");
        PendingConflict?.Validate();
        if (DiagnosedFailures is null || DiagnosedFailures.Length > Requests
            || DiagnosedFailures.Any(f => !MaintenanceFailureSignature.Valid(f))
            || DiagnosedFailures.Distinct(StringComparer.Ordinal).Count() != DiagnosedFailures.Length)
            throw new InvalidDataException("重复错误诊断记录无效。");
        // Publishing an explicitly chosen stopped order does not turn its test into a pass.
        // ReplySaved/Applying can be retained when a stopped run still had a pending action.
        var stoppedTrialDecision = StoppedOrderDecision == "keep" && !MayApply && Actions is { Length: 0 }
            && Phase is AiTaskPhase.Cancelled or AiTaskPhase.Paused or AiTaskPhase.ReplySaved or AiTaskPhase.Applying;
        if (TrialExclusions is null || TrialExclusions.Length > 20000
            || TrialExclusions.Length > 0 && Kind != AiTaskKind.Maintenance
            || (TrialRemovalsApproved || TrialDecisionRecorded)
                && (TrialExclusions.Length == 0 || Phase != AiTaskPhase.Completed && !stoppedTrialDecision))
            throw new InvalidDataException("临时试排除任务状态无效。");
        var trialRemoved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastTrialAttempt = 0;
        foreach (var trial in TrialExclusions)
        {
            if (trial is null) throw new InvalidDataException("临时试排除记录缺失。");
            trial.Validate();
            if (trial.Attempt < lastTrialAttempt || trial.Attempt > Attempts
                || trial.RemovedIds.Any(id => !trialRemoved.Add(id)))
                throw new InvalidDataException("临时试排除轮次或移出项重复。");
            lastTrialAttempt = trial.Attempt;
        }
        ValidateUsage(UsageRequests, UsagePeriods);
        if (DatabaseSnapshotId is null || DatabaseSnapshotId.Length != 0
            && !Databases.DatabaseParser.Hex(DatabaseSnapshotId, 64)) throw new InvalidDataException("任务数据库快照编号无效。");
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Phase) || ContextHash is not { Length: 64 }
            || !ContextHash.All(char.IsAsciiHexDigit) || StartedUtc == default
            || OriginalOrder is null || CurrentOrder is null || Actions is null || SeenOrders is null
            || Actions.Length > 500 || Attempts < 0 || Requests < 0 || ReportedTokens < 0
            || ReservedTokens < 0 || Message is null || Message.Length > 2000
            || (Phase == AiTaskPhase.RequestPending && (!UnknownUsage || Requests == 0))
            || LastUsage is { Valid: false }) throw new InvalidDataException("任务检查点无效，已保留记录。");
        foreach (var order in SeenOrders.Append(OriginalOrder).Append(CurrentOrder))
            if (order is null || order.Length > 20000 || order.Any(id => string.IsNullOrWhiteSpace(id)
                || id.Length > AiModIdentifier.MaximumLength || id.Any(char.IsControl))
                || order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != order.Length)
                throw new InvalidDataException("任务排序检查点无效。");
        // Persist only permission-checked editor actions, without model prose or file paths.
        if (Actions.Any(a => a is null || a.TargetPath is not null || a.Reason is not null
            || a.NormalizedKind is not ("move" or "reorder" or "add" or "addmod" or "remove" or "removemod")
            || string.IsNullOrWhiteSpace(a.PackageId) || a.PackageId.Length > AiModIdentifier.MaximumLength || a.PackageId.Any(char.IsControl)))
            throw new InvalidDataException("恢复动作超出允许范围。");
        Limits?.Validate();
        if (InitialMissingIds is null || InitialMissingIds.Any(id => !OriginalOrder.Contains(id, StringComparer.OrdinalIgnoreCase))
            || InitialMissingIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != InitialMissingIds.Length)
            throw new InvalidDataException("原始缺失模组记录无效。");
        if (ConflictApprovals is null || ConflictApprovals.Length > 20000
            || ConflictApprovals.Any(a => a is null || string.IsNullOrWhiteSpace(a.SelectedId) || a.SelectedId.Length > AiModIdentifier.MaximumLength
                || a.SelectedId.Any(char.IsControl) || a.RemovedIds is null || a.RemovedIds.Length is < 1 or > 20000
                || a.RemovedIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > AiModIdentifier.MaximumLength || id.Any(char.IsControl))
                || !a.RemovedIds.Contains(a.SelectedId, StringComparer.OrdinalIgnoreCase)
                || a.RemovedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != a.RemovedIds.Length
                || a.SelectedIds is null || a.SelectedIds.Length > 20000
                || a.SelectedIds.Any(id => !a.RemovedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                || a.SelectedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != a.SelectedIds.Length
                || a.SelectedIds.Length > 0 && a.SelectedIds[0] != a.SelectedId)
            || ConflictApprovals.Select(a => a.SelectedId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ConflictApprovals.Length)
            throw new InvalidDataException("用户冲突选择记录无效。");
        foreach (var approval in ConflictApprovals)
        {
            approval.RuntimeEvidence?.Validate();
            if (approval.RetentionOverrideIds is null || approval.RetentionOverrideIds.Length > 20000
                || approval.RetentionOverrideIds.Any(id => !approval.RemovedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                || approval.RetentionOverrideIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != approval.RetentionOverrideIds.Length)
                throw new InvalidDataException("保留保护覆盖记录无效。");
            if (approval.MissingIds is null || approval.MissingIds.Length > 20000
                || approval.MissingIds.Any(id => !approval.RemovedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                || approval.MissingIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != approval.MissingIds.Length
                || approval.MissingIds.Length > 0 && approval.RuntimeEvidence is not null)
                throw new InvalidDataException("缺失模组选择记录无效。");
        }
        if (CandidatePendingApply && (Kind != AiTaskKind.Maintenance || Phase != AiTaskPhase.Completed))
            throw new InvalidDataException("待恢复的测试结果状态无效。");
        if (RequiredDownloads is null || RequiredDownloads.Length > 20000
            || RequiredDownloads.Any(d => d is null || string.IsNullOrWhiteSpace(d.PackageId) || d.PackageId.Length > AiModIdentifier.MaximumLength
                || d.Reason is { Length: > 16000 }
                || d.PackageId.Any(char.IsControl) || d.DiscoverPackageId && d.PackageId != "rimwise.workshop-required." + d.WorkshopId
                || string.IsNullOrEmpty(d.WorkshopId) || !d.WorkshopId.All(char.IsAsciiDigit)
                || !ulong.TryParse(d.WorkshopId,out var number) || number == 0)
            || RequiredDownloads.Select(d => d.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != RequiredDownloads.Length)
            throw new InvalidDataException("必要前置下载记录无效。");
        if (Kind == AiTaskKind.Maintenance && (Limits is null || DeadlineUtc is null))
            throw new InvalidDataException("维护恢复缺少原始预算。");
        Feedback?.Validate(this);
    }

    public bool Finished => StoppedOrderDecision is not null || Phase == AiTaskPhase.Completed && !CandidatePendingApply || Phase == AiTaskPhase.Cancelled || Discarded;

    internal static void ValidateUsage(AiUsageRequest[] requests, AiUsagePeriod[] periods)
    {
        if (requests is null || periods is null
            || requests.Any(r => r is null) || periods.Any(p => p is null)
            || requests.Select(r => r.Id).Distinct().Count() != requests.Length)
            throw new InvalidDataException("任务时间记录无效。");
        foreach (var request in requests) request.Validate();
        foreach (var period in periods)
            if (period.Scope is not { Length: 64 } || !period.Scope.All(char.IsAsciiHexDigit) || !Enum.IsDefined(period.Kind)
                || period.StartedUtc == default || period.EndedUtc < period.StartedUtc)
                throw new InvalidDataException("任务时间区间无效。");
    }
}

public sealed record AiRecoverableTask(string Id, AiTaskCheckpoint? Checkpoint, string? Problem, bool Active = false);

/// <summary>
/// Immutable, checksummed, chained checkpoints: flush data before publishing a same-volume rename.
/// An OS file lease covers each operation, including an HTTP request. No PID-only liveness decisions.
/// A torn committed record fails closed; unpublished .tmp files never authorize replay.
/// </summary>
public sealed class AiTaskRecoveryStore
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly string root;
    private readonly AiDeletedTaskUsageStore deleted;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 32 };
    public AiTaskRecoveryStore(string applicationDirectory)
    {
        root = ScopedPaths.Child(applicationDirectory, "Data/Tasks");
        deleted = new(applicationDirectory);
    }

    public IReadOnlyList<AiDeletedTaskUsage> InspectDeletedUsage() => deleted.Inspect();

    /// <summary>Read immutable committed checkpoints for monitoring, including a task with an active writer.</summary>
    public IReadOnlyList<AiRecoverableTask> InspectUsage() => Inspect().Select(item =>
    {
        if (!item.Active) return item;
        try
        {
            var directory = ScopedPaths.Child(root, item.Id);
            using var reader = new AiTaskLease(directory, Stream.Null);
            reader.Read();
            return item with { Checkpoint = reader.Current };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        { return item with { Problem = "用量时间记录暂时无法读取。" }; }
    }).ToArray();

    public static string ContextHash(AiRequest request, string catalogueFingerprint)
    {
        var original = Convert.ToHexString(SHA256.HashData(
        // Presentation-only streaming preference must not invalidate older paid checkpoints.
        JsonSerializer.SerializeToUtf8Bytes(new { Settings = new { request.Settings.SchemaVersion, request.Settings.Enabled,
            request.Settings.Provider, request.Settings.Endpoint, request.Settings.Model,
            request.Settings.PermissionLevel, request.Settings.SecretName }, request.GameVersion, request.CurrentOrder,
            request.InstalledMods, request.RetainedMods, request.DiagnosticFacts, request.ModRequirements,
            request.OrderRules, request.OutputTokenLimit, Catalogue = catalogueFingerprint }, Json)));
        // Preserve legacy paid checkpoints byte-for-byte when no database was used.
        return request.DatabaseSnapshotId.Length == 0 ? original : Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(original + ":" + request.DatabaseSnapshotId)));
    }

    public AiTaskLease Create(AiTaskCheckpoint checkpoint)
    {
        checkpoint.Validate();
        Directory.CreateDirectory(ScopedPaths.Ordinary(root));
        var id = Guid.NewGuid().ToString("N");
        var directory = ScopedPaths.Child(root, id);
        Directory.CreateDirectory(directory);
        var lease = Acquire(id);
        try { lease.Save(checkpoint); return lease; }
        catch { lease.Dispose(); throw; }
    }

    public AiTaskLease Open(string id)
    {
        if (deleted.Contains(id)) throw new InvalidOperationException("该任务已删除，不能继续。");
        var lease = Acquire(id);
        try { lease.Read(); return lease; }
        catch { lease.Dispose(); throw; }
    }

    public IReadOnlyList<AiRecoverableTask> Inspect()
    {
        ScopedPaths.Ordinary(root);
        if (!Directory.Exists(root)) return [];
        var result = new List<AiRecoverableTask>();
        var directories = Directory.GetDirectories(root);
        if (directories.Length > 5000) throw new InvalidDataException("任务记录过多，请归档后再继续。");
        foreach (var directory in directories)
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            if (deleted.Contains(id)) continue;
            try
            {
                // An empty, unpublished task cannot have sent an HTTP request.
                ScopedPaths.Ordinary(directory, true);
                using var lease = Acquire(id);
                if (!Directory.EnumerateFiles(directory, "*.json").Any()) continue;
                lease.Read();
                result.Add(new(id, lease.Current, null));
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { result.Add(new(id, null, null, true)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { result.Add(new(id, null, "任务记录无法验证，未执行恢复。")); }
        }
        return result.OrderByDescending(t => t.Checkpoint?.StartedUtc).ToArray();
    }

    internal void EnsureRequestAllowed(string? ownedTaskId = null)
    {
        // Historical missing billing remains in the ledger. Only an active writer
        // or unverifiable state blocks a new request; this never replays an old request.
        _ = deleted.Inspect();
        if (Inspect().Any(t => t.Id != ownedTaskId && (t.Problem is not null || t.Active)))
            throw new InvalidOperationException("有进行中或损坏的 AI 任务，请先在恢复任务中处理；不会并行发送请求。");
    }

    public IDisposable AcquireRequestSlot() => AcquireRequestSlot(null);

    internal IDisposable AcquireRequestSlot(string? ownedTaskId)
    {
        Directory.CreateDirectory(ScopedPaths.Ordinary(root));
        var slot = new FileStream(ScopedPaths.Child(root, "requests.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { EnsureRequestAllowed(ownedTaskId); return slot; }
        catch { slot.Dispose(); throw; }
    }

    public void Discard(string id)
    {
        // Serialize with senders, but unresolved billing must never prevent deletion.
        Directory.CreateDirectory(ScopedPaths.Ordinary(root));
        using var slot = new FileStream(ScopedPaths.Child(root, "requests.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        DiscardUnderRequestLock(id);
    }

    /// <summary>Explicit user cleanup of finished history. Resumable, damaged, active and unbilled tasks remain.</summary>
    public int CleanFinishedHistory()
    {
        Directory.CreateDirectory(ScopedPaths.Ordinary(root));
        using var slot = new FileStream(ScopedPaths.Child(root, "requests.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var removed = 0;
        foreach (var item in Inspect())
        {
            if (item.Active || item.Problem is not null || item.Checkpoint is not { Finished: true, UnknownUsage: false }) continue;
            // Revalidate while holding the task lease: the inspected snapshot is not authority to delete.
            using (var lease = Open(item.Id))
                if (!lease.Current.Finished || lease.Current.UnknownUsage) continue;
            DiscardUnderRequestLock(item.Id, finishedOnly: true);
            removed++;
        }
        // A crash after publishing a tombstone may leave its recovery payload. Receipts stay untouched.
        foreach (var receipt in deleted.Inspect())
        {
            if (Directory.Exists(ScopedPaths.Child(root, receipt.Id)))
            { DiscardUnderRequestLock(receipt.Id); removed++; }
        }
        return removed;
    }

    private void DiscardUnderRequestLock(string id, bool finishedOnly = false)
    {
        var directory = ScopedPaths.Child(root, id);
        if (!Directory.Exists(directory))
        {
            if (deleted.Contains(id)) return;
            throw new FileNotFoundException("任务不存在。");
        }
        using (var lease = Acquire(id))
        {
            if (!deleted.Contains(id))
            {
                AiTaskCheckpoint? checkpoint = null;
                try { lease.Read(); checkpoint = lease.Current; }
                catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
                if (finishedOnly && checkpoint is not { Finished: true, UnknownUsage: false })
                    throw new InvalidOperationException("任务状态已变化，未清理恢复记录。");
                // Publish the tombstone first: a crash during cleanup cannot resurrect the task.
                deleted.Write(new(id, DateTimeOffset.UtcNow, checkpoint?.ReportedTokens ?? 0,
                    checkpoint?.ReservedTokens ?? 0, checkpoint?.UnknownUsage ?? true)
                    { UsageRequests = checkpoint?.UsageRequests ?? [], UsagePeriods = checkpoint?.UsagePeriods ?? [] });
            }
            foreach (var path in Directory.GetFiles(directory))
                if (Path.GetFileName(path) != "task.lock") File.Delete(ScopedPaths.Ordinary(path, true));
        }
        File.Delete(ScopedPaths.Child(directory, "task.lock"));
        Directory.Delete(ScopedPaths.Ordinary(directory, true));
    }

    /// <summary>Explicit local user evidence only. Never called by an AI or an automatic retry.</summary>
    public void ReconcileUsage(string id, AiTokenUsage usage)
    {
        if (!usage.Valid) throw new ArgumentException("实际用量无效。");
        if (deleted.Contains(id)) { deleted.Reconcile(id, usage); return; }
        using var lease = Open(id);
        var old = lease.Current;
        if (!old.UnknownUsage) throw new InvalidOperationException("该任务没有待核对用量。");
        lease.Save(old with { UnknownUsage = false, Phase = old.Phase == AiTaskPhase.RequestPending ? AiTaskPhase.Paused : old.Phase,
            ReportedTokens = checked(old.ReportedTokens + usage.Total), LastUsage = usage, ReservedTokens = 0,
            Message = "已按用户核对结果登记用量；未自动补发请求。" });
    }

    private AiTaskLease Acquire(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("任务编号无效。");
        var directory = ScopedPaths.Child(root, id);
        ScopedPaths.Ordinary(directory, true);
        var stream = new FileStream(ScopedPaths.Child(directory, "task.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        return new(directory, stream);
    }

    public sealed class AiTaskLease : IDisposable
    {
        private readonly string directory;
        private readonly Stream lease;
        private int sequence;
        private string previousHash = "";
        private AiTaskCheckpoint? current;
        private bool disposed, faulted;
        internal AiTaskLease(string directory, Stream lease) => (this.directory, this.lease) = (directory, lease);
        public string Id => Path.GetFileName(directory);
        public AiTaskCheckpoint Current => Copy(current ?? throw new InvalidOperationException("任务尚无检查点。"));
        public IDisposable TrackUsagePeriod(AiRequest request)
        {
            var period = new AiUsagePeriod(AiUsageRequest.ScopeFor(request.Settings.Normalize().Endpoint, request.ApiKey), Current.Kind, DateTimeOffset.UtcNow);
            Save(Current with { UsagePeriods = [.. Current.UsagePeriods, period] });
            return new UsagePeriodLease(this, period);
        }
        private sealed class UsagePeriodLease(AiTaskLease task, AiUsagePeriod period) : IDisposable
        {
            private bool ended;
            public void Dispose()
            {
                if (ended) return;
                ended = true;
                task.Save(task.Current with { UsagePeriods = task.Current.UsagePeriods.Select(p => p == period
                    ? p with { EndedUtc = DateTimeOffset.UtcNow } : p).ToArray() });
            }
        }
        private sealed record Envelope(int Schema, int Sequence, string PreviousHash, string Hash, byte[] Payload);

        internal void Read()
        {
            var files = Directory.GetFiles(directory, "*.json")
                .OrderBy(p => Path.GetFileName(p).Length).ThenBy(p => p, StringComparer.Ordinal).ToArray();
            if (files.Length < 1) throw new InvalidDataException("任务检查点数量无效。");
            foreach (var path in files)
            {
                if (Path.GetFileName(path) != (sequence + 1).ToString("D4") + ".json")
                    throw new InvalidDataException("任务检查点不连续。");
                using var input = File.OpenRead(ScopedPaths.Ordinary(path, true));
                if (input.Length > MaximumBytes * 2) throw new InvalidDataException("任务记录过大。");
                var item = JsonSerializer.Deserialize<Envelope>(input, Json) ?? throw new InvalidDataException("任务记录为空。");
                if (item.Schema != 1 || item.Sequence != sequence + 1 || item.PreviousHash != previousHash
                    || item.Payload is null || item.Payload.Length > MaximumBytes
                    || item.Hash != Hash(item.Sequence, item.PreviousHash, item.Payload)) throw new InvalidDataException("任务记录校验失败。");
                var value = JsonSerializer.Deserialize<AiTaskCheckpoint>(item.Payload, Json) ?? throw new InvalidDataException("任务检查点为空。");
                value.Validate();
                if (current is not null) ValidateTransition(current, value);
                current = value; sequence = item.Sequence; previousHash = item.Hash;
            }
        }

        // Call while holding this lease before applying external changes. Save rechecks the
        // same invariants; this preflight performs no writes and cannot guarantee later I/O.
        public void ValidateSave(AiTaskCheckpoint value) => PrepareSave(value);

        private (AiTaskCheckpoint Checkpoint, byte[] Payload) PrepareSave(AiTaskCheckpoint value)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (ReferenceEquals(lease, Stream.Null)) throw new InvalidOperationException("用量快照只读。");
            if (faulted) throw new IOException("检查点写入结果未知，必须重新打开任务核对。");
            value.Validate();
            value = Copy(value);
            if (current is not null) ValidateTransition(current, value);
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (payload.Length > MaximumBytes) throw new IOException("任务检查点过大。");
            return (value, payload);
        }

        public void Save(AiTaskCheckpoint value)
        {
            var prepared = PrepareSave(value);
            value = prepared.Checkpoint;
            var payload = prepared.Payload;
            var next = checked(sequence + 1);
            var hash = Hash(next, previousHash, payload);
            var temporary = ScopedPaths.Child(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(output, new Envelope(1, next, previousHash, hash, payload), Json); output.Flush(true); }
                File.Move(temporary, ScopedPaths.Child(directory, next.ToString("D4") + ".json"));
                current = value; sequence = next; previousHash = hash;
                Diagnostics.DiagnosticEventLog.Current?.Record("ai-task", "checkpoint-saved",
                    $"task={Path.GetFileName(directory)}; phase={value.Phase}; attempt={value.Attempts}; requests={value.Requests}; reportedTokens={value.ReportedTokens}; unknownUsage={value.UnknownUsage}; {value.Message}");
            }
            catch { faulted = true; throw; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void ValidateTransition(AiTaskCheckpoint old, AiTaskCheckpoint next)
        {
            if (old.Kind != next.Kind || old.StartedUtc != next.StartedUtc || old.ContextHash != next.ContextHash
                || old.DatabaseSnapshotId != next.DatabaseSnapshotId
                || next.InsertionControlAttempts < old.InsertionControlAttempts
                || old.InsertionControl is not null && JsonSerializer.Serialize(old.InsertionControl, Json) != JsonSerializer.Serialize(next.InsertionControl, Json)
                || !old.OriginalOrder.SequenceEqual(next.OriginalOrder, StringComparer.Ordinal)
                || !old.InitialMissingIds.SequenceEqual(next.InitialMissingIds, StringComparer.Ordinal)
                || next.Attempts < old.Attempts || next.Requests < old.Requests || next.ReportedTokens < old.ReportedTokens
                || old.Discarded && !next.Discarded || old.Limits != next.Limits || old.DeadlineUtc != next.DeadlineUtc
                || (old.Phase is AiTaskPhase.Completed or AiTaskPhase.Cancelled) && next.Phase != old.Phase
                || next.SeenOrders.Length < old.SeenOrders.Length
                || next.TrialExclusions.Length < old.TrialExclusions.Length
                || !old.DiagnosedFailures.SequenceEqual(next.DiagnosedFailures.Take(old.DiagnosedFailures.Length), StringComparer.Ordinal)
                || old.TrialExclusions.Where((t, i) => JsonSerializer.Serialize(t, Json)
                    != JsonSerializer.Serialize(next.TrialExclusions[i], Json)).Any()
                || old.TrialRemovalsApproved && !next.TrialRemovalsApproved
                || old.TrialDecisionRecorded && (!next.TrialDecisionRecorded
                    || old.TrialRemovalsApproved != next.TrialRemovalsApproved)
                || next.ConflictApprovals.Length < old.ConflictApprovals.Length
                || old.ConflictApprovals.Where((a, i) => a.SelectedId != next.ConflictApprovals[i].SelectedId
                    || !a.RemovedIds.SequenceEqual(next.ConflictApprovals[i].RemovedIds, StringComparer.Ordinal)
                    || !a.SelectedIds.SequenceEqual(next.ConflictApprovals[i].SelectedIds, StringComparer.Ordinal)
                    || !a.MissingIds.SequenceEqual(next.ConflictApprovals[i].MissingIds, StringComparer.Ordinal)
                    || JsonSerializer.Serialize(a.RuntimeEvidence, Json) != JsonSerializer.Serialize(next.ConflictApprovals[i].RuntimeEvidence, Json)).Any()
                || old.RequiredDownloads.Any(d => !next.RequiredDownloads.Contains(d))
                || next.UsageRequests.Length < old.UsageRequests.Length || next.UsagePeriods.Length < old.UsagePeriods.Length
                || old.UsageRequests.Where((r, i) => !ValidUsageUpdate(r, next.UsageRequests[i])).Any()
                || old.UsagePeriods.Where((p, i) => p != next.UsagePeriods[i]
                    && (p.EndedUtc is not null || p with { EndedUtc = next.UsagePeriods[i].EndedUtc } != next.UsagePeriods[i])).Any()
                || old.SeenOrders.Where((order, i) => !order.SequenceEqual(next.SeenOrders[i], StringComparer.Ordinal)).Any())
                throw new InvalidDataException("恢复不能重置原任务、预算或历史。");
        }
        private static bool ValidUsageUpdate(AiUsageRequest old, AiUsageRequest next) =>
            old with { ProviderIds = next.ProviderIds } == next && old.ProviderIds.SequenceEqual(next.ProviderIds)
            || old.EndedUtc is null && old.ProviderIds.All(next.ProviderIds.Contains)
                && old with { EndedUtc = next.EndedUtc, ProviderIds = next.ProviderIds, Usage = next.Usage,
                    ResponseReceived = next.ResponseReceived } == next;
        private static AiTaskCheckpoint Copy(AiTaskCheckpoint value) => value with
        {
            OriginalOrder = value.OriginalOrder.ToArray(), CurrentOrder = value.CurrentOrder.ToArray(),
            Actions = value.Actions.ToArray(), SeenOrders = value.SeenOrders.Select(order => order.ToArray()).ToArray(),
            RequiredDownloads = value.RequiredDownloads.ToArray(),
            InitialMissingIds = value.InitialMissingIds.ToArray(),
            UsageRequests = value.UsageRequests.Select(r => r with { ProviderIds = r.ProviderIds.ToArray() }).ToArray(),
            UsagePeriods = value.UsagePeriods.ToArray(),
            PendingConflict = value.PendingConflict?.Copy(),
            TrialExclusions = value.TrialExclusions.Select(t => t.Copy()).ToArray(),
            DiagnosedFailures = value.DiagnosedFailures.ToArray(),
            Feedback = value.Feedback?.Copy(),
            InsertionControl = value.InsertionControl?.Copy(),
            ConflictApprovals = value.ConflictApprovals.Select(a => a with { RemovedIds = a.RemovedIds.ToArray(),
                SelectedIds = a.SelectedIds.ToArray(), MissingIds = a.MissingIds.ToArray(),
                RetentionOverrideIds = a.RetentionOverrideIds.ToArray(), RuntimeEvidence = a.RuntimeEvidence?.Copy() }).ToArray()
        };
        private static string Hash(int sequence, string previous, byte[] payload)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(sequence + ":" + previous + ":"));
            hash.AppendData(payload);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        public void Dispose() { if (!disposed) { disposed = true; lease.Dispose(); } }
    }
}
