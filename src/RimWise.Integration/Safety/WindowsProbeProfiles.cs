using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace RimWise.Integration.Safety;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProbeProfiles : IProbeProfileStore
{
    public string Owner
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new IOException("无法获取当前用户 SID。"); }
    }
    public string Sid(string profile)
    {
        Marshal.ThrowExceptionForHR(DeriveAppContainerSidFromAppContainerName(profile, out var sid));
        try { return new SecurityIdentifier(sid).Value; }
        finally { FreeSid(sid); }
    }
    public void Create(string profile)
    {
        var hr = CreateAppContainerProfile(profile, profile, "RimWise temporary restricted test", IntPtr.Zero, 0, out var sid);
        if (sid != IntPtr.Zero) FreeSid(sid);
        Marshal.ThrowExceptionForHR(hr);
    }
    public bool Remove(string profile)
    {
        // Only deterministic, nonce-qualified development profiles may reach the Windows API.
        var prefix = new[] { "RimWise.GameTest.", "RimWise.Probe.", "RimSortWorkbench.GameTest.", "RimSortWorkbench.Probe." }
            .FirstOrDefault(p => profile.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null || !Guid.TryParseExact(profile[prefix.Length..], "N", out _))
            throw new InvalidDataException("拒绝清理非探针配置。");
        var sid = Sid(profile);
        // Get the real known folder, not the development process's redirected LOCALAPPDATA.
        var localAppData = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref localAppData, 0, IntPtr.Zero, out var pointer));
        string package;
        try { package = Path.Combine(Marshal.PtrToStringUni(pointer)!, "Packages", profile.ToLowerInvariant()); }
        finally { Marshal.FreeCoTaskMem(pointer); }
        ScopedPaths.Ordinary(package);
        if (Absent(sid, package)) return true;
        Marshal.ThrowExceptionForHR(DeleteAppContainerProfile(profile));
        return Absent(sid, package);
    }
    private static bool Absent(string sid, string package)
    {
        using var mapping = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings\" + sid);
        // Directory.Exists can return false on access denial. Inspect the parent to distinguish
        // absence from an inaccessible child, and propagate denied enumeration to the caller.
        var parent = Path.GetDirectoryName(package)!;
        return mapping is null && !Directory.EnumerateFileSystemEntries(parent).Any(path =>
            Path.GetFileName(path).Equals(Path.GetFileName(package), StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint capabilityCount, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
}
