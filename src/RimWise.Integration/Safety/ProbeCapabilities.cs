using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace RimWise.Integration.Safety;

/// <summary>Development experiments only. Neither preset grants network capabilities.</summary>
public enum IsolationProbePolicy { Strict, RegistryRead }

[SupportedOSPlatform("windows")]
internal sealed class ProbeCapabilities : IDisposable
{
    private IntPtr groups, sids, entries;
    private uint groupCount, sidCount;
    internal IntPtr Entries => entries;
    internal uint Count => sidCount;
    internal string[] ExpectedSids { get; private set; } = [];

    internal ProbeCapabilities(IsolationProbePolicy policy)
    {
        if (policy == IsolationProbePolicy.Strict) return;
        if (policy != IsolationProbePolicy.RegistryRead) throw new ArgumentOutOfRangeException(nameof(policy));
        try
        {
            // Test the specific Winsock catalog-read hypothesis without adding internetClient,
            // privateNetworkClientServer, or changing any host registry permissions.
            if (!DeriveCapabilitySidsFromName("registryRead", out groups, out groupCount, out sids, out sidCount))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DeriveCapabilitySidsFromName(registryRead)");
            if (sidCount != 1 || groupCount > 16 || sids == IntPtr.Zero)
                throw new IOException("Windows 返回了意外的探针能力集合。");
            var sid = Marshal.ReadIntPtr(sids);
            ExpectedSids = [new SecurityIdentifier(sid).Value];
            var size = Marshal.SizeOf<SidAndAttributes>();
            entries = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new SidAndAttributes { Sid = sid, Attributes = 4 }, entries, false); // SE_GROUP_ENABLED
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (entries != IntPtr.Zero) { Marshal.FreeHGlobal(entries); entries = IntPtr.Zero; }
        FreeArray(ref groups, ref groupCount);
        FreeArray(ref sids, ref sidCount);
    }

    private static void FreeArray(ref IntPtr array, ref uint count)
    {
        if (array != IntPtr.Zero)
        {
            for (var i = 0; i < count; ++i) LocalFree(Marshal.ReadIntPtr(array, checked(i * IntPtr.Size)));
            LocalFree(array); array = IntPtr.Zero;
        }
        count = 0;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeriveCapabilitySidsFromName(string name, out IntPtr groupSids, out uint groupSidCount,
        out IntPtr capabilitySids, out uint capabilitySidCount);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
