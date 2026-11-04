using System.ComponentModel;
using System.Diagnostics;

namespace RimWise.Integration.Game;

/// <summary>Holds verified handles across a user's close confirmation. Never follows a reused PID.</summary>
public sealed class GameCloseConfirmation : IDisposable
{
    private readonly List<Process> processes = [];
    public int Count => processes.Count;

    public static GameCloseConfirmation Capture(string gameDirectory)
    {
        string? executable = null;
        var captured = new GameCloseConfirmation();
        try
        {
            foreach (var name in new[] { "RimWorld", "RimWorldWin64", "RimWorldWin" })
            {
                var candidates = Process.GetProcessesByName(name);
                try
                {
                    foreach (var process in candidates)
                    {
                        if (!GameProcessService.IsRunning(process)) continue;
                        try
                        {
                            _ = process.Handle;
                            if (process.HasExited) continue;
                            executable ??= GameProcessService.FindExecutable(gameDirectory);
                            if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                                throw new IOException($"检测到其他游戏安装正在运行（PID {process.Id}）；请保存并关闭该游戏后再开始排序。");
                            captured.processes.Add(process);
                        }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                        {
                            if (!GameProcessService.IsRunning(process)) continue;
                            throw new IOException($"无法核对游戏进程（PID {process.Id}），请保存并关闭游戏后再开始排序。", ex);
                        }
                    }
                }
                finally
                {
                    foreach (var process in candidates)
                        if (!captured.processes.Contains(process)) process.Dispose();
                }
            }
            return captured;
        }
        catch { captured.Dispose(); throw; }
    }

    // The caller invokes this only after explicit confirmation. No forced termination:
    // a hung game or an in-game confirmation must not cause loss of an unsaved session.
    public async Task CloseAsync(CancellationToken token)
    {
        foreach (var process in processes)
        {
            token.ThrowIfCancellationRequested();
            if (!GameProcessService.IsRunning(process)) continue;
            try
            {
                if (!process.CloseMainWindow() && GameProcessService.IsRunning(process))
                    throw new IOException($"游戏尚不能正常关闭（PID {process.Id}），请确认游戏已保存并手动关闭，再开始排序。");
                await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
            }
            catch (TimeoutException ex)
            { throw new IOException("游戏仍未退出，可能正在等待游戏内确认。未开始排序，请处理游戏窗口后重试。", ex); }
            catch (InvalidOperationException) when (!GameProcessService.IsRunning(process)) { }
        }
        if (GameProcessService.IsAnyRunning())
            throw new IOException("检测到另一个游戏进程正在运行，未开始排序，也未关闭未经确认的新进程。");
    }

    public void Dispose()
    {
        foreach (var process in processes) process.Dispose();
        processes.Clear();
    }
}
