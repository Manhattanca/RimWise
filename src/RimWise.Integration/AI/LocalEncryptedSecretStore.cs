using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RimWise.Integration.Configuration;

namespace RimWise.Integration.AI;

/// <summary>Stores one DPAPI-protected secret in the configured user data directory.</summary>
public sealed class LocalEncryptedSecretStore(string applicationDirectory) : ISecretStore
{
    private readonly string path = Path.Combine(Path.GetFullPath(applicationDirectory), "Data", "ai-secret.bin");

    public string? Read(string name)
    {
        if (!File.Exists(path)) return null;
        RejectLinks(path);
        var encrypted = File.ReadAllBytes(path);
        if (encrypted.Length is 0 or > 1024 * 1024) throw new InvalidDataException("AI 密钥文件大小无效。");
        var value = Encoding.UTF8.GetString(Unprotect(encrypted, name));
        Diagnostics.DiagnosticRedactor.RegisterSecret(value);
        return value;
    }

    // The exporter uses these values only for redaction, never as output fields.
    internal IReadOnlyList<string> ReadDiagnosticSecrets(string name)
    {
        var values = new List<string>();
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            Safety.ScopedPaths.Ordinary(candidate);
            if (!File.Exists(candidate)) continue;
            var length = new FileInfo(candidate).Length;
            if (length is 0 or > 1024 * 1024) throw new InvalidDataException("无法安全读取密钥以完成诊断脱敏。");
            values.Add(Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(candidate), name)));
        }
        return values;
    }

    public void Write(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("AI 密钥不能为空。", nameof(value));
        Diagnostics.DiagnosticRedactor.RegisterSecret(value);
        RejectLinks(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fileLock = AppDataFileLock.Acquire(path + ".lock");
        RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var encrypted = Protect(Encoding.UTF8.GetBytes(value), name);
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
                File.Move(temporary, path, overwrite: true);
            }
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete(string name)
    {
        RejectLinks(path);
        using var fileLock = AppDataFileLock.Acquire(path + ".lock");
        if (File.Exists(path)) File.Delete(path);
    }

    private static byte[] Protect(byte[] data, string name)
    {
        EnsureWindows();
        return Crypt(data, name, protect: true);
    }

    private static byte[] Unprotect(byte[] data, string name)
    {
        EnsureWindows();
        try { return Crypt(data, name, protect: false); }
        catch (Win32Exception)
        {
            // Pre-RimWise files used this entropy prefix. Existing settings retain their
            // SecretName; only new writes use the new prefix, without discarding old keys.
            return Crypt(data, name, protect: false, prefix: "RimSortWorkbench/");
        }
    }

    private static byte[] Crypt(byte[] data, string name, bool protect, string prefix = "RimWise/")
    {
        var input = new DATA_BLOB { cbData = (uint)data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        var entropyBytes = Encoding.UTF8.GetBytes(prefix + name);
        var entropy = new DATA_BLOB { cbData = (uint)entropyBytes.Length, pbData = Marshal.AllocHGlobal(entropyBytes.Length) };
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            Marshal.Copy(entropyBytes, 0, entropy.pbData, entropyBytes.Length);
            var output = new DATA_BLOB();
            var success = protect
                ? CryptProtectData(ref input, "RimWise AI secret", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, ref output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保护 AI 密钥。");
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            Marshal.FreeHGlobal(entropy.pbData);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本地加密密钥需要 Windows DPAPI。");
    }

    private static void RejectLinks(string target)
    {
        for (var current = Path.GetFullPath(target); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("AI 密钥目录不支持符号链接或目录联接。");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DATA_BLOB { public uint cbData; public IntPtr pbData; }
    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string description, ref DATA_BLOB optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, ref DATA_BLOB dataOut);
    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, ref DATA_BLOB dataOut);
    [DllImport("Kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
