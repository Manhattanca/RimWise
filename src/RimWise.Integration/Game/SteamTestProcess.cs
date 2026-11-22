using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RimWise.Integration.Steam;

namespace RimWise.Integration.Game;

/// <summary>Recognizes a Steam-started test by its executable, creation time and unique data argument.</summary>
internal static class SteamTestProcess
{
    internal static async Task<Process> StartAsync(ProcessStartInfo request, string executable, string data,
        TimeSpan timeout, CancellationToken token, Action<int, string>? waiting = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        token.ThrowIfCancellationRequested();
        var since = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        var deadline = new SteamLaunchDeadline(timeout);
        var journal = new SteamLaunchJournal(data);
        var observation = new SteamLaunchObservation();
        string? lastFailure = null;
        OrdinaryGameRunner.WriteNew(Path.Combine(data, "steam-request.json"), new
        { AppId = SteamGameLaunch.AppId, PreparedUtc = since, WaitSeconds = timeout.TotalSeconds, AI = false });
        try
        {
            SteamGameLaunch.Request(request);
            while (true)
            {
                // Even after cancellation, acquire an already-created matching process first,
                // so the caller can clean up its own game. Steam itself is never terminated.
                var process = Find(executable, data, since, message => lastFailure = message,
                    observed: value => observation = value);
                journal.Observe(observation, watch.Elapsed);
                if (process is not null)
                {
                    journal.Finish("Matched", observation, watch.Elapsed, process.Id);
                    return process;
                }
                waiting?.Invoke((int)watch.Elapsed.TotalSeconds, token.IsCancellationRequested
                    ? "steam-cancelling" : observation.Phase);
                if (deadline.Expired(watch.Elapsed, token.IsCancellationRequested))
                {
                    token.ThrowIfCancellationRequested();
                    throw new IOException($"已向 Steam 请求启动，等待 {watch.Elapsed.TotalSeconds:F0} 秒后仍未确认带本轮测试参数的游戏进程。"
                        + observation.Describe() + "不会接管或终止 Steam／其他游戏。" + lastFailure);
                }
                await Task.Delay(250, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { journal.Finish("Cancelled", observation, watch.Elapsed); throw; }
        catch { journal.Finish("Unconfirmed", observation, watch.Elapsed); throw; }
    }

    [SupportedOSPlatform("windows")]
    internal static Process? Find(string executable, string data, DateTime since, Action<string> failure,
        int? processId = null, DateTime? expectedStart = null, Action<SteamLaunchObservation>? observed = null)
    {
        Process? match = null;
        Process[] candidates;
        try { candidates = processId is { } pid ? [Process.GetProcessById(pid)]
            : Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)); }
        catch (ArgumentException) { observed?.Invoke(new()); return null; }
        var observation = new SteamLaunchObservation(Candidates: candidates.Length);
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    // Keep a real handle open while checking arguments; never use a recycled PID to kill.
                    _ = process.Handle;
                    if (process.HasExited) { observation = observation with { Exited = observation.Exited + 1 }; continue; }
                    if (process.StartTime.ToUniversalTime() < since
                        || expectedStart is { } exact && process.StartTime.ToUniversalTime() != exact)
                    { observation = observation with { PredatesRequest = observation.PredatesRequest + 1 }; continue; }
                    if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    { observation = observation with { ExecutableMismatch = observation.ExecutableMismatch + 1 }; continue; }
                    string? commandLine;
                    try { commandLine = WindowsProcessCommandLine.Read(process); }
                    catch (NotSupportedException) { commandLine = ReadCommandLine(process.Id); }
                    if (process.HasExited) { observation = observation with { Exited = observation.Exited + 1 }; continue; }
                    if (!Matches(commandLine, executable, data))
                    { observation = observation with { ArgumentsMismatch = observation.ArgumentsMismatch + 1 }; continue; }
                    observation = observation with { Matched = observation.Matched + 1 };
                    if (match is not null) throw new IOException("Steam 启动了多个匹配测试进程，不能安全确认归属。");
                    match = process;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException)
                {
                    observation = observation with { IdentityUnavailable = observation.IdentityUnavailable + 1 };
                    failure($" 进程验证失败：{ex.GetType().Name} (0x{ex.HResult:X8})。");
                }
            }
            return match;
        }
        catch
        {
            match?.Dispose();
            throw;
        }
        finally
        {
            observed?.Invoke(observation);
            foreach (var process in candidates)
                if (!ReferenceEquals(process, match)) process.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool Matches(string? commandLine, string executable, string data)
    {
        if (string.IsNullOrEmpty(commandLine)) return false;
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var args = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "").ToArray();
            return args.Length > 0 && string.Equals(args[0], executable, StringComparison.OrdinalIgnoreCase)
                && args.Count(a => a.StartsWith("-savedatafolder=", StringComparison.OrdinalIgnoreCase)) == 1
                && args.Any(a => a.Equals("-savedatafolder=" + data, StringComparison.OrdinalIgnoreCase))
                // Accept the previous fixed argument set only to recover owned old attempts.
                && args.Count(a => a.Equals("-quicktest", StringComparison.OrdinalIgnoreCase)) <= 1
                && args.Count(a => a.Equals("-logFile", StringComparison.OrdinalIgnoreCase)) == 1
                && Enumerable.Range(1, args.Length - 1).Any(i => args[i - 1].Equals("-logFile", StringComparison.OrdinalIgnoreCase)
                    && args[i].Equals(Path.Combine(data, "Player.log"), StringComparison.OrdinalIgnoreCase));
        }
        finally { LocalFree(pointer); }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadCommandLine(int pid)
    {
        // Query only this candidate's command line. Do not enumerate commands of unrelated applications.
        object? locator = null, services = null, results = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!);
            services = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            results = ((dynamic)services!).ExecQuery($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}", "WQL", 48);
            foreach (var item in (System.Collections.IEnumerable)results!)
            {
                try { return (string?)((dynamic)item).CommandLine; }
                finally { Release(item); }
            }
            return null;
        }
        finally { Release(results); Release(services); Release(locator); }

        static void Release(object? value)
        { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
