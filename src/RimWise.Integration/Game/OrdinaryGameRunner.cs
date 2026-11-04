using System.Diagnostics;
using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

// Preserve LogLimit/ErrorDetected ordinals for old recovery records; new runs never emit them.
public enum GameTestStopReason { Exited, TimedOut, LogLimit, Cancelled, ProbeCompleted, LaunchFailed, ErrorDetected }
public sealed record OrdinaryGameProgress(int ProcessId, int ElapsedSeconds, long LogBytes)
{
    public bool StartupReady { get; init; }
    public bool StartupDelayWarning { get; init; }
    public string? Phase { get; init; }
    public int MapTicks { get; init; }
}
public sealed record OrdinaryGameReport(int ProcessId, int ExitCode, GameTestStopReason StopReason,
    bool ProcessStopped, bool CaptureCompleted, IReadOnlyList<GameLogSnapshot> Logs)
{
    // A process handle, empty log, or generic game message is not a trusted map-completion signal.
    public bool MapSuccessVerified => ProbeStage == "map-ready" && ProbeTicks >= 120 && ProbeColonists > 0
        && ProcessStopped && CaptureCompleted && StopReason == GameTestStopReason.ProbeCompleted
        && MenuPassed && FactionsPassed && PawnsTested > 0
        && !Logs.Any(log => log.Truncated) && !LaunchEnvironmentFailure;
    public bool MenuPassed { get; init; }
    public bool FactionsPassed { get; init; }
    public int PawnsTested { get; init; }
    public string? FailedPhase { get; init; }
    public string? ProbeStage { get; init; }
    public int ProbeTicks { get; init; }
    public int ProbeColonists { get; init; }
    public int ProbeErrors { get; init; }
    public int ProbeWarnings { get; init; }
    public string? StartFailureCode { get; init; }
    public bool StoppedByTester { get; init; }
    public bool StartupObserved { get; init; }
    public bool LaunchEnvironmentFailure => GameTestDiagnostics.HasLaunchEnvironmentFailure(Logs);
    // Historical resource stops can preserve positive error evidence, never proof of a clean run.
    public bool BoundedErrorCaptureVerified => StopReason == GameTestStopReason.LogLimit
        && ProcessStopped && CaptureCompleted && Logs.Any(log => log.Text.Length > 0 && log.Issues.Count > 0)
        && ProbeStage != "order-mismatch" && !LaunchEnvironmentFailure;
}

/// <summary>Owns only the Process object it starts. No process-name based termination, shell or AI-supplied command.</summary>
internal sealed class OrdinaryGameRunner
{
    internal Task<OrdinaryGameReport> RunAsync(ProcessStartInfo info, string data, ModCatalogue catalogue,
        TimeSpan timeout, IProgress<OrdinaryGameProgress>? progress, CancellationToken token, string? runtimeLog = null,
        bool warnOnSlowStartup = false) =>
        RunCoreAsync(_ => Task.FromResult(Process.Start(info) ?? throw new IOException("游戏未返回测试进程。")),
            info.FileName, "standalone", true, data, catalogue, timeout, progress, token, runtimeLog, warnOnSlowStartup);

    internal Task<OrdinaryGameReport> RunSteamAsync(ProcessStartInfo request, string executable, string data, ModCatalogue catalogue,
        TimeSpan timeout, IProgress<OrdinaryGameProgress>? progress, CancellationToken token, string? runtimeLog = null,
        bool warnOnSlowStartup = false) =>
        RunCoreAsync(t => SteamTestProcess.StartAsync(request, executable, data,
            timeout < TimeSpan.FromSeconds(45) ? timeout : TimeSpan.FromSeconds(45), t),
            executable, "steam", false, data, catalogue, timeout, progress, token, runtimeLog, warnOnSlowStartup);

