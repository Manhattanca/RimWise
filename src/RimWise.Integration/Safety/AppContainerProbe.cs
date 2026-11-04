using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace RimWise.Integration.Safety;

public sealed record IsolationProbeResult(bool Passed, string Message, uint? ExitCode = null,
    string? EvidenceDirectory = null, bool? TemporaryProfileRemoved = null)
{
    public string Policy { get; init; } = nameof(IsolationProbePolicy.Strict);
    public string? FailureStage { get; init; }
    public bool StartupTraced { get; init; }
    public bool? RegistryWriteHandlesDenied { get; init; }
    public int? FailureHResult { get; init; }
    public int? NativeErrorCode { get; init; }
}

/// <summary>
/// Development-only LPAC experiment. It grants access only to newly created probe fixtures.
/// A passed probe is NOT permission to run arbitrary mods, and is not a game compatibility certificate.
/// Default runs do not register a profile. An explicit development-only opt-in creates and removes
/// a temporary Windows-managed profile; no original game ACL is changed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppContainerProbe
{
    public static IsolationProbeResult Run(string probeExecutable, string scratchRoot, bool useTemporaryProfile = false)
        => RunWithCheckpoint(probeExecutable, scratchRoot, useTemporaryProfile, null);

    // Explicit separate experiment, never an automatic fallback for a failed strict run.
    public static IsolationProbeResult RunPolicy(string probeExecutable, string scratchRoot, IsolationProbePolicy policy)
        => RunWithCheckpoint(probeExecutable, scratchRoot, true, null, policy);

    internal static IsolationProbeResult RunStartupDiagnostic(string probeExecutable, string scratchRoot)
        => RunWithCheckpoint(probeExecutable, scratchRoot, true, null, IsolationProbePolicy.Strict, traceStartup: true);

    public static ProbeProfileRecovery RecoverProfile(string sessionDirectory)
        => ProbeProfileLease.Recover(sessionDirectory, new WindowsProbeProfiles());

    internal static IsolationProbeResult RunWithCheckpoint(string probeExecutable, string scratchRoot,
        bool useTemporaryProfile, Action<string>? checkpoint, IsolationProbePolicy policy = IsolationProbePolicy.Strict,
        bool traceStartup = false, bool holdForLifecycle = false)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        var root = ScopedPaths.Ordinary(scratchRoot, true);
        probeExecutable = ScopedPaths.Ordinary(probeExecutable, true);
        // An exclusive fresh fixture prevents applying access grants to existing personal directories.
        var session = ScopedPaths.Child(root, "isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var allowed = ScopedPaths.Child(session, "allowed"); Directory.CreateDirectory(allowed);
        var readOnly = ScopedPaths.Child(session, "read-only"); Directory.CreateDirectory(readOnly);
        var outside = ScopedPaths.Child(session, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(readOnly, "input.txt"), "read-fixture");
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "unchanged");
        var executable = Path.Combine(readOnly, "IsolationProbe.exe"); File.Copy(probeExecutable, executable);
        IntPtr sid = IntPtr.Zero;
        ProbeProfileLease? profile = null;
        var result = new IsolationProbeResult(false, "探针尚未完成。", EvidenceDirectory: session);
        var stage = "创建受限身份";
        try
        {
            if (useTemporaryProfile)
            {
                // Development-only explicit opt-in: Windows stores profile state outside the project.
                // The normal sorter and AI requests never register or run a profile.
                profile = ProbeProfileLease.Prepare(session, new WindowsProbeProfiles());
                checkpoint?.Invoke("intent-flushed");
                profile.Create();
                checkpoint?.Invoke("profile-created");
            }
            Marshal.ThrowExceptionForHR(Native.DeriveAppContainerSidFromAppContainerName(
                profile?.Profile ?? "RimWise.Probe." + Guid.NewGuid().ToString("N"), out sid));
            var identity = new SecurityIdentifier(sid);
            stage = "设置探针读取权限";
            Grant(readOnly, identity, FileSystemRights.ReadAndExecute);
            stage = "设置探针写入权限";
            Grant(allowed, identity, FileSystemRights.Modify);
            // An explicit package-SID DACL permits AppContainer access to a medium-IL
            // directory (Microsoft: Launch an AppContainer). Do not lower the host
            // directory's integrity label or require a privileged SACL change.
            stage = "启动并验证 LPAC 子进程";
            var exit = StartLpac(executable, [allowed, readOnly, outside], readOnly, sid, checkpoint, policy, traceStartup, holdForLifecycle);
            var marker = Path.Combine(allowed, "result.txt");
            var registryMarker = Path.Combine(allowed, "registry-write.txt");
            var registryWritesDenied = File.Exists(registryMarker) && File.ReadAllText(registryMarker)
                == "winsock-parameters=0000000005\nprotocol-catalog=0000000005\nnamespace-catalog=0000000005\n";
            var good = exit == 0 && File.Exists(marker) && File.ReadAllText(marker) == "read=1;write=1;outsideDenied=1;outsideReadDenied=1;readonlyDenied=1;networkDenied=1;childDenied=1;environmentClean=1"
                && registryWritesDenied
                && File.ReadAllText(Path.Combine(outside, "sentinel.txt")) == "unchanged"
                && File.ReadAllText(Path.Combine(readOnly, "input.txt")) == "read-fixture";
            result = new(good, good ? "受控 LPAC 探针通过；真实游戏兼容与完整权限审计仍未验证。" : "隔离探针未通过，自动游戏／构建执行保持禁用。", exit, session)
            { RegistryWriteHandlesDenied = registryWritesDenied };
            if (!good)
                result = result with { FailureStage = !File.Exists(Path.Combine(allowed, "entry.txt"))
                    ? "进程初始化（探针入口未确认）" : "受控边界断言" };
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException or ExternalException or PlatformNotSupportedException)
        {
            result = new(false, "隔离不可用，自动执行保持禁用（" + stage + "）：" + ex.Message, EvidenceDirectory: session)
            { FailureStage = stage, FailureHResult = ex.HResult, NativeErrorCode = ex is Win32Exception native ? native.NativeErrorCode : null };
        }
        finally
        {
            if (sid != IntPtr.Zero) Native.FreeSid(sid);
            if (profile is not null)
            {
                // Never mask the original diagnostic with a cleanup exception.
                try
                {
                    var removed = profile.Remove();
                    File.WriteAllText(Path.Combine(session, "profile-lifecycle.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { profile.Profile, profile.Sid,
                            State = removed ? "removed-folder-and-mapping-absent" : "cleanup-not-confirmed", Utc = DateTimeOffset.UtcNow }));
                    result = result with { TemporaryProfileRemoved = removed };
                    if (!removed) result = result with { Passed = false, Message = result.Message + " 临时配置清理未确认，请查看生命周期记录。" };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ExternalException)
                {
                    result = result with { Passed = false, TemporaryProfileRemoved = false, Message = result.Message + " 临时配置清理验证失败：" + ex.Message };
                }
                finally { profile.Dispose(); }
            }
        }
        result = result with { Policy = policy.ToString(), StartupTraced = traceStartup };
        File.WriteAllText(Path.Combine(session, "host-result.json"), System.Text.Json.JsonSerializer.Serialize(result));
        return result;
    }

    private static void Grant(string directory, SecurityIdentifier sid, FileSystemRights rights)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static uint StartLpac(string executable, string[] args, string workingDirectory, IntPtr sid,
        Action<string>? checkpoint, IsolationProbePolicy probePolicy, bool traceStartup, bool holdForLifecycle)
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot, ["WINDIR"] = systemRoot,
            ["LOCALAPPDATA"] = args[0], ["APPDATA"] = args[0], ["USERPROFILE"] = args[0],
            ["TEMP"] = args[0], ["TMP"] = args[0],
            ["RIM_PROBE_PORT"] = Environment.GetEnvironmentVariable("RIM_PROBE_PORT") ?? ""
        };
        if (holdForLifecycle) variables["RIM_PROBE_HOLD"] = "1";
        return RestrictedProcess.Run(executable, args, workingDirectory, sid, checkpoint, probePolicy,
            traceStartup, variables, Path.GetDirectoryName(workingDirectory)!);
    }

    private static class Native
    {
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
        [DllImport("advapi32.dll")] internal static extern IntPtr FreeSid(IntPtr sid);
    }
}