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
        TimeSpan timeout, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        token.ThrowIfCancellationRequested();
        var since = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        string? lastFailure = null;
        OrdinaryGameRunner.WriteNew(Path.Combine(data, "steam-request.json"), new
        { AppId = SteamGameLaunch.AppId, PreparedUtc = since, WaitSeconds = timeout.TotalSeconds, AI = false });
        SteamGameLaunch.Request(request);
        while (true)
        {
            // Even after cancellation, first try to acquire an already-created matching process,
            // so the caller can clean up its own game. Steam itself is never terminated.
            var process = Find(executable, data, since, message => lastFailure = message);
            if (process is not null) return process;
            if (watch.Elapsed >= timeout)
            {
                token.ThrowIfCancellationRequested();
                throw new IOException("已向 Steam 请求启动，但未确认带本轮测试参数的游戏进程。不会接管或终止 Steam／其他游戏；请检查 Steam 是否有启动提示。" + lastFailure);
            }
            await Task.Delay(250, CancellationToken.None);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static Process? Find(string executable, string data, DateTime since, Action<string> failure,
        int? processId = null, DateTime? expectedStart = null)
    {
        Process? match = null;
        Process[] candidates;
        try { candidates = processId is { } pid ? [Process.GetProcessById(pid)]
            : Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)); }
        catch (ArgumentException) { return null; }
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    // Keep a real handle open while checking arguments; never use a recycled PID to kill.
                    _ = process.Handle;
                    if (process.HasExited || process.StartTime.ToUniversalTime() < since
                        || expectedStart is { } exact && process.StartTime.ToUniversalTime() != exact
                        || !string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) continue;
                    string? commandLine;
                    try { commandLine = WindowsProcessCommandLine.Read(process); }
                    catch (NotSupportedException) { commandLine = ReadCommandLine(process.Id); }
                    if (process.HasExited || !Matches(commandLine, executable, data)) continue;
                    if (match is not null) throw new IOException("Steam 启动了多个匹配测试进程，不能安全确认归属。");
                    match = process;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException)
                { failure($" 进程验证失败：{ex.GetType().Name} (0x{ex.HResult:X8})。"); }
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
