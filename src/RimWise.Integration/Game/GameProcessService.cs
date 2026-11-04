using System.Diagnostics;
using RimWise.Integration.Safety;
using RimWise.Integration.Steam;

namespace RimWise.Integration.Game;

public sealed class GameProcessService : IDisposable
{
    private Process? owned;
    private bool steamRequested;
    public bool IsOwnedRunning => owned is { HasExited: false };
    public string Status => IsOwnedRunning ? $"游戏运行中（PID {owned!.Id}）" : IsAnyRunning() ? "游戏正在运行"
        : steamRequested ? "已请求 Steam 启动游戏" : "游戏未运行";
    public static bool IsAnyRunning(bool recordBlocker = false)
    {
        foreach (var name in new[] { "RimWorld", "RimWorldWin64", "RimWorldWin" })
        {
            var list = Process.GetProcessesByName(name);
            try
            {
                var running = list.FirstOrDefault(IsRunning);
                if (running is null) continue;
                if (recordBlocker)
                {
                    string identity;
                    try { identity = $"PID={running.Id}; startUtc={running.StartTime.ToUniversalTime():O}; executable={running.MainModule?.FileName}"; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    { identity = $"PID={running.Id}; identity unavailable: {ex.GetType().Name}"; }
                    Diagnostics.DiagnosticEventLog.Current?.Record("game-process", "launch-blocked", identity);
                }
                return true;
            }
            finally { foreach (var process in list) process.Dispose(); }
        }
        return false;
    }
    internal static bool IsRunning(Process process)
    {
        try { return !process.HasExited; }
        // An inaccessible process must still protect the player's configuration.
        catch (System.ComponentModel.Win32Exception) { return true; }
        catch (InvalidOperationException) { return true; }
    }
    public static string FindExecutable(string gameDirectory)
    {
        ScopedPaths.Ordinary(gameDirectory, true);
        foreach (var name in new[] { "RimWorldWin64.exe", "RimWorld.exe", "RimWorldWin.exe" })
        {
            var candidate = ScopedPaths.Child(gameDirectory, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("未找到环世界程序，请检查游戏位置。");
    }
    public void Launch(string gameDirectory)
    {
        if (IsOwnedRunning || IsAnyRunning()) throw new InvalidOperationException("游戏正在运行，不会重复启动。");
        var executable = FindExecutable(gameDirectory);
        if (SteamGameLaunch.IsSteamInstallation(gameDirectory))
        {
            SteamGameLaunch.Request(SteamGameLaunch.CreateRequest([]));
            steamRequested = true;
            return;
        }
        steamRequested = false;
        owned?.Dispose();
        owned = Process.Start(new ProcessStartInfo(executable)
        { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false })
            ?? throw new IOException("游戏未能启动。");
    }
    public void Dispose() => owned?.Dispose(); // Closing the manager does not terminate a manually launched game.
}
