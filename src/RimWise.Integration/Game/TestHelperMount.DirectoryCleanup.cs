using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

internal sealed partial class TestHelperMount
{
    private static void CleanDirectory(string gameDirectory, string session)
    {
        var record = ReadDeployment(gameDirectory, session);
        var path = Path.Combine(ScopedPaths.Child(gameDirectory, "Mods"), "RimWise-Test-" + record.SessionId);
        using var pins = new PinnedDirectorySet();
        pins.PinParent(path);
        // Missing is idempotent. Access errors and files/links must not masquerade as missing.
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        using var root = PinnedDirectory.OpenDirectory(path, 0x10081, share: 3);
        if (Identity(root) != record.DirectoryIdentity)
            throw new IOException("测试辅助目录身份已变化，已保留替换后的目录及其内容。");

        var directories = new List<SafeFileHandle>();
        var files = new List<FileStream>();
        try
        {
            // Preflight the complete small tree, keeping handles until deletion. No
            // recursive delete, link traversal, unknown-file removal or mutable hash check.
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                if (Path.GetFileName(entry) is not ("About" or "Assemblies"))
                    throw new IOException("辅助组件目录含未登记内容，已完整保留供检查。");
            foreach (var name in new[] { "About", "Assemblies" })
            {
                var child = Path.Combine(path, name);
                try { _ = File.GetAttributes(child); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                directories.Add(PinnedDirectory.OpenDirectory(child, 0x10081, share: 3));
                var expected = name == "About" ? "About.xml" : "RimWise.TestHelper.dll";
                foreach (var entry in Directory.EnumerateFileSystemEntries(child))
                    if (Path.GetFileName(entry) != expected)
                        throw new IOException("辅助组件子目录含未登记内容，已完整保留供检查。");
            }
            foreach (var file in record.Files)
            {
                var full = Path.Combine(path, file.Path);
                var handle = CreateFile(full, 0x80010080, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error is 2 or 3) continue;
                    throw new Win32Exception(error);
                }
                FileStream stream;
                try
                {
                    var info = Information(handle);
                    if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.Links != 1)
                        throw new IOException("辅助组件文件不是独立普通文件，已保留，未跟随链接。");
                    stream = new FileStream(handle, FileAccess.Read);
                }
                catch { handle.Dispose(); throw; }
                files.Add(stream);
                if (Convert.ToHexString(SHA256.HashData(stream)) != file.Sha256)
                    throw new IOException("测试辅助组件内容已变化，已完整保留供检查。");
            }
            foreach (var file in files) DeleteByHandle(file.SafeFileHandle);
            foreach (var file in files) file.Dispose();
            foreach (var directory in directories) DeleteByHandle(directory);
            foreach (var directory in directories) directory.Dispose();
            DeleteByHandle(root);
        }
        finally
        {
            foreach (var file in files) file.Dispose();
            foreach (var directory in directories) directory.Dispose();
        }
    }

    private static string Identity(SafeFileHandle handle)
    {
        var info = Information(handle);
        return $"{info.Volume:X8}{info.IndexHigh:X8}{info.IndexLow:X8}{info.Creation.dwHighDateTime:X8}{info.Creation.dwLowDateTime:X8}";
    }

    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