    private static async Task<OrdinaryGameReport> RunCoreAsync(Func<CancellationToken, Task<Process>> start,
        string executable, string route, bool redirect, string data, ModCatalogue catalogue,
        TimeSpan timeout, IProgress<OrdinaryGameProgress>? progress, CancellationToken token, string? runtimeLog,
        bool warnOnSlowStartup)
    {
        token.ThrowIfCancellationRequested();
        var stdoutPath = ScopedPaths.Child(data, "stdout.log");
        var stderrPath = ScopedPaths.Child(data, "stderr.log");
        var logPath = ScopedPaths.Child(data, "Player.log");
        var runtimeCopy = ScopedPaths.Child(data, "runtime-Player.log");
        if (new[] { stdoutPath, stderrPath, logPath, runtimeCopy }.Any(File.Exists))
            throw new IOException("测试目录含旧日志，拒绝复用。");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(warnOnSlowStartup ? 30 : 10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        // If the parent dies before process.json is published, recovery still has a unique
        // launch intent. This journal never supplies an executable to the launcher.
        WriteNew(ScopedPaths.Child(data, "launch.json"), new GameTestLaunchIntent(1, executable, route,
            DateTime.UtcNow, timeout.TotalSeconds));
        var watch = Stopwatch.StartNew();
        var nonce = GameProbe.ReadNonce(data);
        var failureCapture = new GameFailureLogCapture(executable, data, runtimeLog);
        GameProbeSignal? signal = null;
        Process started;
        try { started = await start(token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            // Preserve a failed launch as an attempt, so the controller can diagnose it instead of
            // falling through its generic exception handler. Never claim an unobserved process was killed.
            var failurePath = ScopedPaths.Child(data, "launch-failure.log");
            File.WriteAllText(failurePath, ex.GetType().Name + ": " + ex.Message);
            var failureLogs = await failureCapture.CaptureAsync(0, nonce, catalogue, true, token);
            var failure = new OrdinaryGameReport(0, -1, GameTestStopReason.LaunchFailed, false, false,
                new[] { logPath, stdoutPath, stderrPath, failurePath }.Where(File.Exists)
                    .Select(p => PlayerLogReader.Read(p, catalogue)).Concat(failureLogs).ToArray())
            { StartFailureCode = route == "steam" ? "SteamProcessNotObserved" : "ProcessStartFailed" };
            WriteNew(ScopedPaths.Child(data, "result.json"), failure);
            return failure;
        }
        using var process = started;
        GameTestLogCapture runtimeCapture;
        // Every operation after Start is protected by cleanup, including journal and capture creation failures.
        using var captureCancel = new CancellationTokenSource();
        Task? capture = null;
        var reason = GameTestStopReason.Exited;
        var captureCompleted = false;
        var startupReady = false;
        var stoppedByTester = false;
        string? lastPhase = null;
        try
        {
            runtimeCapture = new GameTestLogCapture(executable, data, process.Id, process.StartTime.ToUniversalTime(), runtimeLog);
            WriteNew(ScopedPaths.Child(data, "process.json"), new { process.Id,
                StartUtc = process.StartTime.ToUniversalTime(), Executable = executable, Route = route,
                TimeoutSeconds = timeout.TotalSeconds, StandardStreamsCaptured = redirect });
            // Steam owns the game's stdout/stderr handles. Never substitute Steam's output for game logs.
            capture = redirect ? Task.WhenAll(CopyAsync(process.StandardOutput.BaseStream, stdoutPath),
                CopyAsync(process.StandardError.BaseStream, stderrPath)) : Task.CompletedTask;
            while (!process.HasExited)
            {
                if (token.IsCancellationRequested) { reason = GameTestStopReason.Cancelled; break; }
                if (nonce is not null && (signal = GameProbe.Read(data, nonce, process.Id)) is not null)
                { reason = GameTestStopReason.ProbeCompleted; break; }
                if (!startupReady && nonce is not null)
                    startupReady = GameProbe.StartupReady(data, nonce, process.Id);
                var phase = nonce is null ? null : GameProbe.Progress(data, nonce, process.Id);
                lastPhase = phase?.Stage ?? lastPhase;
                var playerBytes = LogLength(logPath);
                if (playerBytes == 0) playerBytes = runtimeCapture.CorrelatedLength();
                var bytes = playerBytes + LogLength(stdoutPath) + LogLength(stderrPath);
                if (capture.IsFaulted) await capture;
                // Logged errors are evidence, not proof that the test cannot proceed. Let the
                // helper finish or report an actual blocked stage, then collect the final logs.
                // ErrorDetected remains readable for historical recovery records only.
                progress?.Report(new(process.Id, (int)watch.Elapsed.TotalSeconds, bytes)
                {
                    StartupReady = startupReady,
                    Phase = phase?.Stage,
                    MapTicks = phase?.Ticks ?? 0,
                    StartupDelayWarning = warnOnSlowStartup && !startupReady && watch.Elapsed >= timeout
                });
                // Product testing has a startup reminder, never a total-duration kill timer.
                // Keep this process and attempt alive; no AI diagnosis/restart can run while it loads.
                if (!warnOnSlowStartup && watch.Elapsed >= timeout) { reason = GameTestStopReason.TimedOut; break; }
                await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
            }
        }
        finally
        {
            try { stoppedByTester = !process.HasExited; await StopOwnedAsync(process); }
            finally
            {
                if (capture is not null)
                {
                    try { await capture.WaitAsync(TimeSpan.FromSeconds(3)); captureCompleted = true; }
                    catch (TimeoutException) { captureCancel.Cancel(); }
                    finally
                    {
                        captureCancel.Cancel();
                        // Observe pipe tasks even when process termination itself failed.
                        try { await capture.WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch (OperationCanceledException) { }
                        catch (TimeoutException) { throw new IOException("测试日志管道未能关闭。"); }
                    }
                }
            }
        }
        // A crash/fast exit can race the last loop iteration. Preserve its fresh final signal.
        if (nonce is not null && signal is null && reason == GameTestStopReason.Exited)
        {
            signal = GameProbe.Read(data,nonce,process.Id);
            if (signal is not null) reason = GameTestStopReason.ProbeCompleted;
        }
        // A default runtime log is read only after proving its PID and unique test arguments.
        // Copy the complete correlated log; file size never changes the test outcome.
        if (runtimeCapture.CopyCorrelated(runtimeCopy))
        {
            WriteNew(ScopedPaths.Child(data, "log-source.json"), new
            { Kind = "correlated-runtime-log", ProcessId = process.Id, Limited = false });
        }
        if (token.IsCancellationRequested) reason = GameTestStopReason.Cancelled;
        var extraLogs = await failureCapture.CaptureAsync(process.Id, nonce, catalogue,
            reason == GameTestStopReason.Exited && process.ExitCode != 0, token,
            File.Exists(runtimeCopy) ? runtimeLog : null);
        var logs = new[] { logPath, runtimeCopy, stdoutPath, stderrPath, ScopedPaths.Child(data,"workbench-helper-failure.txt") }.Where(File.Exists)
            .Select(path => PlayerLogReader.Read(path, catalogue)).Concat(extraLogs).ToArray();
        var fallback = ScopedPaths.Child(data, "workbench-messages.log");
        if (File.Exists(fallback))
        {
            var extra = PlayerLogReader.Read(fallback, catalogue);
            // A primary log with an early error may still miss a later in-game message.
            // Merge by issue, rather than discarding the entire fallback severity.
            var primaryErrors = logs.SelectMany(l => l.Issues).Select(issue => issue.Summary).ToHashSet(StringComparer.Ordinal);
            var primaryWarnings = logs.SelectMany(l => l.Warnings).Select(issue => issue.Summary).ToHashSet(StringComparer.Ordinal);
            logs = logs.Append(extra with
            {
                Kind = "game-errors",
                Issues = extra.Issues.Where(issue => !primaryErrors.Contains(issue.Summary)).ToArray(),
                Warnings = extra.Warnings.Where(issue => !primaryWarnings.Contains(issue.Summary)).ToArray()
            }).ToArray();
        }
        captureCompleted &= redirect || logs.Any(log => log.Text.Length > 0);
        var report = new OrdinaryGameReport(process.Id, process.ExitCode, reason, process.HasExited, captureCompleted, logs)
        { StoppedByTester = stoppedByTester, StartupObserved = startupReady,
            ProbeStage = signal?.Stage ?? (File.Exists(ScopedPaths.Child(data, "workbench-helper-failure.txt"))
                ? "helper-failed" : null), ProbeTicks = signal?.Ticks ?? 0, ProbeColonists = signal?.Colonists ?? 0,
            ProbeErrors = signal?.Errors ?? 0, ProbeWarnings = signal?.Warnings ?? 0,
            MenuPassed = signal?.MenuPassed ?? false, FactionsPassed = signal?.FactionsPassed ?? false,
            PawnsTested = signal?.PawnsTested ?? 0, FailedPhase = signal?.Phase ?? lastPhase };
        WriteNew(ScopedPaths.Child(data, "result.json"), report);
        return report;

        static long LogLength(string path) => File.Exists(path)
            ? new FileInfo(ScopedPaths.Ordinary(path, true)).Length : 0;

        async Task CopyAsync(Stream input, string path)
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await input.CopyToAsync(output, captureCancel.Token);
        }
    }

    internal static async Task StopOwnedAsync(Process process)
    {
        if (process.HasExited) return;
        // The original Process handle prevents PID reuse from targeting an unrelated process.
        process.CloseMainWindow();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    internal static void WriteNew<T>(string path, T value)
    {
        path = ScopedPaths.Ordinary(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(flushToDisk: true);
    }
}
