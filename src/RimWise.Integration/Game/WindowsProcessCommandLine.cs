using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RimWise.Integration.Game;

/// <summary>Reads only the command-line field of a candidate process held by an existing handle.</summary>
internal static class WindowsProcessCommandLine
{
    internal static string Read(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!IsWow64Process(process.SafeHandle, out var wow64)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (Environment.Is64BitProcess && wow64)
            throw new NotSupportedException("跨位数命令行读取使用 WMI 验证。");
        var basic = new BasicInformation();
        var status = NtQueryInformationProcess(process.SafeHandle, 0, ref basic, Marshal.SizeOf<BasicInformation>(), out _);
        if (status < 0 || basic.Peb == IntPtr.Zero) throw new InvalidOperationException($"无法读取进程参数 (NTSTATUS 0x{status:X8})。");
        // These are the layouts documented for PEB and RTL_USER_PROCESS_PARAMETERS in winternl.h.
        // They may change on future Windows versions: all reads are bounded and failures reject ownership.
        var size = IntPtr.Size;
        var pebOffset = size == 8 ? 32 : 16;
        var parameters = Pointer(ReadMemory(process.SafeHandle, IntPtr.Add(basic.Peb, pebOffset), size), 0);
        if (parameters == IntPtr.Zero) throw new InvalidOperationException("进程参数尚未初始化。");
        var unicodeSize = size == 8 ? 16 : 8;
        var offset = 16 + 10 * size + unicodeSize;
        var unicode = ReadMemory(process.SafeHandle, IntPtr.Add(parameters, offset), unicodeSize);
        var length = BitConverter.ToUInt16(unicode, 0);
        var maximum = BitConverter.ToUInt16(unicode, 2);
        var buffer = Pointer(unicode, size == 8 ? 8 : 4);
        if (length == 0) return "";
        if (length % 2 != 0 || maximum < length || buffer == IntPtr.Zero)
            throw new InvalidOperationException("进程命令行结构无效，拒绝接管。");
        return System.Text.Encoding.Unicode.GetString(ReadMemory(process.SafeHandle, buffer, length));
    }

    private static IntPtr Pointer(byte[] bytes, int offset) => IntPtr.Size == 8
        ? new(BitConverter.ToInt64(bytes, offset)) : new(BitConverter.ToInt32(bytes, offset));

    private static byte[] ReadMemory(SafeProcessHandle process, IntPtr address, int size)
    {
        if (size is < 1 or > 65534) throw new InvalidOperationException("进程参数长度超出范围。");
        var buffer = new byte[size];
        if (!ReadProcessMemory(process, address, buffer, size, out var read) || read != size)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    { internal IntPtr Reserved1, Peb, Reserved2, Reserved3, UniqueProcessId, Reserved4; }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        ref BasicInformation information, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, [Out] byte[] buffer,
        nint size, out nint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
}
