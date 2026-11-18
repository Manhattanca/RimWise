using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Deploys the fixed helper as ordinary files. Only legacy recovery may detach
/// a verified owned junction; new game launches never depend on reparse points.</summary>
internal sealed partial class TestHelperMount : IDisposable
{
    private readonly OrdinaryGameTestOptions options;
    private readonly string session;
    internal TestHelperMount(OrdinaryGameTestOptions options, string session)
    {
        this.options = options;
        this.session = session;
        ValidateSession(session);
        try
        {
            // Retry our previous round, but never hide a failed cleanup by overwriting it.
            CleanAfterUse(options, session);
            if (GameProcessService.IsAnyRunning()) throw new IOException("游戏仍在运行，未部署测试辅助组件。");
            EnsureNoHelperRemnants(options.GameDirectory);
            DeployDirectory(options, session);
        }
        catch { CleanAfterUse(options, session); throw; }
    }
    private static string LinkPath(OrdinaryGameTestOptions options, string session)
    {
        var mods = ScopedPaths.Child(options.GameDirectory, "Mods");
        if (!Directory.Exists(mods)) throw new DirectoryNotFoundException("游戏的 Mods 目录不存在。");
        // Legacy recovery must be able to inspect the final link without following it.
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
}
