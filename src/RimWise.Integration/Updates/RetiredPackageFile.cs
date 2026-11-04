using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Updates;

/// <summary>Hashes, backs up and removes the same exclusively opened file. Never recursively deletes.</summary>
internal static class RetiredPackageFile
{
    internal static bool Remove(string installation, PackageFile entry, string backupRoot, string version)
    {
        var path = ScopedPaths.Child(installation, entry.Path);
        using var parent = PinnedDirectory.Open(Path.GetDirectoryName(path)!);
        using var handle = CreateFileW(path, 0x80000000 | 0x10000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw Failure();
        if (!GetFileInformationByHandle(handle, out var info)) throw Failure();
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1)
            throw new IOException("保留链接文件或非普通文件。");
        using var source = new FileStream(handle, FileAccess.Read);
        var hash = Convert.ToHexString(SHA256.HashData(source));
        if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        source.Position = 0;
        LatestCleanupBackup.Append(source, entry, backupRoot, version);
        byte delete = 1; // FILE_DISPOSITION_INFO.DeleteFile is BOOLEAN, not Win32 BOOL.
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw Failure();
        return true;
    }

    private static IOException Failure() => new("旧程序文件暂时无法清理，将在下次启动重试。", new Win32Exception(Marshal.GetLastWin32Error()));
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, uint length);
}
