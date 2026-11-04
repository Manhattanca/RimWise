namespace RimWise.Integration.AI;

/// <summary>Technical observations extracted locally from this attempt, never arbitrary log lines.</summary>
public sealed record AiStartupEvidence(string Code, string Symbol = "", string Source = "general");

public sealed record AiStartupContext(string Stage, bool HasLog, bool CompleteCapture,
    IReadOnlyList<AiStartupEvidence> Evidence)
{
    private static readonly HashSet<string> Stages = ["Exited", "TimedOut", "LogLimit", "LaunchFailed", "ProbeCompleted", "ErrorDetected"];
    private static readonly HashSet<string> Codes = ["GraphicsDriverUnavailable", "GraphicsInitializationFailed",
        "OutOfMemory", "AccessDenied", "MissingNativeLibrary", "MissingManagedAssembly", "MissingType",
        "MissingMethod", "XmlParentMissing", "PatchFailure", "HarmonyFailure", "InitializationFailure",
        "UnhandledException", "SteamProcessNotObserved", "ProcessStartFailed", "NoMapSignal", "LogIncomplete",
        "MainMenuFailure", "NewGameConfigurationFailure", "WorldGenerationFailure", "FactionGenerationFailure", "MapGenerationFailure", "PawnTestFailure", "UnexpectedExit", "TestHelperFailure",
        "ExceptionType", "StackMethod", "PatchTarget", "PatchOwner", "NativeModule", "NativeAccessViolation",
        "SourceAvailable", "SourceUnavailable", "XmlParent", "XmlDefinition", "XmlWorker", "ObservedGameVersion",
        "TesterDetectedLogErrors", "TesterStoppedProcess", "StoppedBeforeHelperReady", "MainMenuObserved", "FactionsObserved"];

    public bool EnvironmentFailure => Evidence.Any(e => e.Code is "GraphicsDriverUnavailable"
        or "GraphicsInitializationFailed" or "OutOfMemory" or "AccessDenied");
    public bool BoundedErrorCaptureVerified { get; init; }
    public bool CanAdjustOrder => HasLog && (CompleteCapture && Stage != "LogLimit"
        || Stage == "LogLimit" && BoundedErrorCaptureVerified) && !EnvironmentFailure;

    internal AiStartupContext CopyValidated()
    {
        if (BoundedErrorCaptureVerified && (Stage != "LogLimit" || !HasLog || CompleteCapture)
            || !Stages.Contains(Stage) || Evidence is null || Evidence.Count is < 1 or > 96
            || Evidence.Any(e => e is null || !Codes.Contains(e.Code) || e.Symbol is null || e.Symbol.Length > 160
                || e.Source is not ("general" or "game-errors" or "startup" or "crash")
                || !e.Symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '`' or '-')))
            throw new InvalidDataException("启动诊断上下文无效。");
        return this with { Evidence = Evidence.Distinct().ToArray() };
    }
}

/// <summary>Model advice is display-only. Only the existing level-one planner can authorize actions.</summary>
public sealed record AiStartupDiagnosis(string Category, string Reason, IReadOnlyList<string> Suggestions)
{
    internal AiStartupDiagnosis CopyValidated()
    {
        if (Category is not ("mod" or "environment" or "unknown") || string.IsNullOrWhiteSpace(Reason)
            || Reason.Length > 2000 || Suggestions is null || Suggestions.Count > 8
            || Suggestions.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 1000))
            throw new InvalidDataException("AI 启动诊断格式无效。");
        return this with { Suggestions = Suggestions.ToArray() };
    }
}

public sealed record StartupFailureDetails(AiStartupContext Context, IReadOnlyList<string> LogPaths,
    AiStartupDiagnosis? Diagnosis = null)
{
    public IReadOnlyList<string> Observations { get; init; } = [];
    internal StartupFailureDetails CopyValidated()
    {
        if (Context is null || Observations is null || Observations.Count > 4
            || Observations.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 801)
            || LogPaths is null || LogPaths.Count > 24 || LogPaths.Any(p => string.IsNullOrWhiteSpace(p)
            || p.Length > 32768 || p.Any(char.IsControl) || !Path.IsPathFullyQualified(p)))
            throw new InvalidDataException("启动排障记录无效。");
        return this with { Context = Context.CopyValidated(), LogPaths = LogPaths.ToArray(), Observations = Observations.ToArray(), Diagnosis = Diagnosis?.CopyValidated() };
    }

    public const string HelpReminder = "向 Mod 作者或社区求助时，请提供上述日志文件，并说明游戏版本、模组清单和复现步骤；不要只带着此界面的截图询问他人。日志可能含本机路径等信息，分享前请检查隐私。";

    public string DisplayText => string.Join(Environment.NewLine, new[]
    {
        Context.Evidence.Any(e => e.Code == "TesterDetectedLogErrors")
            ? "测试器检测到日志错误后结束了本轮测试；这不代表游戏自行崩溃。" : "",
        Observations.Count > 0 ? "本轮捕获的具体错误：" + Environment.NewLine + string.Join(Environment.NewLine, Observations) : "",
        "启动排障：" + (Diagnosis?.Reason ?? LocalReason()),
        Diagnosis is null ? "尚无经过确认的 AI 诊断结论。" : "AI 分析仅供排查，尚未证明根因。",
        string.Join(Environment.NewLine, Diagnosis?.Suggestions.Select(s => "建议：" + s) ?? []),
        "本轮日志与启动记录（可复制路径）：",
        LogPaths.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, LogPaths.Select(Diagnostics.StoredEvidence.Resolve)) : "未取得本轮游戏日志。",
        HelpReminder
    }.Where(s => s.Length > 0));

    public string LocalReason() => Context.EnvironmentFailure
        ? "本轮记录包含图形驱动、资源或访问权限相关错误，自动排序已停止。"
        : Context.Stage == "LaunchFailed" ? "未能确认本轮游戏进程启动；请检查 Steam 启动记录。"
        : !Context.HasLog ? "游戏未完成地图验证，且未取得可分析的本轮日志，暂时无法确定原因。"
        : Context.Stage == "LogLimit" ? "本轮日志达到大小上限，测试器已停止游戏；已保留错误证据，但本轮没有完成地图验证，不能判定排序成功。"
        : Context.Evidence.Any(e => e.Code == "TesterDetectedLogErrors")
            ? "已捕获加载或运行错误，地图测试尚未完成；请结合具体错误与异常链诊断。"
        : "游戏未完成地图验证，暂时无法确认原因。";
}
