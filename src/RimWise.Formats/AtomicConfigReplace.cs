using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace RimWise.Formats;

/// <summary>Atomically replaces a configuration without requiring changes to its permissions.</summary>
internal static class AtomicConfigReplace
{
    internal static void Replace(string source, string target, string? displaced)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Replace(source, target, displaced, ignoreMetadataErrors: false);
            return;
        }

        // ReplaceFile normally requests WRITE_DAC even when both files already have the
        // same security descriptor. Modify-only accounts need not have that permission.
        // Ignore only ACL-merge failures, and only when there is nothing to merge.
        // Never use IGNORE_MERGE_ERRORS, change ACLs, or replace by delete-and-move.
        const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        var targetSecurity = new FileInfo(target).GetAccessControl(sections).GetSecurityDescriptorBinaryForm();
        var sourceSecurity = new FileInfo(source).GetAccessControl(sections).GetSecurityDescriptorBinaryForm();
        if (!sourceSecurity.AsSpan().SequenceEqual(targetSecurity))
        {
            // Custom target permissions must be preserved by Windows itself. If this
            // requires rights we do not have, fail without weakening those permissions.
            File.Replace(source, target, displaced, ignoreMetadataErrors: false);
            return;
        }
        if (!ReplaceFileW(target, source, displaced, 0x4, IntPtr.Zero, IntPtr.Zero))
            throw new IOException("配置原子替换未完成，文件及备份已保留。", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFileW(string target, string source, string? backup, uint flags, IntPtr excluded, IntPtr reserved);
}
