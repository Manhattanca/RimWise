using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RimWise.Integration.Safety;

/// <summary>
/// Holds each existing directory from the volume root without write/delete sharing.
/// This narrows directory replacement races during our own archive operations; it is
/// not an OS sandbox or a snapshot of mutable files within a mod.
/// </summary>
internal sealed class PinnedDirectory : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal string DirectoryPath { get; }

    private PinnedDirectory(string path) => DirectoryPath = path;

    internal static PinnedDirectory Open(string path, bool create = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("安全目录操作目前仅支持 Windows。");
        path = ScopedPaths.Ordinary(path);
        var root = Path.GetPathRoot(path) ?? throw new IOException("目录缺少磁盘根路径。");
        if (Native.GetDriveTypeW(root) is not (2 or 3 or 6))
            throw new IOException("安全目录移动只支持本地磁盘。");
        // Preserve the separator for a volume root such as D:\.
        path = path.Length <= root.Length ? root : path.TrimEnd('\\', '/');
        var lease = new PinnedDirectory(path);
        try
        {
            var current = root;
            lease.handles.Add(OpenDirectory(current, 0x80)); // FILE_READ_ATTRIBUTES
            foreach (var part in path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                // Its parent is already pinned, and the newly opened handle is checked
                // without following a reparse point. Failure never falls back to Move.
                if (create && !Native.CreateDirectoryW(current, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 183) throw Failure("无法创建安全目录", error);
                }
                lease.handles.Add(OpenDirectory(current, 0x80));
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal void MoveChildTo(string sourceName, PinnedDirectory destination, string destinationName, Action<string> validate)
    {
        ObjectDisposedException.ThrowIf(handles.Count == 0, this);
        ObjectDisposedException.ThrowIf(destination.handles.Count == 0, destination);
        var source = ChildName(DirectoryPath, sourceName);
        var target = ChildName(destination.DirectoryPath, destinationName);
        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("只允许同盘且互不嵌套的目录移动。");
        // DELETE on this exact object permits its own rename while our handle prevents
        // other callers replacing it. The destination's ancestors stay pinned as well.
        using var sourceHandle = OpenDirectory(source, 0x10000 | 0x80);
        validate(source);
        var bytes = System.Text.Encoding.Unicode.GetBytes(target);
        var offset = checked((int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FirstCharacter)));
        // Include a zero WCHAR as well as the counted bytes. The Win32 conversion
        // layer can inspect a terminated path; do not expose uninitialized tail data.
        var size = checked(Math.Max(Marshal.SizeOf<RenameInfo>(), offset + bytes.Length + 2));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // FileRenameInfo: ReplaceIfExists = FALSE, RootDirectory = NULL, full
            // Unicode destination. No occupied file or directory is overwritten.
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, checked((int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.NameLength))), bytes.Length);
            Marshal.Copy(bytes, 0, buffer + offset, bytes.Length);
            if (!Native.SetFileInformationByHandle(sourceHandle, 3, buffer, (uint)size))
                throw Failure("安全移动未完成；不会改用普通路径移动", Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ChildName(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) throw new IOException("只允许直属子目录。");
        return ScopedPaths.Child(root, name);
    }

    internal static SafeFileHandle OpenDirectory(string path, uint access, uint share = 1)
    {
        var handle = Native.CreateFileW(path, access, share, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw Failure("无法锁定目录", Marshal.GetLastWin32Error());
            if (!Native.GetFileInformationByHandle(handle, out var information))
                throw Failure("无法验证目录句柄", Marshal.GetLastWin32Error());
            if ((information.Attributes & FileAttributes.ReparsePoint) != 0
                || (information.Attributes & FileAttributes.Directory) == 0)
                throw new IOException("目标不是普通目录，已停止操作。");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static IOException Failure(string message, int error) => new(message, new Win32Exception(error));

    public void Dispose()
    {
        for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        handles.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInfo
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint NameLength;
        public char FirstCharacter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint GetDriveTypeW(string root);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDirectoryW(string path, IntPtr security);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint length);
    }
}
