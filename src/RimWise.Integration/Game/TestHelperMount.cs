using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>The sole reparse-point exception: one owned link to the embedded fixed helper.
/// General path validation remains unchanged. Removal never follows the link or deletes its target.</summary>
internal sealed partial class TestHelperMount : IDisposable
{
    private readonly string link, target;
    private readonly OrdinaryGameTestOptions options;
    private readonly string session;
    internal TestHelperMount(OrdinaryGameTestOptions options, string session)
    {
        this.options = options;
        this.session = session;
        target = ScopedPaths.Child(session, "fixed-helper");
        link = LinkPath(options, session);
        try
        {
            // A transient lock may have cleared since the previous round's cleanup.
            if (Directory.Exists(target)) CleanAfterUse(options, session);
            CreateOwnedPayload(options, session);
            using var pins = new PinnedDirectorySet();
            pins.PinParent(link);
            pins.PinDirectory(target);
            if (!CreateDirectory(link,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(),"辅助 Mod 目录不能创建，未覆盖现有内容。");
            CreateJunction(link,target);
        }
        catch { CleanAfterUse(options, session); throw; }
    }
    private static string LinkPath(OrdinaryGameTestOptions options, string session)
    {
        var mods = ScopedPaths.Child(options.GameDirectory, "Mods");
        if (!Directory.Exists(mods)) throw new DirectoryNotFoundException("游戏的 Mods 目录不存在。");
        // Deliberately do not pass the final owned link to ScopedPaths.Ordinary.
        return Path.Combine(mods,"RimWise-Test-"+Path.GetFileName(session)[5..]);
    }
    private static void Remove(string link,string target)
    {
        using var pins = new PinnedDirectorySet();
        pins.PinParent(link);
        var info = new DirectoryInfo(link);
        if (!info.Exists && info.LinkTarget is null) return;
        using var handle = CreateFile(link,0x10080,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0 || info.LinkTarget is null
            || !string.Equals(NormalizeTarget(info.LinkTarget),Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))
            throw new IOException("测试辅助目录已被替换，拒绝清理其他内容。");
        DeleteByHandle(handle);
    }
    public void Dispose() => CleanAfterUse(options, session);

    private static string NormalizeTarget(string path) => Path.GetFullPath(
        path.StartsWith(@"\??\",StringComparison.Ordinal) || path.StartsWith(@"\\?\",StringComparison.Ordinal) ? path[4..] : path);

    private static void CreateJunction(string path,string destination)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\"+destination);
        var print = Encoding.Unicode.GetBytes(destination);
        var bytes = new byte[16+substitute.Length+2+print.Length+2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(bytes,0);
        BitConverter.GetBytes((ushort)(bytes.Length-8)).CopyTo(bytes,4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(bytes,10);
        BitConverter.GetBytes((ushort)(substitute.Length+2)).CopyTo(bytes,12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(bytes,14);
        substitute.CopyTo(bytes,16); print.CopyTo(bytes,18+substitute.Length);
        using var handle = CreateFile(path,0x40010000,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if(!DeviceIoControl(handle,0x900A4,bytes,(uint)bytes.Length,IntPtr.Zero,0,out _,IntPtr.Zero))
        {
            var error=Marshal.GetLastWin32Error();
            DeleteByHandle(handle);
            throw new Win32Exception(error);
        }
    }
    private static void DeleteByHandle(SafeFileHandle handle)
    {
        var delete=1;
        if (!SetFileInformationByHandle(handle,4,ref delete,4)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateDirectoryW",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path,IntPtr security);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file,int informationClass,ref int information,int size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file,uint code,byte[] input,uint length,IntPtr output,uint outputLength,out uint returned,IntPtr overlapped);
}
