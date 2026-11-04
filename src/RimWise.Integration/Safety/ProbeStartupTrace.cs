using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace RimWise.Integration.Safety;

/// <summary>
/// Development evidence for our synthetic child only. Does not inject code, enable
/// privileges, change loader registry settings, or attach to an existing process.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProbeStartupTrace
{
    internal const uint DebugOnlyThisProcess = 2;
    private const uint Continue = 0x00010002, NotHandled = 0x80010001;

    internal static uint Observe(IntPtr process, uint processId, string evidenceFile)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("启动跟踪仅支持 x64 诊断宿主。");
        using var output = new FileStream(evidenceFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var timer = Stopwatch.StartNew();
        // DEBUG_EVENT's union begins at 16 on x64. Its largest member is
        // EXCEPTION_DEBUG_INFO (160 bytes). Keep the native buffer explicitly sized.
        var debugEvent = Marshal.AllocHGlobal(176);
        var count = 0;
        var initialBreakpointSeen = false;
        try
        {
            while (timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (!Native.WaitForDebugEventEx(debugEvent, 250))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 121) continue; // ERROR_SEM_TIMEOUT
                    throw new Win32Exception(error, "WaitForDebugEventEx");
                }
                var kind = unchecked((uint)Marshal.ReadInt32(debugEvent));
                var pid = unchecked((uint)Marshal.ReadInt32(debugEvent, 4));
                var thread = unchecked((uint)Marshal.ReadInt32(debugEvent, 8));
                var data = debugEvent + 16;
                uint continuation = Continue;
                uint? exit = null;
                try
                {
                    if (pid != processId) throw new IOException("启动跟踪收到非目标进程事件。");
                    if (++count > 4096) throw new IOException("启动跟踪超过事件上限。");
                    switch (kind)
                    {
                        case 1: // EXCEPTION_DEBUG_EVENT
                            var code = unchecked((uint)Marshal.ReadInt32(data));
                            Save(new { Kind = "exception", Code = $"0x{code:X8}",
                                Address = Address(Marshal.ReadIntPtr(data, 16)),
                                FirstChance = Marshal.ReadInt32(data, 152) != 0 });
                            // Continue debugger notification only; never swallow an application fault.
                            if (code == 0x80000003 && !initialBreakpointSeen)
                            { initialBreakpointSeen = true; continuation = Continue; }
                            else continuation = NotHandled;
                            break;
                        case 3: // CREATE_PROCESS_DEBUG_EVENT
                            SaveModule("process-image", Marshal.ReadIntPtr(data), Marshal.ReadIntPtr(data, 24));
                            break;
                        case 5: // EXIT_PROCESS_DEBUG_EVENT
                            exit = unchecked((uint)Marshal.ReadInt32(data));
                            Save(new { Kind = "exit", Code = $"0x{exit.Value:X8}" });
                            break;
                        case 6: // LOAD_DLL_DEBUG_EVENT
                            SaveModule("load-dll", Marshal.ReadIntPtr(data), Marshal.ReadIntPtr(data, 8));
                            break;
                        case 8: // OUTPUT_DEBUG_STRING_EVENT
                            Save(new { Kind = "debug-string", Text = DebugString(process, data) });
                            break;
                        default:
                            Save(new { Kind = "event", Code = kind });
                            break;
                    }
                }
                finally
                {
                    if (!Native.ContinueDebugEvent(pid, thread, continuation))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "ContinueDebugEvent");
                }
                if (exit.HasValue) return exit.Value;
            }
            Save(new { Kind = "timeout", Milliseconds = timer.ElapsedMilliseconds });
            throw new IOException("启动跟踪超时。");
        }
        finally { Marshal.FreeHGlobal(debugEvent); }

        void Save<T>(T item)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item);
            output.Write(bytes); output.WriteByte((byte)'\n'); output.Flush(flushToDisk: true);
        }
        void SaveModule(string kind, IntPtr file, IntPtr address)
        {
            try
            {
                var path = new StringBuilder(32768);
                var length = file == IntPtr.Zero ? 0 : Native.GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 0);
                Save(new { Kind = kind, Base = Address(address),
                    Path = length > 0 && length < path.Capacity ? path.ToString() : null });
            }
            finally { if (file != IntPtr.Zero && file != new IntPtr(-1)) Native.CloseHandle(file); }
        }
    }

    // Positive control: starts only the diagnostic's extracted trusted probe with
    // no arguments. Its entry immediately exits 2; no test files or profiles are used.
    internal static uint RunTrustedEntryControl(string executable, string evidenceFile)
    {
        executable = ScopedPaths.Ordinary(executable, true);
        var start = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>() };
        if (!Native.CreateProcessW(executable, new StringBuilder('"' + executable + '"'),
            IntPtr.Zero, IntPtr.Zero, false, DebugOnlyThisProcess | 4 | 0x08000000,
            IntPtr.Zero, Path.GetDirectoryName(executable)!, ref start, out var child))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW(trace control)");
        try
        {
            if (Native.ResumeThread(child.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread(trace control)");
            return Observe(child.Process, child.ProcessId, evidenceFile);
        }
        finally
        {
            Native.TerminateProcess(child.Process, 1);
            Native.WaitForSingleObject(child.Process, 5000);
            Native.CloseHandle(child.Thread); Native.CloseHandle(child.Process);
        }
    }

    private static string Address(IntPtr value) => "0x" + unchecked((ulong)value.ToInt64()).ToString("X16");

    private static string? DebugString(IntPtr process, IntPtr data)
    {
        var unicode = Marshal.ReadInt16(data, 8) != 0;
        var characters = unchecked((ushort)Marshal.ReadInt16(data, 10));
        var length = Math.Min(characters * (unicode ? 2 : 1), 16384);
        if (length == 0) return "";
        var bytes = new byte[length];
        if (!Native.ReadProcessMemory(process, Marshal.ReadIntPtr(data), bytes, (nuint)length, out var read)) return null;
        return (unicode ? Encoding.Unicode : Encoding.Latin1).GetString(bytes, 0, checked((int)read)).TrimEnd('\0');
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfo
        { internal int Size; internal IntPtr Reserved, Desktop, Title; internal uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;
          internal ushort Show, ReservedSize; internal IntPtr ReservedBytes,Input,Output,Error; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo
        { internal IntPtr Process,Thread; internal uint ProcessId,ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WaitForDebugEventEx(IntPtr value, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ContinueDebugEvent(uint process, uint thread, uint status);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, nuint size, out nuint read);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder path, uint size, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string working, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr process, uint milliseconds);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    }
}
