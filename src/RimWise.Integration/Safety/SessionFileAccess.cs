using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RimWise.Integration.Safety;

internal sealed record FileGrant(string Path, bool Directory, uint Volume, ulong Id, bool Writable);

/// <summary>Non-inheriting grants on original files, identified and changed through the same handle.
/// Recovery removes only this unique session SID, preserving unrelated current DACL entries.</summary>
[SupportedOSPlatform("windows")]
internal static class SessionFileAccess
{
    internal static FileGrant Inspect(string path, bool writable = false, PinnedDirectorySet? batch = null)
    {
        using var parent = PinParent(path, batch);
        using var handle = Open(path, false, parentPinned: batch is not null);
        var information = Information(handle);
        return new(path, (information.Attributes & FileAttributes.Directory) != 0,
            information.Volume, ((ulong)information.IndexHigh << 32) | information.IndexLow, writable);
    }

    internal static void Add(FileGrant grant, SecurityIdentifier sid, PinnedDirectorySet? batch = null)
        => Edit(grant, sid, remove: false, batch);

    internal static void Remove(FileGrant grant, SecurityIdentifier sid, PinnedDirectorySet? batch = null)
        => Edit(grant, sid, remove: true, batch);

    internal static void VerifyFixture(string root, SecurityIdentifier sid)
    {
        var directory = ScopedPaths.Child(root, "权限预检");
        Directory.CreateDirectory(directory);
        var child = ScopedPaths.Child(directory, "existing.txt"); File.WriteAllText(child, "synthetic");
        var grant = Inspect(directory);
        var before = new FileInfo(child).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        try
        {
            Add(grant, sid);
            if (new FileInfo(child).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) != before)
                throw new IOException("目录权限意外向子文件传播，停止原游戏授权。");
        }
        finally { Remove(grant, sid); }
        var writable = Inspect(directory, writable: true);
        try
        {
            Add(writable, sid);
            var rules = new FileInfo(child).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Where(r => r.IdentityReference.Equals(sid)).ToArray();
            if (rules.Length != 1 || !rules[0].IsInherited) throw new IOException("独立数据目录授权未正确传播。");
        }
        finally
        {
            try { Remove(writable, sid); }
            finally { Remove(Inspect(child, writable: true), sid); }
        }
        if (new FileInfo(child).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) != before)
            throw new IOException("样例清理未保留原始权限，停止原游戏授权。");
    }

    private static void Edit(FileGrant grant, SecurityIdentifier sid, bool remove, PinnedDirectorySet? batch)
    {
        if (grant.Directory) batch?.ReleaseDirectoryTree(grant.Path);
        using var parent = PinParent(grant.Path, batch);
        if (remove)
        {
            using var readHandle = Open(grant.Path, false, parentPinned: batch is not null);
            Match(grant, Information(readHandle));
            if (!Read(readHandle).GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(r => sid.Equals(r.IdentityReference))) return;
        }
        // SetSecurityInfo normally propagates EXISTING inheritable ACEs too. Microsoft documents
        // that a MAXIMUM_ALLOWED handle suppresses propagation; use it only for original directories.
        // Fresh writable data uses the ordinary handle so its session ACE reaches its own children.
        using var handle = Open(grant.Path, true, suppressPropagation: grant.Directory && !grant.Writable,
            propagate: grant.Writable, parentPinned: batch is not null);
        Match(grant, Information(handle));
        var security = Read(handle);
        if (!UpdateRules(security, grant, sid, remove)) return;
        // Original objects never receive inheritable ACEs. The writable tree is freshly created.
        var bytes = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!Native.GetSecurityDescriptorDacl(pinned.AddrOfPinnedObject(), out var present, out var dacl, out _)
                || !present) throw new IOException("无法获得明确 DACL。");
            var error = Native.SetSecurityInfo(handle, 1, 4, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            if (error != 0) throw new Win32Exception((int)error, "无法更新会话权限。");
        }
        finally { pinned.Free(); }
        var after = Read(handle).GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(r => sid.Equals(r.IdentityReference)).ToArray();
        if (remove ? after.Length != 0 : after.Length != 1)
            throw new IOException("会话权限更新未确认。");
    }

    internal static bool UpdateRules(FileSystemSecurity security, FileGrant grant, SecurityIdentifier sid, bool remove)
    {
        var existing = security.GetAccessRules(true, remove, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(r => sid.Equals(r.IdentityReference)).ToArray();
        if (remove)
        {
            if (existing.Length == 0) return false;
            // Fresh data may have inherited our session ACE. Strip only this SID from the DACL;
            // do not let removal re-inherit it from a parent that has not yet been cleaned.
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.DiscretionaryAcl is not { } acl) throw new IOException("缺少明确 DACL。");
            for (var i = acl.Count - 1; i >= 0; --i)
                if (acl[i] is KnownAce ace && ace.SecurityIdentifier.Equals(sid)) acl.RemoveAce(i);
            var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
        }
        else
        {
            if (existing.Length != 0) throw new IOException("会话 SID 已存在权限项，拒绝合并未知授权。");
            security.AddAccessRule(new FileSystemAccessRule(sid,
                grant.Writable ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute,
                grant.Writable && grant.Directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return true;
    }

    internal static void Verify(FileGrant grant, IntPtr token, bool expectRead, PinnedDirectorySet? batch = null)
    {
        using var parent = PinParent(grant.Path, batch);
        using var handle = Open(grant.Path, false, parentPinned: batch is not null);
        Match(grant, Information(handle));
        var security = Read(handle).GetSecurityDescriptorBinaryForm();
        if (!Native.DuplicateToken(token, 2, out var impersonation)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var privileges = Marshal.AllocHGlobal(4096);
        try
        {
            uint size = 4096;
            var mapping = new Mapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200a0, All = 0x1f01ff };
            if (!Native.AccessCheck(security, impersonation, 0x02000000, ref mapping, privileges, ref size,
                out var granted, out var allowed)) throw new Win32Exception(Marshal.GetLastWin32Error());
            const uint readExecute = 0x1200a9;
            const uint changes = 0x000d0156; // data/append/EA/attributes/delete-child/delete/WRITE_DAC/WRITE_OWNER
            if (expectRead && (!allowed || (granted & readExecute) != readExecute))
                throw new IOException("隔离进程不能读取必要输入：" + grant.Path);
            if (grant.Writable && (!allowed || (granted & 0x1301bf) != 0x1301bf))
                throw new IOException("隔离进程不能写入本轮独立数据目录。");
            if (!grant.Writable && (granted & changes) != 0)
                throw new IOException("隔离进程可能修改原始输入，拒绝恢复运行：" + grant.Path);
            if (!expectRead && allowed && (granted & 1) != 0)
                throw new IOException("隔离进程可读取排除的文件，拒绝恢复运行：" + grant.Path);
        }
        finally { Marshal.FreeHGlobal(privileges); Native.CloseHandle(impersonation); }
    }

    private static FileSystemSecurity Read(SafeFileHandle handle)
    {
        var error = Native.GetSecurityInfo(handle, 1, 1 | 2 | 4, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error, "无法读取对象权限。");
        try
        {
            var bytes = new byte[Native.GetSecurityDescriptorLength(descriptor)]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            FileSystemSecurity security = (Information(handle).Attributes & FileAttributes.Directory) != 0
                ? new DirectorySecurity() : new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(bytes); return security;
        }
        finally { Native.LocalFree(descriptor); }
    }

    private static IDisposable? PinParent(string path, PinnedDirectorySet? batch)
    {
        if (batch is null) return PinnedDirectory.Open(Path.GetDirectoryName(path)!);
        batch.PinParent(path);
        return null;
    }

    private static SafeFileHandle Open(string path, bool modify, bool suppressPropagation = false, bool propagate = false,
        bool parentPinned = false)
    {
        if (!parentPinned) ScopedPaths.Ordinary(path, true);
        var access = suppressPropagation ? 0x02000000u : 0x20000u | 0x80u | (modify ? 0x40000u : 0);
        var handle = Native.CreateFileW(path, access, propagate ? 7u : 1u,
            IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "无法锁定权限目标：" + path);
    }
    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!Native.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Links > 1)
            throw new IOException("拒绝链接、重解析点或多硬链接权限目标。");
        return info;
    }
    private static void Match(FileGrant grant, FileInformation info)
    {
        if (info.Volume != grant.Volume || (((ulong)info.IndexHigh << 32) | info.IndexLow) != grant.Id
            || ((info.Attributes & FileAttributes.Directory) != 0) != grant.Directory)
            throw new IOException("权限目标已被替换，拒绝继续：" + grant.Path);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Mapping { internal uint Read, Write, Execute, All; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] internal static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool DuplicateToken(IntPtr token, int level, out IntPtr duplicate);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool AccessCheck(byte[] descriptor, IntPtr token, uint access, ref Mapping mapping, IntPtr privileges, ref uint size, out uint granted, out bool allowed);
    }
}
