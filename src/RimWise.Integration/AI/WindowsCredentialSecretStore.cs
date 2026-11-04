using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RimWise.Integration.AI;

/// <summary>Stores API keys in Windows Credential Manager instead of the app settings JSON.</summary>
public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;

    public string? Read(string name)
    {
        EnsureWindows();
        if (!CredRead(name, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "无法读取 Windows 凭据。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }

    public void Write(string name, string value)
    {
        EnsureWindows();
        var target = Marshal.StringToCoTaskMemUni(name);
        var blob = System.Text.Encoding.UTF8.GetBytes(value);
        var blobPointer = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new CREDENTIAL
            {
                Type = Generic,
                TargetName = target,
                CredentialBlob = blobPointer,
                CredentialBlobSize = (uint)blob.Length,
                Persist = PersistLocalMachine,
                UserName = Marshal.StringToCoTaskMemUni(Environment.UserName)
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Windows 凭据。");
            Marshal.FreeCoTaskMem(credential.UserName);
        }
        finally
        {
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(blobPointer);
        }
    }

    public void Delete(string name)
    {
        EnsureWindows();
        if (!CredDelete(name, Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error, "无法删除 Windows 凭据。");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AI 密钥存储需要 Windows Credential Manager。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);
}
