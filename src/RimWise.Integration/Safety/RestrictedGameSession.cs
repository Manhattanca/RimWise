using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;

namespace RimWise.Integration.Safety;

internal sealed record GameSessionResult(bool ProcessStarted, uint? ExitCode, bool GrantsRemoved,
    bool ProfileRemoved, string Message, bool MapSuccessVerified = false);

/// <summary>Development real-game adapter; not connected to production automatic maintenance.
/// The caller must obtain permission for the reviewable read plan before invoking Run.</summary>
[SupportedOSPlatform("windows")]
internal static class RestrictedGameSession
{
    internal static GameSessionResult Run(GameReadPlan plan, string session, int seconds,
        Action<string>? progress = null, CancellationToken cancellation = default)
    {
        if (seconds is < 30 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        session = ScopedPaths.Ordinary(session, true);
        var data = ScopedPaths.Child(session, "测试 存档数据");
        if (!File.Exists(Path.Combine(data, "Config", "ModsConfig.xml")))
            throw new IOException("缺少本轮独立测试配置。");
        using var profile = ProbeProfileLease.PrepareGame(session, new WindowsProbeProfiles());
        var sid = new SecurityIdentifier(profile.Sid);
        using var batch = new PinnedDirectorySet();
        // Record every exact object before ANY original DACL changes. No whole-DACL restoration.
        var inspecting = new GameTestProgress("正在核对输入文件", plan.ReadObjects.Length, progress);
        inspecting.Update(0);
        var grants = plan.ReadObjects.Select((path, index) =>
        {
            cancellation.ThrowIfCancellationRequested();
            var grant = SessionFileAccess.Inspect(path, batch: batch);
            inspecting.Update(index + 1);
            return grant;
        }).Append(SessionFileAccess.Inspect(data, writable: true, batch: batch)).ToArray();
        var excluded = plan.ExcludedRootFiles.Select(path => SessionFileAccess.Inspect(path, batch: batch)).ToArray();
        SaveNew(Path.Combine(session, "file-grants.json"), grants);
        SaveNew(Path.Combine(session, "read-plan.json"), plan);
        var started = false;
        var grantsRemoved = false;
        var profileRemoved = false;
        var cleanupAllowed = true;
        var attempted = new List<FileGrant>();
        uint? exit = null;
        var message = "未开始";
        try
        {
            cancellation.ThrowIfCancellationRequested();
            profile.Create();
            progress?.Invoke("PROFILE_CREATED 隔离身份已创建");
            // Internal preflight belongs to this real launch, not a separate diagnostic round.
            // Refuse original grants if this host cannot perform and remove scoped fixture ACLs.
            SessionFileAccess.VerifyFixture(session, sid);
            progress?.Invoke("FILE_ACCESS_PREFLIGHT_PASSED 权限预检已通过");
            var granting = new GameTestProgress("正在设置临时只读权限", grants.Length, progress);
            granting.Update(0);
            foreach (var grant in grants)
            {
                cancellation.ThrowIfCancellationRequested();
                attempted.Add(grant);
                SessionFileAccess.Add(grant, sid, batch);
                granting.Update(attempted.Count);
            }
            progress?.Invoke("READ_GRANTS_APPLIED count=" + grants.Length);
            var nativeSid = Marshal.AllocHGlobal(sid.BinaryLength);
            try
            {
                var sidBytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(sidBytes, 0);
                Marshal.Copy(sidBytes, 0, nativeSid, sidBytes.Length);
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SystemRoot"] = windows, ["WINDIR"] = windows,
                    ["USERPROFILE"] = data, ["LOCALAPPDATA"] = data, ["APPDATA"] = data,
                    ["TEMP"] = data, ["TMP"] = data,
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_EnableDiagnostics"] = "0",
                    ["DOTNET_DbgEnableMiniDump"] = "0", ["COMPlus_DbgEnableMiniDump"] = "0"
                };
                exit = RestrictedProcess.Run(plan.Executable,
                    ["-savedatafolder=" + data, "-quicktest", "-logFile", Path.Combine(data, "Player.log")],
                    plan.Game, nativeSid, point =>
                    {
                        if (point == "process-resumed") started = true;
                        progress?.Invoke(point);
                    }, IsolationProbePolicy.RegistryRead, false, variables, session,
                    observe: (handle, pid) => Observe(handle, pid, data, seconds, progress, cancellation),
                    verifyAccess: token =>
                    {
                        var verifying = new GameTestProgress("正在检查隔离进程的实际访问权限", grants.Length, progress);
                        verifying.Update(0);
                        var checkedCount = 0;
                        foreach (var grant in grants)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            SessionFileAccess.Verify(grant, token, expectRead: true, batch: batch);
                            verifying.Update(++checkedCount);
                        }
                        foreach (var file in excluded) SessionFileAccess.Verify(file, token, expectRead: false, batch: batch);
                        foreach (var file in Directory.EnumerateFiles(session, "*.json", SearchOption.TopDirectoryOnly))
                            SessionFileAccess.Verify(SessionFileAccess.Inspect(file, batch: batch), token, expectRead: false, batch: batch);
                        SaveNew(Path.Combine(session, "file-boundary-check.json"), new
                        {
                            OriginalsReadOnly = true, ReadObjects = grants.Length - 1,
                            ExcludedFilesDenied = excluded.Length, WritableDirectory = data,
                            CheckedBeforeResume = true, Utc = DateTimeOffset.UtcNow
                        });
                    });
                message = exit == 0 ? "受限进程正常退出，尚未确认地图测试通过。"
                    : $"游戏启动后以错误码 {exit} 退出，未通过运行测试。";
            }
            finally { Marshal.FreeHGlobal(nativeSid); }
        }
        catch (RestrictedProcessTerminationException ex)
        { cleanupAllowed = false; message = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or ExternalException or OperationCanceledException)
        { message = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            // Cleanup has its own batch and must be able to reopen original directories
            // for ACL edits after the game is stopped.
            batch.Dispose();
            // RestrictedProcess owns and stops the child before returning or throwing.
            // A failed cleanup remains recoverable, and prevents reporting a passed session.
            try { if (cleanupAllowed) grantsRemoved = CleanGrants(attempted.ToArray(), sid, session, progress); }
            finally
            {
                try { if (cleanupAllowed) profileRemoved = profile.Remove(); }
                catch (Exception ex) { progress?.Invoke("PROFILE_CLEANUP_FAILED " + ex.GetType().Name); }
            }
        }
        var result = new GameSessionResult(started, exit, grantsRemoved, profileRemoved, message);
        SaveNew(Path.Combine(session, "restricted-game-result.json"), result);
        return result;
    }

    internal static GameSessionResult Recover(string session)
    {
        session = ScopedPaths.Ordinary(session, true);
        using var profile = ProbeProfileLease.OpenGame(session, new WindowsProbeProfiles());
        // A creation-time Job closes with its host. Refuse recovery while its recorded PID exists,
        // even on PID reuse: the operator may retry after confirming no test process remains.
        var evidence = Path.Combine(session, "token-evidence.json");
        if (File.Exists(evidence))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(evidence));
            var pid = json.RootElement.GetProperty("ProcessId").GetInt32();
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) throw new IOException("记录的进程编号仍存在，暂不清理会话权限。");
            }
            catch (ArgumentException) { }
        }
        var path = Path.Combine(session, "file-grants.json");
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("权限记录过大。");
        var grants = JsonSerializer.Deserialize<FileGrant[]>(File.ReadAllText(path)) ?? throw new InvalidDataException("权限记录为空。");
        if (grants.Length is 0 or > 100001 || grants.Count(g => g.Writable) != 1
            || grants.Single(g => g.Writable).Path != ScopedPaths.Child(session, "测试 存档数据")
            || grants.Select(g => g.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != grants.Length)
            throw new InvalidDataException("权限记录范围无效。");
        var removed = CleanGrants(grants, new SecurityIdentifier(profile.Sid), session, null);
        var profileRemoved = profile.Remove();
        var result = new GameSessionResult(false, null, removed, profileRemoved,
            removed && profileRemoved ? "会话权限和配置清理已确认。" : "清理未全部确认，请保留会话记录。");
        SaveNew(Path.Combine(session, "recovery-" + Guid.NewGuid().ToString("N") + ".json"), result);
        return result;
    }

    private static bool CleanGrants(FileGrant[] grants, SecurityIdentifier sid, string session, Action<string>? progress)
    {
        var errors = new List<object>();
        using var batch = new PinnedDirectorySet();
        var cleaning = new GameTestProgress("正在撤除临时文件权限", grants.Length, progress);
        cleaning.Update(0);
        var cleanedCount = 0;
        foreach (var grant in grants.Reverse())
            try
            {
                SessionFileAccess.Remove(grant, sid, batch);
                if (grant.Writable && grant.Directory) CleanDataChildren(grant.Path, sid);
            }
            catch (Exception ex)
            {
                errors.Add(new { grant.Path, Error = ex.GetType().Name, ex.Message });
                progress?.Invoke("GRANT_CLEANUP_FAILED " + grant.Path);
            }
            finally { cleaning.Update(++cleanedCount); }
        SaveNew(Path.Combine(session, "grant-cleanup-" + Guid.NewGuid().ToString("N") + ".json"),
            new { Sid = sid.Value, Removed = errors.Count == 0, Errors = errors });
        return errors.Count == 0;
    }

    private static void CleanDataChildren(string directory, SecurityIdentifier sid, int depth = 0)
    {
        // Data was writable by third-party code: validate and pin each level before enumeration.
        // Refuse reparse points and hard links instead of following them during trusted cleanup.
        if (depth > 64) throw new IOException("测试数据目录过深，停止清理并保留记录。");
        using var pinned = PinnedDirectory.Open(directory);
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (entries.Length > 100000) throw new IOException("测试数据项目过多，停止清理并保留记录。");
        foreach (var entry in entries)
        {
            var child = SessionFileAccess.Inspect(entry, writable: true);
            SessionFileAccess.Remove(child, sid);
            if (child.Directory) CleanDataChildren(child.Path, sid, depth + 1);
        }
    }

    private static uint Observe(IntPtr handle, uint pid, string data, int seconds, Action<string>? progress, CancellationToken cancellation)
    {
        progress?.Invoke($"GAME_STARTED pid={pid}; bounded={seconds}s");
        var watch = Stopwatch.StartNew();
        var nextProgress = 0;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Native.WaitForSingleObject(handle, 500) == 0)
            {
                if (!Native.GetExitCodeProcess(handle, out var exit)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return exit;
            }
            if (watch.Elapsed.TotalSeconds >= nextProgress)
            {
                progress?.Invoke("GAME_PROGRESS seconds=" + (int)watch.Elapsed.TotalSeconds);
                nextProgress += 30;
            }
        }
        throw new OperationCanceledException("达到测试时长上限，已终止受限游戏。");
    }

    internal static void SaveNew<T>(string path, T value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
        file.Flush(flushToDisk: true);
    }
    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(IntPtr handle, out uint exit);
    }
}
