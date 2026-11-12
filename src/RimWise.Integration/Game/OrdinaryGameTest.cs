using System.Security.Cryptography;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;
using RimWise.Integration.Steam;
using RimWise.Integration.Diagnostics;

namespace RimWise.Integration.Game;

public sealed record OrdinaryGameTestOptions(string GameDirectory, string PersonalConfigPath,
    string TestRoot, GameVersion Version, int MaximumSeconds = 180)
{
    public bool UseFixedHelper { get; init; }
    public bool WarnOnSlowStartup { get; init; }
    public bool CollectInsertionEvidence { get; init; }
}
public sealed record OrdinaryGameTestResult(string SessionDirectory, OrdinaryGameReport Report,
    bool PersonalConfigUnchanged);

/// <summary>Development test entry. No AI, subscriptions, personal configuration writes or game/mod file edits.</summary>
public static class OrdinaryGameTest
{
    public static async Task<OrdinaryGameTestResult> RunAsync(OrdinaryGameTestOptions options, ModCatalogue catalogue,
        IReadOnlyList<string> order, IProgress<OrdinaryGameProgress>? progress, CancellationToken token)
    {
        var session = new OrdinaryGameTestSession(options, catalogue, order, [], [], progress);
        if (!await session.VerifySafetyAsync(token)) throw new InvalidOperationException("游戏正在运行，未启动测试。");
        try
        {
            await session.BeginAsync(token);
            await session.TestAsync(order, token);
        }
        finally
        {
            // No cancelled token can suppress process cleanup or the personal-config integrity check.
            await session.FinishAsync(false, CancellationToken.None);
        }
        return new(session.SessionDirectory!, session.LastReport!, true);
    }
}

