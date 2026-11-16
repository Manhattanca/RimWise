using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Safety;

namespace RimWise.Desktop.Services;

/// <summary>One current-user DPAPI credential, separate from AI credentials and drafts.</summary>
public sealed class MarketplaceCredentialStore
{
    private readonly string directory;
    public string CredentialPath => ScopedPaths.Child(directory, "marketplace-auth.bin");
    public string DraftPath => ScopedPaths.Child(directory, "marketplace-draft.json");

    public MarketplaceCredentialStore(string profileDirectory)
        => directory = ScopedPaths.Child(ScopedPaths.Ordinary(profileDirectory), "Data");

    public string ReadClientId()
    {
        var value = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "GitHubOAuthClientId")?.Value?.Trim() ?? "";
        if (value.Length > 200 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new InvalidDataException("内置 GitHub 登录配置无效。");
        return value;
    }

    public string? ReadClientSecret()
    {
        var value = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "GitHubOAuthClientSecret")?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 512 || value.Any(c => c is < '!' or > '~'))
            throw new InvalidDataException("内置 GitHub 登录配置无效。");
        DiagnosticRedactor.RegisterSecret(value);
        return value;
    }

    public string? ReadToken()
    {
        if (!File.Exists(CredentialPath)) return null;
        if (new FileInfo(CredentialPath).Length is 0 or > 64 * 1024) throw new InvalidDataException("登录凭据文件无效，请退出登录后重试。");
        var bytes = Crypt(File.ReadAllBytes(CredentialPath), protect: false, "1");
        try
        {
            var token = Encoding.UTF8.GetString(bytes);
            DiagnosticRedactor.RegisterSecret(token);
            return token;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void SaveToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16000) throw new ArgumentException("登录凭据无效。", nameof(token));
        DiagnosticRedactor.RegisterSecret(token);
        var bytes = Encoding.UTF8.GetBytes(token);
        try { WriteAtomic(CredentialPath, Crypt(bytes, protect: true, "1")); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void DeleteToken()
    {
        var path = CredentialPath;
        if (File.Exists(path)) File.Delete(path);
    }

    public void SaveDraft(byte[] bytes) => WriteAtomic(DraftPath, bytes);

    public byte[]? ReadDraft()
    {
        var path = DraftPath;
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is 0 or > 2 * 1024 * 1024)
            throw new InvalidDataException("本地发布草稿大小无效。");
        return File.ReadAllBytes(path);
    }

    private void WriteAtomic(string path, byte[] bytes)
    {
        ScopedPaths.Ordinary(directory);
        Directory.CreateDirectory(directory);
        ScopedPaths.Ordinary(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            ScopedPaths.Ordinary(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] Crypt(byte[] data, bool protect, string purpose)
    {
        var entropyBytes = Encoding.UTF8.GetBytes("RimWise/Marketplace/GitHub/" + purpose);
        var input = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var entropy = new DataBlob { Length = entropyBytes.Length, Data = Marshal.AllocHGlobal(entropyBytes.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Marshal.Copy(entropyBytes, 0, entropy.Data, entropyBytes.Length);
            var output = new DataBlob();
            var success = protect
                ? CryptProtectData(ref input, "RimWise marketplace", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, ref output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取或保护发布登录凭据。");
            try
            {
                var bytes = new byte[output.Length];
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob output);
    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob output);
    [DllImport("Kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
