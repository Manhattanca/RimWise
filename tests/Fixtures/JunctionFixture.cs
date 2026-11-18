using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Synthetic regression inputs only. The production deployer never creates links.
internal static class JunctionFixture
{
    internal static void HardLink(string path, string existing)
    {
        if (!CreateHardLinkW(path, existing, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void Create(string path, string destination)
    {
        Directory.CreateDirectory(path);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + destination);
        var print = Encoding.Unicode.GetBytes(destination);
        var bytes = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)(bytes.Length - 8)).CopyTo(bytes, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(bytes, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(bytes, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(bytes, 14);
        substitute.CopyTo(bytes, 16); print.CopyTo(bytes, 18 + substitute.Length);
        using var handle = CreateFile(path, 0x40010000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x900A4, bytes, (uint)bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, uint length,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
}