/// <summary>Trusted bridge for the level-one controller. Only independent test configurations are written.
/// Successful candidates remain proposals; saving the personal game order is a separate existing transaction.</summary>
internal sealed class OrdinaryGameTestSession : ILevelOneGameSession, IMaintenanceTrialSession
{
    private static readonly SemaphoreSlim InProcessLease = new(1, 1);
    private readonly OrdinaryGameTestOptions options;
    private readonly ModCatalogue catalogue;
    private readonly string[] retained;
    private readonly CustomOrderRule[] rules;
    private readonly IProgress<OrdinaryGameProgress>? progress;
    private readonly string executable;
    private readonly string originalHash;
    private string[] current;
    private FileStream? lease;
    private bool inProcessLease, finished;
    private int attempts;
    private string? languageFolder;
    private TestHelperMount? helper;
    private readonly IReadOnlyList<string>? downloads;
    private readonly HashSet<string> approvedRemovals;
    private readonly HashSet<string> trialRemovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] originalOrder;
    internal string? SessionDirectory { get; private set; }
    internal OrdinaryGameReport? LastReport { get; private set; }

    internal OrdinaryGameTestSession(OrdinaryGameTestOptions options, ModCatalogue catalogue,
        IReadOnlyList<string> order, IReadOnlyList<string> retained, IReadOnlyList<CustomOrderRule> rules,
        IProgress<OrdinaryGameProgress>? progress = null, IReadOnlyList<string>? downloads = null,
        IReadOnlyList<string>? approvedRemovals = null)
    {
        this.options = options with { GameDirectory = ScopedPaths.Ordinary(options.GameDirectory, true),
            PersonalConfigPath = ScopedPaths.Ordinary(options.PersonalConfigPath, true),
            TestRoot = ScopedPaths.Ordinary(options.TestRoot) };
        if (options.MaximumSeconds < 1 || options.MaximumSeconds > (options.WarnOnSlowStartup ? 1800 : 180))
            throw new ArgumentOutOfRangeException(nameof(options));
        // Older identifier/configuration layouts require their own runtime validation before using this adapter.
        if (options.Version.Major != 1 || options.Version.Minor is < 1 or > 7)
            throw new NotSupportedException("该版本尚未适配自动运行测试；仍可使用基础排序功能。");
        this.catalogue = new(catalogue.Installations.ToArray());
        this.retained = retained.ToArray(); this.rules = rules.ToArray(); this.progress = progress;
        this.downloads = downloads?.ToArray();
        this.approvedRemovals = new(approvedRemovals ?? [], StringComparer.OrdinalIgnoreCase);
        current = order.ToArray();
        originalOrder = order.ToArray();
        foreach (var directory in this.catalogue.Installations.Select(mod => mod.Directory)
            .Append(this.options.GameDirectory).Append(Path.GetDirectoryName(this.options.PersonalConfigPath)!))
        {
            // The scanner keeps unreadable and linked entries for display. They may
            // be inactive or already removed by helper recovery; do not traverse or
            // require them to exist. Keep the lexical test-output overlap check.
            // Every selected installation is checked by ValidateOrder, including
            // newly added prerequisites in ApplyAsync and each TestAsync call.
            if (Overlaps(this.options.TestRoot, directory)) throw new IOException("测试数据必须与游戏、模组和个人配置目录分开。");
        }
        ValidateOrder(current.Except(this.approvedRemovals,StringComparer.OrdinalIgnoreCase).ToArray());
        executable = GameProcessService.FindExecutable(this.options.GameDirectory);
        originalHash = ConfigHash();
    }

    public async Task<bool> VerifySafetyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckPersonalConfig();
        ScopedPaths.Ordinary(executable, true);
        ScopedPaths.Ordinary(options.TestRoot);
        ValidateOrder(current.Except(approvedRemovals,StringComparer.OrdinalIgnoreCase).ToArray());
        // Only verified remnants of this tool's independent tests can be stopped.
        // An unrelated game or an ambiguous journal blocks a new test instead.
        await GameTestRecovery.StopInterruptedAsync(options, token);
        if (GameProcessService.IsAnyRunning()) return false;
        if (options.UseFixedHelper) TestHelperMount.CleanInterrupted(options);
        return true;
    }

    public Task BeginAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (finished || SessionDirectory is not null || !InProcessLease.Wait(0))
            throw new InvalidOperationException("已有测试运行或会话已经使用。");
        inProcessLease = true;
        Directory.CreateDirectory(ScopedPaths.Ordinary(options.TestRoot));
        lease = new FileStream(ScopedPaths.Child(options.TestRoot, "ordinary-game-test.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckPersonalConfig();
        if (GameProcessService.IsAnyRunning()) throw new InvalidOperationException("游戏正在运行，取消测试。");
        TestDataRetention.EnsureTestSpace(options.TestRoot);
        languageFolder = GameTestLaunch.ReadLanguage(options.PersonalConfigPath);
        SessionDirectory = ScopedPaths.Child(options.TestRoot, "game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SessionDirectory);
        OrdinaryGameRunner.WriteNew(ScopedPaths.Child(SessionDirectory, "session.json"), new
        {
            Schema = 1, Version = options.Version.ToString(), Executable = executable,
            OriginalConfigHash = originalHash, InitialOrder = current, options.MaximumSeconds, LanguageFolder = languageFolder,
            GameLaunchCallsAI = false, WritesPersonalConfiguration = false
        });
        return Task.CompletedTask;
    }

    public async Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (finished || SessionDirectory is null || lease is null) throw new InvalidOperationException("测试会话未开始。");
        if (!order.SequenceEqual(current, StringComparer.Ordinal)) throw new InvalidOperationException("排序不符合已验证的候选方案。");
        ValidateOrder(order); CheckPersonalConfig();
        if (GameProcessService.IsAnyRunning(recordBlocker: true)) throw new InvalidOperationException("游戏正在运行，拒绝重复启动。");
        TestDataRetention.EnsureTestSpace(options.TestRoot);
        var data = ScopedPaths.Child(SessionDirectory, "attempt-" + (++attempts).ToString("D2") + " 测试数据");
        Directory.CreateDirectory(data);
        try
        {
        if (options.UseFixedHelper) helper = new(options, SessionDirectory);
        var testOrder = options.UseFixedHelper ? order.Append(GameProbe.PackageId).ToArray() : order;
        var knownExpansions = catalogue.Installations.Where(mod => mod.Source == ModSource.Game
            && mod.PackageId.StartsWith("ludeon.rimworld.", StringComparison.OrdinalIgnoreCase))
            .Select(mod => mod.PackageId).ToArray();
        GameTestLaunch.WriteConfiguration(data, options.Version.ToString(), testOrder, knownExpansions, languageFolder);
        if (options.UseFixedHelper) GameProbe.Prepare(data,testOrder,downloads);
        var runner = new OrdinaryGameRunner();
        var timeout = TimeSpan.FromSeconds(options.MaximumSeconds);
        var runtimeLog = GameTestLogCapture.RuntimeLogForConfiguration(options.PersonalConfigPath);
        LastReport = SteamGameLaunch.IsSteamInstallation(options.GameDirectory)
            ? await runner.RunSteamAsync(SteamGameLaunch.CreateRequest(GameTestLaunch.Arguments(data)),
                executable, data, catalogue, timeout, progress, token, runtimeLog, options.WarnOnSlowStartup)
            : await runner.RunAsync(GameTestLaunch.Create(executable, data), data, catalogue, timeout, progress, token, runtimeLog, options.WarnOnSlowStartup);
        CheckPersonalConfig();
        token.ThrowIfCancellationRequested();
        progress?.Report(new(LastReport.ProcessId, 0, LastReport.Logs.Sum(log => (long)log.Text.Length))
        { Phase = "diagnostics", StartupReady = LastReport.StartupObserved });
        var logContext = DiagnosticLogContext.Create(LastReport, catalogue, order, token, options.Version);
        // Only failed/error-bearing rounds need code context; successful yellow-only
        // rounds never launch the decompiler or make an extra AI call.
        if (!LastReport.MapSuccessVerified || LastReport.Logs.Any(log => log.Issues.Count > 0))
            logContext = await DiagnosticCodeEvidence.EnrichAsync(logContext, catalogue, order, options.Version,
                ScopedPaths.Child(options.TestRoot, "AnalysisCache"), token);
        // Save the exact sanitized selection for local review/replay; never save an API key or
        // authorize a second paid request just because the first diagnosis was inconclusive.
        OrdinaryGameRunner.WriteNew(ScopedPaths.Child(data, "diagnostic-context.json"), new
        { Schema = 1, Startup = StartupLogDiagnostics.Create(LastReport, data).Context, Logs = logContext });
        var feedback = CreateFeedback(LastReport, logContext, data);
        return options.CollectInsertionEvidence ? feedback with { InsertionEvidence = CaptureInsertionEvidence(LastReport) } : feedback;
        }
        finally
        {
            // Detach as soon as this round has stopped, including cancellation or a
            // diagnostic exception. No helper remains mounted while AI considers a plan.
            helper?.Dispose(); helper = null;
            // The runner has captured its evidence and stopped the owned process before
            // returning. A failed cleanup never replaces the actual game/test exception.
            TestDataRetention.MaintainAfterTest(options.TestRoot);
        }
    }

    internal static MaintenanceGameReport CreateFeedback(OrdinaryGameReport report, AiLogContext logContext, string data)
    {
        var facts = GameTestDiagnostics.Project(report.Logs, logContext.Owners);
        var errors = report.Logs.Where(log => !GameTestDiagnostics.IsHelperDiagnostic(log))
            .SelectMany(log => log.Issues).Select(issue => issue.Summary).Take(100).ToArray();
        if (report.StopReason == GameTestStopReason.ModsReset)
            errors = new[] { "游戏加载异常后自动重置了模组排序，本轮原始组合未通过测试。" }.Concat(errors).Take(100).ToArray();
        if (errors.Length == 0 && facts.Any(f => f.Code == "GameTestBlockingFailure"))
            errors = ["测试流程被包含模组调用的异常中断；异常链及本地归属见本轮日志。"];
        // The helper count includes explicitly approved non-blocking errors. Reconcile only
        // against its own captured messages, never duplicate runtime/stdout copies.
        var excludedProbeErrors = report.CaptureCompleted
            ? report.Logs.Where(log => log.Kind == "game-errors" && !log.Truncated)
                .Select(log => log.ExcludedErrors.Sum(issue => issue.Count)).DefaultIfEmpty(0).Max() : 0;
        if (report.ProbeErrors > excludedProbeErrors && errors.Length == 0)
            errors = ["辅助检测发现游戏错误；完整信息见本轮日志。"];
        if (report.StopReason == GameTestStopReason.Exited && report.ExitCode != 0 && errors.Length == 0)
            errors = ["游戏进程异常退出；请结合本轮启动和运行日志分析。"];
        if (errors.Length > 0 && facts.Count == 0) facts = [new("GameLogError", "")];
        // An authenticated error prefix remains useful in historical log-budget stop records.
        // It may justify a reversible experiment, but never a successful map verdict.
        return new(report.MapSuccessVerified, errors)
        {
            ErrorSignatures = MaintenanceErrorMemory.Capture(report),
            StartupFailure = report.MapSuccessVerified ? null : StartupLogDiagnostics.Create(report, data),
            DiagnosticFacts = facts,
            RuntimeContext = GameTestDiagnostics.ProjectWarnings(report.Logs, report.ProbeWarnings > 0),
            LogContext = logContext,
            LogPaths = report.Logs.Select(l => l.Path).ToArray(),
            CanDiagnose = errors.Length > 0 && facts.Count > 0 && report.CaptureCompleted
                && report.ProcessStopped
                && report.StopReason is not (GameTestStopReason.Cancelled or GameTestStopReason.LaunchFailed)
                && (report.BoundedErrorCaptureVerified || report.StopReason != GameTestStopReason.LogLimit
                    && !report.Logs.Any(log => log.Truncated))
                && report.ProbeStage != "order-mismatch"
                && (report.ProbeStage != "helper-failed" || facts.Any(f => f.PackageId.Length > 0))
                && !GameTestDiagnostics.HasLaunchEnvironmentFailure(report.Logs)
        };
    }

    internal static InsertionEvidence CaptureInsertionEvidence(OrdinaryGameReport report)
    {
        var sources = report.Logs.Where(log => !GameTestDiagnostics.IsHelperDiagnostic(log)).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var complete = report.MapSuccessVerified && sources.Length > 0;
        long capturedProbeErrors = 0;
        foreach (var log in sources)
        {
            Dictionary<string, int>? exact;
            try { exact = PlayerLogReader.ExactErrorCounts(log.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; continue; }
            if (exact is null) { complete = false; continue; }
            if (log.Truncated) complete = false;
            if (log.Kind == "game-errors") capturedProbeErrors = Math.Max(capturedProbeErrors,
                exact.Values.Sum(v => (long)v) + log.ExcludedErrors.Sum(e => (long)e.Count));
            foreach (var pair in exact)
            {
                var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(log.Kind + ":" + pair.Key)));
                counts[key] = Math.Max(counts.GetValueOrDefault(key), pair.Value);
            }
        }
        // A helper-only error without a readable error block cannot be compared safely.
        if (report.ProbeErrors > capturedProbeErrors) complete = false;
        if (counts.Count > 20000) return new(false, [], sources.Select(s => s.Path).Take(20).ToArray());
        return new(complete, counts, sources.Select(s => s.Path).Take(20).ToArray());
    }

    public Task ApplyAsync(AiOrderPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (finished || SessionDirectory is null || !plan.CanApply
            || !plan.Before.SequenceEqual(current, StringComparer.Ordinal)
            || plan.Fingerprint != AiOrderPlanner.Fingerprint(current, catalogue, retained, rules, options.Version))
            throw new InvalidOperationException("AI 排序方案已过期或未通过验证。");
        // A plan cannot remove beyond player approvals or separately validated private trials.
        var required = AiOrderPlanner.DependencyClosure(current, catalogue, options.Version);
        if (current.Any(id => !plan.After.Contains(id, StringComparer.OrdinalIgnoreCase)
                && !approvedRemovals.Contains(id) && !trialRemovals.Contains(id))
            || plan.After.Except(current, StringComparer.OrdinalIgnoreCase).Any(id => !required.Contains(id)))
            throw new InvalidOperationException("一级维护只能调整排序并加入已证实的必要前置。");
        ValidateOrder(plan.After); CheckPersonalConfig();
        OrdinaryGameRunner.WriteNew(ScopedPaths.Child(SessionDirectory, "candidate-" + Guid.NewGuid().ToString("N") + ".json"),
            new { plan.Before, plan.After, plan.Fingerprint });
        current = plan.After.ToArray();
        return Task.CompletedTask;
    }

    internal void AuthorizeConflictRemovals(IReadOnlyList<ConflictRemovalApproval> approvals)
    {
        var expanded = AiOrderPlanner.ExpandRequiredOrder(originalOrder, catalogue, options.Version,allowMissing:true);
        var remaining = DeclaredConflictResolution.Replay(catalogue, expanded, retained, options.Version, approvals);
        approvedRemovals.UnionWith(expanded.Except(remaining, StringComparer.OrdinalIgnoreCase));
    }

    public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials)
    {
        var expanded = AiOrderPlanner.ExpandRequiredOrder(originalOrder, catalogue, options.Version, allowMissing: true);
        var baseline = expanded.Except(approvedRemovals, StringComparer.OrdinalIgnoreCase).ToArray();
        var remaining = TrialExclusionPlanning.Replay(catalogue, baseline, retained, options.Version, trials);
        trialRemovals.Clear();
        trialRemovals.UnionWith(baseline.Except(remaining, StringComparer.OrdinalIgnoreCase));
    }

    public Task FinishAsync(bool acceptTestedOrder, CancellationToken token)
    {
        if (finished) return Task.CompletedTask;
        try
        {
            CheckPersonalConfig();
            if (SessionDirectory is not null)
                OrdinaryGameRunner.WriteNew(ScopedPaths.Child(SessionDirectory, "finished.json"), new
                {
                    PersonalConfigUnchanged = true, TestedOrder = current, Attempts = attempts,
                    CandidateAccepted = acceptTestedOrder && LastReport?.MapSuccessVerified == true,
                    TemporaryExclusions = trialRemovals.ToArray(),
                    ProcessStopped = LastReport?.ProcessStopped, PersonalConfigWritten = false
                });
        }
        finally
        {
            try { helper?.Dispose(); helper = null; }
            finally
            {
                if (lease is not null) TestDataRetention.MaintainAfterTest(options.TestRoot);
                finished = true;
                lease?.Dispose(); lease = null;
                if (inProcessLease) { InProcessLease.Release(); inProcessLease = false; }
            }
        }
        return Task.CompletedTask;
    }

    private void ValidateOrder(IReadOnlyList<string> order)
    {
        if (order.Count is < 1 or > 20000 || order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != order.Count)
            throw new InvalidDataException("测试排序为空、过大或含重复项。");
        var mods = order.Select(id => catalogue.Resolve(id, options.Version).UniqueInstallation
            ?? throw new InvalidDataException("测试模组未安装或有多份安装。")).ToArray();
        foreach (var mod in mods)
        {
            try { ScopedPaths.Ordinary(mod.Directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"无法读取本轮启用模组 {mod.Name}（{mod.PackageId}）。路径：{mod.Directory}。{ex.Message}", ex);
            }
        }
    }

    private string ConfigHash()
    {
        using var file = new FileStream(ScopedPaths.Ordinary(options.PersonalConfigPath, true), FileMode.Open,
            FileAccess.Read, FileShare.Read);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("个人排序配置过大。");
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private void CheckPersonalConfig()
    {
        if (ConfigHash() != originalHash) throw new IOException("个人排序配置在测试期间发生变化；已停止，不覆盖外部修改。");
    }
    private static bool Overlaps(string a, string b)
    {
        a = a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        b = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }
}
