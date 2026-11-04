using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace RimWise.Integration.Safety;

internal sealed class RestrictedProcessTerminationException() : IOException("未确认受限进程退出，保留会话以便恢复；禁止继续测试。");

/// <summary>Shared suspended LPAC launch, exact token verification and creation-time Job lifetime.
/// Internal trusted callers supply fixed arguments and a clean environment; there is no shell fallback.</summary>
[SupportedOSPlatform("windows")]
internal static class RestrictedProcess
{
    internal static uint Run(string executable, string[] args, string workingDirectory, IntPtr sid,
        Action<string>? checkpoint, IsolationProbePolicy probePolicy, bool traceStartup, IReadOnlyDictionary<string, string> variables, string evidenceDirectory,
        Func<IntPtr, uint, uint>? observe = null, Action<IntPtr>? verifyAccess = null)
    {
        using var requestedCapabilities = new ProbeCapabilities(probePolicy);
        IntPtr attributes = IntPtr.Zero, capabilities = IntPtr.Zero, policy = IntPtr.Zero, job = IntPtr.Zero,
            jobList = IntPtr.Zero, childPolicy = IntPtr.Zero, environment = IntPtr.Zero;
        var attributesInitialized = false;
        var process = new Native.ProcessInformation();
        try
        {
            nuint size = 0;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 4, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!Native.InitializeProcThreadAttributeList(attributes, 4, 0, ref size)) ThrowLast("InitializeProcThreadAttributeList");
            attributesInitialized = true;
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SecurityCapabilities>());
            Marshal.StructureToPtr(new Native.SecurityCapabilities { AppContainerSid = sid,
                Capabilities = requestedCapabilities.Entries, CapabilityCount = requestedCapabilities.Count }, capabilities, false);
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x20009, capabilities, (nuint)Marshal.SizeOf<Native.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero)) ThrowLast("UpdateProcThreadAttribute(SecurityCapabilities)");
            policy = Marshal.AllocHGlobal(4); Marshal.WriteInt32(policy, 1); // opt out of ALL APPLICATION PACKAGES
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x2000f, policy, 4, IntPtr.Zero, IntPtr.Zero)) ThrowLast("UpdateProcThreadAttribute(LPAC)");
            job = Native.CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) ThrowLast("CreateJobObjectW");
            var limits = new Native.JobLimits { Basic = new Native.BasicJobLimits { LimitFlags = 0x2000 | 0x8, ActiveProcessLimit = 1 } };
            var limitSize = Marshal.SizeOf<Native.JobLimits>(); var limitPtr = Marshal.AllocHGlobal(limitSize);
            try
            {
                Marshal.StructureToPtr(limits, limitPtr, false);
                if (!Native.SetInformationJobObject(job, 9, limitPtr, (uint)limitSize)) ThrowLast("SetInformationJobObject");
            }
            finally { Marshal.FreeHGlobal(limitPtr); }
            // Assign as part of process creation. Creating suspended then assigning separately
            // leaves an orphan window if the host is killed between those two system calls.
            jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job);
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x2000d, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                ThrowLast("UpdateProcThreadAttribute(JobList)");
            childPolicy = Marshal.AllocHGlobal(4); Marshal.WriteInt32(childPolicy, 1); // PROCESS_CREATION_CHILD_PROCESS_RESTRICTED
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x2000e, childPolicy, 4, IntPtr.Zero, IntPtr.Zero))
                ThrowLast("UpdateProcThreadAttribute(ChildPolicy)");
            var startup = new Native.StartupInfoEx { Attributes = attributes };
            startup.Startup.Cb = Marshal.SizeOf<Native.StartupInfoEx>();
            var line = new StringBuilder(string.Join(" ", new[] { executable }.Concat(args).Select(Quote)));
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            // Created inside Job, suspended -> verify token -> resume. No unrestricted fallback.
            var flags = 0x80000u | 0x4u | 0x08000000u | 0x400u;
            if (traceStartup) flags |= ProbeStartupTrace.DebugOnlyThisProcess;
            if (!Native.CreateProcessW(executable, line, IntPtr.Zero, IntPtr.Zero, false, flags,
                environment, workingDirectory, ref startup, out process)) ThrowLast("CreateProcessW");
            if (!Native.IsProcessInJob(process.Process, job, out var assigned) || !assigned) throw new IOException("探针未进入受限 Job。");
            if (!Native.OpenProcessToken(process.Process, 8 | 2, out var token)) ThrowLast("OpenProcessToken");
            try
            {
                if (!Native.GetTokenInformation(token, 29, out var appContainer, 4, out _)) ThrowLast("GetTokenInformation(AppContainer)");
                if (appContainer == 0) throw new IOException("子进程不是 AppContainer。");
                VerifyTokenIdentity(token, sid, requestedCapabilities.ExpectedSids);
                VerifyLpacAccess(token);
                verifyAccess?.Invoke(token);
                File.WriteAllText(Path.Combine(evidenceDirectory, "token-evidence.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { process.ProcessId, AppContainer = true,
                        ProfileSidMatched = true, CapabilityCount = requestedCapabilities.Count,
                        CapabilitySids = requestedCapabilities.ExpectedSids, Policy = probePolicy.ToString(),
                        LpacGrantedAccess = 2, JobAssigned = true,
                        JobAssignedAtCreation = true, ChildProcessPolicyRestricted = true,
                        ActiveProcessLimit = 1, KillOnJobClose = true }));
            }
            finally { Native.CloseHandle(token); }
            checkpoint?.Invoke("token-verified-suspended");
            if (Native.ResumeThread(process.Thread) == uint.MaxValue) ThrowLast("ResumeThread");
            checkpoint?.Invoke("process-resumed");
            if (traceStartup)
                return ProbeStartupTrace.Observe(process.Process, process.ProcessId,
                    Path.Combine(evidenceDirectory, "startup-trace.jsonl"));
            if (observe is not null) return observe(process.Process, process.ProcessId);
            if (Native.WaitForSingleObject(process.Process, 20000) != 0) throw new IOException("隔离探针超时。");
            if (!Native.GetExitCodeProcess(process.Process, out var exit)) ThrowLast("GetExitCodeProcess");
            return exit;
        }
        finally
        {
            // Kill suspended or timed-out children as well; close-on-job guarantees no survivor.
            var stopped = true;
            if (process.Process != IntPtr.Zero)
            {
                Native.TerminateProcess(process.Process, 1);
                if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
                stopped = Native.WaitForSingleObject(process.Process, 5000) == 0;
                Native.CloseHandle(process.Process);
            }
            if (process.Thread != IntPtr.Zero) Native.CloseHandle(process.Thread);
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (capabilities != IntPtr.Zero) Marshal.FreeHGlobal(capabilities);
            if (policy != IntPtr.Zero) Marshal.FreeHGlobal(policy);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (childPolicy != IntPtr.Zero) Marshal.FreeHGlobal(childPolicy);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (!stopped) throw new RestrictedProcessTerminationException();
        }
    }

    private static void VerifyTokenIdentity(IntPtr token, IntPtr expectedSid, string[] expectedCapabilities)
    {
        var buffer = TokenBuffer(token, 31); // TOKEN_APPCONTAINER_INFORMATION
        try
        {
            var actual = Marshal.ReadIntPtr(buffer);
            if (actual == IntPtr.Zero || !new SecurityIdentifier(actual).Equals(new SecurityIdentifier(expectedSid)))
                throw new IOException("探针身份与临时配置不匹配。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        buffer = TokenBuffer(token, 30); // TOKEN_GROUPS (capabilities)
        try
        {
            var count = Marshal.ReadInt32(buffer);
            if (count != expectedCapabilities.Length) throw new IOException("探针能力数量与明确请求不符。");
            var actual = new List<string>();
            // TOKEN_GROUPS aligns SID_AND_ATTRIBUTES to pointer alignment after GroupCount.
            for (var index = 0; index < count; ++index)
            {
                var entry = Marshal.PtrToStructure<ProbeCapabilities.SidAndAttributes>(buffer + IntPtr.Size
                    + index * Marshal.SizeOf<ProbeCapabilities.SidAndAttributes>());
                if (entry.Attributes != 4) throw new IOException("探针能力标志不符合预期。");
                actual.Add(new SecurityIdentifier(entry.Sid).Value);
            }
            if (!actual.Order().SequenceEqual(expectedCapabilities.Order())) throw new IOException("探针获得了非预期能力。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr TokenBuffer(IntPtr token, int kind)
    {
        if (Native.GetTokenInformationBuffer(token, kind, IntPtr.Zero, 0, out var size)
            || Marshal.GetLastWin32Error() != 122 || size < 4 || size > 65536)
            throw new IOException("无法获取探针身份信息长度。");
        var buffer = Marshal.AllocHGlobal(size);
        if (Native.GetTokenInformationBuffer(token, kind, buffer, size, out _)) return buffer;
        var error = Marshal.GetLastWin32Error(); Marshal.FreeHGlobal(buffer);
        throw new Win32Exception(error, "无法读取探针身份信息。");
    }

    private static void VerifyLpacAccess(IntPtr token)
    {
        // Effective-access test adapted from Chromium's CheckLpacToken test design.
        // Source and BSD notice: docs/THIRD_PARTY.md, packaging/licenses/chromium-LICENSE.txt.
        // Test the effective access instead of an unsupported TokenInformationClass.
        // World permits both bits; ordinary application packages permit bit 1;
        // restricted application packages permit bit 2. LPAC must receive only bit 2.
        var descriptor = new RawSecurityDescriptor("O:SYG:SYD:(A;;0x3;;;WD)(A;;0x1;;;AC)(A;;0x2;;;S-1-15-2-2)");
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        if (!Native.DuplicateToken(token, 2, out var impersonation)) ThrowLast("DuplicateToken");
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            uint size = 1024;
            var mapping = new Native.GenericMapping();
            if (!Native.AccessCheck(bytes, impersonation, 0x02000000, ref mapping, buffer, ref size, out var granted, out var allowed))
                ThrowLast("AccessCheck(LPAC)");
            if (!allowed || granted != 2) throw new IOException($"LPAC 权限断言失败：allowed={allowed}, granted=0x{granted:X}。");
        }
        finally { Marshal.FreeHGlobal(buffer); Native.CloseHandle(impersonation); }
    }
    private static string Quote(string value)
    {
        if (value.Contains('"') || value.EndsWith('\\') || value.Any(char.IsControl)) throw new ArgumentException("探针参数无效。");
        return "\"" + value + "\"";
    }
    private static void ThrowLast(string api)
    {
        var code = Marshal.GetLastWin32Error();
        throw new Win32Exception(code, $"{api} ({code}): {new Win32Exception(code).Message}");
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityCapabilities { internal IntPtr AppContainerSid, Capabilities; internal uint CapabilityCount, Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        { internal int Cb; internal IntPtr Reserved, Desktop, Title; internal uint X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags; internal ushort ShowWindow,Reserved2; internal IntPtr ReservedPtr,StdInput,StdOutput,StdError; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr Process,Thread; internal uint ProcessId,ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicJobLimits { internal long ProcessUserTime, JobUserTime; internal uint LimitFlags; internal nuint MinWorkingSet,MaxWorkingSet; internal uint ActiveProcessLimit; internal nuint Affinity; internal uint Priority,Scheduling; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobLimits { internal BasicJobLimits Basic; internal IoCounters Io; internal nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)] internal struct GenericMapping { internal uint Read, Write, Execute, All; }
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
        [DllImport("advapi32.dll")] internal static extern IntPtr FreeSid(IntPtr sid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,nuint attribute,IntPtr value,nuint size,IntPtr previous,IntPtr returnSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string working,ref StartupInfoEx startup,out ProcessInformation process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateJobObjectW(IntPtr attributes,string? name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(IntPtr job,int kind,IntPtr info,uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool assigned);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(IntPtr process,out uint code);
        [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(IntPtr process,uint code);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(IntPtr token,int kind,out int value,int size,out int returned);
        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] internal static extern bool GetTokenInformationBuffer(IntPtr token,int kind,IntPtr value,int size,out int returned);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool DuplicateToken(IntPtr token,int level,out IntPtr duplicate);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool AccessCheck(byte[] descriptor,IntPtr token,uint desired,ref GenericMapping mapping,IntPtr privileges,ref uint privilegeLength,out uint granted,out bool allowed);
    }
}
