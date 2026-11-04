namespace RimWise.Integration.AI;

/// <summary>A completed test bound to one order and attempt. Raw logs stay outside the checkpoint;
/// bounded startup advice and local support paths are retained for recovery.</summary>
public sealed record MaintenanceFeedback(int Attempt, string[] TestedOrder, bool StartupVerified,
    bool HadErrors, bool CanDiagnose)
{
    public bool ExistingErrorsOnly { get; init; }
    public InsertionEvidence? InsertionEvidence { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Passed => StartupVerified && (!HadErrors || ExistingErrorsOnly);
    public AiDiagnosticFact[] DiagnosticFacts { get; init; } = [];
    public bool ReplyReceived { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public AiReplyFailure ReplyFailure { get; init; }
    public AiServiceError? ServiceError { get; init; }
    public string ContextHash { get; init; } = "";
    public StartupFailureDetails? StartupFailure { get; init; }
    public AiRuntimeContext? RuntimeContext { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AiLogContext? LogContext { get; init; }
    public string? Analysis { get; init; }
    public string[] LogPaths { get; init; } = [];

    internal static MaintenanceFeedback Capture(int attempt, IReadOnlyList<string> order, MaintenanceGameReport report,
        string contextHash) =>
        new(attempt, order.ToArray(), report.StartupVerified, report.Errors.Count > 0, report.CanDiagnose)
        {
            ContextHash = contextHash,
            ExistingErrorsOnly = report.ExistingErrorsOnly,
            InsertionEvidence = report.InsertionEvidence?.Copy(),
            StartupFailure = report.StartupFailure?.CopyValidated(),
            RuntimeContext = report.RuntimeContext?.CopyValidated(),
            LogContext = report.LogContext?.CopyValidated(),
            LogPaths = report.LogPaths.Take(10).ToArray(),
            DiagnosticFacts = report.DiagnosticFacts.Where(f => f is not null
                && RuntimeConflictEvidence.IsDiagnosticCode(f.Code) && SafeIdentifier(f.PackageId, allowEmpty: true))
                .Distinct().Take(100).ToArray()
        };

    internal MaintenanceGameReport Restore() => new(StartupVerified, HadErrors ? ["本轮游戏测试检测到报错。"] : [])
        { ExistingErrorsOnly = ExistingErrorsOnly, InsertionEvidence = InsertionEvidence?.Copy(), CanDiagnose = CanDiagnose, DiagnosticFacts = DiagnosticFacts.ToArray(), StartupFailure = StartupFailure?.CopyValidated(),
            RuntimeContext = RuntimeContext?.CopyValidated(), LogContext = LogContext?.CopyValidated(), LogPaths = LogPaths.ToArray() };

    internal void Validate(AiTaskCheckpoint task)
    {
        InsertionEvidence?.Validate();
        if (ExistingErrorsOnly && (!StartupVerified || !HadErrors || !global::RimWise.Integration.AI.InsertionEvidence.Matches(task.InsertionControl, InsertionEvidence)
            || task.Limits?.InsertionBaselineJson is null)) throw new InvalidDataException("原排序错误判定缺少完整对照测试。");
        ServiceError?.CopyValidated();
        if (ServiceError is not null && (!ReplyReceived || ReplyFailure != AiReplyFailure.ServiceFailure))
            throw new InvalidDataException("AI service error does not match the saved reply.");
        StartupFailure?.CopyValidated();
        RuntimeContext?.CopyValidated();
        LogContext?.CopyValidated();
        if (!Enum.IsDefined(ReplyFailure) || ReplyFailure != AiReplyFailure.None && (!ReplyReceived || task.MayApply)
            || Analysis is { Length: > 10000 } || LogPaths is null || LogPaths.Length > 10
            || LogPaths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32768 || p.Any(char.IsControl) || !Path.IsPathFullyQualified(p)))
            throw new InvalidDataException("运行诊断记录无效。");
        if (StartupVerified && StartupFailure is not null) throw new InvalidDataException("已通过地图验证的反馈不能含启动失败。");
        if (StartupFailure is { } startup && task.MayApply
            && (!CanDiagnose || !startup.Context.CanAdjustOrder || startup.Diagnosis?.Category != "mod"
                || task.Limits!.AttemptLimitReached(Attempt + task.InsertionControlAttempts)))
            throw new InvalidDataException("启动排障回复不允许在恢复时应用调整。");
        if (task.Kind != AiTaskKind.Maintenance || Attempt < 1 || Attempt != task.Attempts
            || task.Limits!.AttemptLimitReached(Attempt - 1 + task.InsertionControlAttempts) || TestedOrder is null
            || !TestedOrder.SequenceEqual(task.CurrentOrder, StringComparer.Ordinal)
            || ContextHash is not { Length: 64 } || !ContextHash.All(char.IsAsciiHexDigit)
            || DiagnosticFacts is null || DiagnosticFacts.Length > 100
            || DiagnosticFacts.Any(f => f is null || !RuntimeConflictEvidence.IsDiagnosticCode(f.Code)
                || !SafeIdentifier(f.PackageId, allowEmpty: true))
            || ReplyReceived && (task.Requests == 0 || task.LastUsage is not { Valid: true }
                && task.UsageRequests.LastOrDefault() is not { ResponseReceived: true }))
            throw new InvalidDataException("维护反馈与本轮排序、次数或用量不一致。");
    }

    internal MaintenanceFeedback Copy() => this with
        { TestedOrder = TestedOrder.ToArray(), InsertionEvidence = InsertionEvidence?.Copy(), DiagnosticFacts = DiagnosticFacts.ToArray(), StartupFailure = StartupFailure?.CopyValidated(),
            RuntimeContext = RuntimeContext?.CopyValidated(), LogContext = LogContext?.CopyValidated(), LogPaths = LogPaths.ToArray() };

    private static bool SafeIdentifier(string? id, bool allowEmpty) =>
        allowEmpty ? AiModIdentifier.IsValidOrEmpty(id) : AiModIdentifier.IsValid(id);
}
