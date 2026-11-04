using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

internal sealed record GameTestLaunchIntent(int Schema, string Executable, string Route,
    DateTime PreparedUtc, double TimeoutSeconds);

public sealed record InterruptedGameTest(string SessionId, string AttemptName, bool ProcessFound,
    string Message);

/// <summary>Trusted local recovery. Never starts a program or consumes an old log as a new test result.
/// Inputs come from the current configured installation, not a persisted executable or personal-file path.</summary>
public static class GameTestRecovery
{
    private sealed record SessionRecord(int Schema, string Executable, string OriginalConfigHash);
    private sealed record ProcessRecord(int Id, DateTime StartUtc, string Executable, string Route);
    private sealed record FinishedRecord(bool PersonalConfigUnchanged, bool? ProcessStopped);

    public static IReadOnlyList<InterruptedGameTest> Inspect(OrdinaryGameTestOptions options) =>
        ProcessInterruptedAsync(options, false, CancellationToken.None).GetAwaiter().GetResult();

    public static Task<IReadOnlyList<InterruptedGameTest>> StopInterruptedAsync(OrdinaryGameTestOptions options,
        CancellationToken token) => ProcessInterruptedAsync(options, true, token);

    private static async Task<IReadOnlyList<InterruptedGameTest>> ProcessInterruptedAsync(
        OrdinaryGameTestOptions options, bool stop, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = ScopedPaths.Ordinary(options.TestRoot);
        var executable = GameProcessService.FindExecutable(ScopedPaths.Ordinary(options.GameDirectory, true));
        if (!Directory.Exists(root)) return [];
        using var rootLease = new FileStream(ScopedPaths.Child(root, "ordinary-game-test.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var sessions = Directory.GetDirectories(root, "game-*");
        if (sessions.Length > 5000) throw new InvalidDataException("测试会话过多，请先归档。");
        var result = new List<InterruptedGameTest>();
        foreach (var sessionPath in sessions)
        {
            token.ThrowIfCancellationRequested();
            var id = Path.GetFileName(sessionPath);
            if (!Guid.TryParseExact(id[5..], "N", out _)) continue;
            ScopedPaths.Ordinary(sessionPath, true);
            var finishedPath = ScopedPaths.Child(sessionPath, "finished.json");
            if (File.Exists(finishedPath) && Read<FinishedRecord>(finishedPath) is
                { PersonalConfigUnchanged: true, ProcessStopped: true }) continue;
            var attempts = Directory.GetDirectories(sessionPath, "attempt-*");
            var pending = attempts.Where(data =>
            {
                ScopedPaths.Ordinary(data, true);
                return File.Exists(ScopedPaths.Child(data, "launch.json"))
                    && !File.Exists(ScopedPaths.Child(data, "recovered.json"));
            }).ToArray();
            // A session interrupted before launch has no process to take over. Later manual
            // configuration changes must not turn that empty session into a permanent blocker.
            if (pending.Length == 0) continue;
            var session = Read<SessionRecord>(ScopedPaths.Child(sessionPath, "session.json"));
            // Shared user data can contain tests of another installation. Its journal
            // does not authorize touching that installation or blocking the current one.
            if (!string.Equals(session.Executable, executable, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new(id, "", false, "已跳过其他安装的历史测试记录，未接管其进程。"));
                continue;
            }
            if (session.Schema != 1) throw new InvalidDataException("旧测试会话格式无效，未接管进程。");
            foreach (var data in pending)
            {
                var attempt = Path.GetFileName(data);
                ScopedPaths.Ordinary(data, true);
                var intentPath = ScopedPaths.Child(data, "launch.json");
                // Old diagnostics predate the launch journal and cannot authorize process recovery.
                if (!File.Exists(intentPath) || File.Exists(ScopedPaths.Child(data, "recovered.json"))) continue;
                var intent = Read<GameTestLaunchIntent>(intentPath);
                if (intent.Schema != 1 || intent.PreparedUtc == default || intent.PreparedUtc > DateTime.UtcNow.AddMinutes(1)
                    || intent.TimeoutSeconds is <= 0 or > 1800 || !double.IsFinite(intent.TimeoutSeconds)
                    || intent.Route is not ("steam" or "standalone")
                    || !string.Equals(intent.Executable, executable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("启动意图无效，未接管任何进程。");
                var recordPath = ScopedPaths.Child(data, "process.json");
                var record = File.Exists(recordPath) ? Read<ProcessRecord>(recordPath) : null;
                if (record is not null && (record.Id <= 0 || record.StartUtc < intent.PreparedUtc
                    || record.Route != intent.Route || !string.Equals(record.Executable, executable, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("进程记录与启动意图不一致。");
                string? failure = null;
                using var process = SteamTestProcess.Find(executable, data, intent.PreparedUtc,
                    text => failure = text, record?.Id, record?.StartUtc);
                if (failure is not null) throw new IOException("旧进程归属无法验证：" + failure);
                if (process is null)
                {
                    // Missing after reboot or PID reuse: never terminate another process or
                    // interpret the previous attempt's logs as a completed new test.
                    result.Add(new(id, attempt, false, "旧测试进程已不存在；继续时需使用新目录重新测试。"));
                }
                else
                {
                    // A dead attempt cannot overwrite the personal order. Only a live
                    // takeover needs the old configuration-integrity precondition.
                    using var config = File.OpenRead(ScopedPaths.Ordinary(options.PersonalConfigPath, true));
                    if (config.Length > 4 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(config)) != session.OriginalConfigHash)
                        throw new IOException("个人排序已变化，未接管旧测试；不会覆盖该配置。");
                    if (stop) await OrdinaryGameRunner.StopOwnedAsync(process);
                    result.Add(new(id, attempt, true, stop ? "已结束经身份核对的旧测试进程。" : "发现仍在运行的旧测试进程。"));
                }
                if (stop)
                    OrdinaryGameRunner.WriteNew(ScopedPaths.Child(data, "recovered.json"),
                        new { RecoveredUtc = DateTime.UtcNow, Matched = process is not null, OldLogsAreNewEvidence = false });
            }
        }
        return result;
    }

    private static T Read<T>(string path)
    {
        using var file = File.OpenRead(ScopedPaths.Ordinary(path, true));
        if (file.Length is 0 or > 64 * 1024) throw new InvalidDataException("测试恢复记录大小无效。");
        return JsonSerializer.Deserialize<T>(file, new JsonSerializerOptions { MaxDepth = 16 })
            ?? throw new InvalidDataException("测试恢复记录为空。");
    }
}
