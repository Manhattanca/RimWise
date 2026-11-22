using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

// Counts and fixed reason codes only. Never persist another application's command line.
internal sealed record SteamLaunchObservation(int Candidates = 0, int Exited = 0, int PredatesRequest = 0,
    int ExecutableMismatch = 0, int ArgumentsMismatch = 0, int IdentityUnavailable = 0, int Matched = 0)
{
    public string Phase => Matched > 0 ? "steam-matched"
        : IdentityUnavailable > 0 ? "steam-identity-unavailable"
        : ArgumentsMismatch > 0 ? "steam-arguments-mismatch"
        : PredatesRequest > 0 ? "steam-existing-process"
        : ExecutableMismatch > 0 ? "steam-installation-mismatch" : "steam-request";

    internal string Describe() => Phase switch
    {
        "steam-matched" => "已确认本轮测试进程。",
        "steam-identity-unavailable" => "发现游戏候选进程，但无法读取其身份；请检查游戏与 RimWise 的运行权限是否一致。",
        "steam-arguments-mismatch" => "发现游戏候选进程，但其启动参数不属于本轮测试；不会接管，请检查 Steam 启动确认窗口。",
        "steam-existing-process" => "发现请求之前已运行的游戏进程，不会将其当作本轮测试或将其关闭。",
        "steam-installation-mismatch" => "发现同名游戏进程，但不属于当前选择的安装目录；不会接管。",
        _ => "尚未发现可核验的游戏候选进程；请检查 Steam 是否仍在准备启动、更新或等待确认。"
    };
}

internal sealed class SteamLaunchDeadline(TimeSpan timeout)
{
    internal static TimeSpan Limit(TimeSpan testTimeout) => testTimeout < TimeSpan.FromMinutes(3)
        ? testTimeout : TimeSpan.FromMinutes(3);
    private TimeSpan? cancelledAt;
    // Cancellation still scans first, and keeps the previous bounded 45-second ownership
    // cleanup window. It must not wait the entire extended cold-start allowance.
    internal bool Expired(TimeSpan elapsed, bool cancelled)
    {
        if (cancelled) cancelledAt ??= elapsed;
        return elapsed >= timeout || cancelledAt is { } at && elapsed - at >= TimeSpan.FromSeconds(45);
    }
}

internal sealed class SteamLaunchJournal(string data)
{
    private SteamLaunchObservation? last;
    private TimeSpan writtenAt;
    private int lines;
    internal void Observe(SteamLaunchObservation value, TimeSpan elapsed)
    {
        if (lines >= 64 || value == last && elapsed - writtenAt < TimeSpan.FromSeconds(5)) return;
        TryWrite(() => File.AppendAllText(ScopedPaths.Ordinary(ScopedPaths.Child(data, "steam-observation.jsonl")),
            JsonSerializer.Serialize(new { Schema = 1, ElapsedSeconds = elapsed.TotalSeconds, Observation = value }) + "\n"));
        last = value; writtenAt = elapsed; lines++;
    }
    internal void Finish(string outcome, SteamLaunchObservation value, TimeSpan elapsed, int processId = 0) =>
        TryWrite(() => OrdinaryGameRunner.WriteNew(Path.Combine(data, "steam-result.json"), new
        { Schema = 1, Outcome = outcome, ElapsedSeconds = elapsed.TotalSeconds, ProcessId = processId, Observation = value }));

    private static void TryWrite(Action write)
    {
        // Optional diagnostics cannot abandon a game process after Steam has started it.
        try { write(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
