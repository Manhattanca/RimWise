using RimWise.Integration.Game;

namespace RimWise.Desktop.Services;

internal static class GameTestStatus
{
    internal static string Describe(OrdinaryGameProgress progress)
    {
        var stage = progress.Phase switch
        {
            "steam-request" => "已请求 Steam 启动游戏，正在确认测试进程；请留意 Steam 的启动提示",
            "steam-identity-unavailable" => "发现游戏进程，正在重试身份核验；请检查游戏与 RimWise 的运行权限是否一致",
            "steam-arguments-mismatch" => "发现游戏进程，但启动参数不属于本轮测试；请检查 Steam 启动确认窗口，不会接管该进程",
            "steam-existing-process" => "发现此前已运行的游戏进程，不会将其当作本轮测试；正在等待本轮进程",
            "steam-installation-mismatch" => "发现其他安装目录的同名游戏进程，不会接管；正在等待当前安装的测试进程",
            "steam-cancelling" => "正在取消 Steam 启动等待并核对迟到的测试进程；不会关闭 Steam 或其他游戏",
            "process-start" => "正在启动游戏测试进程",
            "launch-failed" => "未能确认游戏测试进程；请检查 Steam 登录及启动提示，测试未通过",
            "diagnostics" => "本轮游戏测试已结束，正在整理日志与模组关联证据…",
            "mods-reset" => "游戏加载异常并重置了模组排序，正在收集异常链",
            "main-menu" => "正在检查主界面和加载报错",
            "scenario-selection" => "正在选择测试剧本",
            "storyteller-selection" => "正在选择叙述者和难度",
            "starting-site" => "正在选择随机落点",
            "ideology-selection" => "正在配置测试理念",
            "starting-pawns" => "正在检查随机开局人物",
            "game-configuration" => "正在完成开局配置",
            "world-generation" => "正在生成测试世界",
            "faction-check" => "正在检查派系生成",
            "waiting-map" => "正在生成测试地图",
            "pawn-test" => "正在随机选择小人并检查",
            "map-running" => $"正在验证地图和小人运行 · {progress.MapTicks}/120 ticks",
            _ => progress.StartupReady ? "主界面已加载，正在检查测试阶段" : "测试进程已启动，正在加载游戏"
        };
        var status = stage + $"\n本轮已等待 {TimeSpan.FromSeconds(progress.ElapsedSeconds):mm\\:ss} · 日志 {progress.LogBytes / 1024:N0} KB";
        if (progress.ProcessId == 0 && progress.Phase?.StartsWith("steam-", StringComparison.Ordinal) == true)
            status += progress.Phase == "steam-cancelling"
                ? "\n正在完成最多 45 秒的安全收尾，请勿在此期间手动启动游戏。"
                : "\n最多等待 3 分钟确认启动；Steam 的确认窗口可能在后台，可取消测试。";
        if (progress.ProcessId > 0 && (progress.Phase is null or "loading") && !progress.StartupReady)
            status += progress.LogBytes == 0
                ? "\n尚未收到游戏日志和测试阶段信号；可继续等待，或取消后导出日志。"
                : "\n已收到游戏日志，尚未收到测试阶段信号；正在等待加载。";
        if (progress.ProcessId > 0 && progress.LogBytes > 0)
            status += progress.SecondsSinceLogChange == 0 ? " · 日志正在更新" : $" · 距上次日志变化 {progress.SecondsSinceLogChange} 秒";
        if (progress.StartupDelayWarning) status += "\n启动已等待 30 分钟，可能已卡住；可继续等待或取消测试。";
        return status;
    }
}
