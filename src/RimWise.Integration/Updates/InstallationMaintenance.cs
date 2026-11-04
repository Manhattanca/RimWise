using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Configuration;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Updates;

public sealed record InstallationUpdateResult(string? PreviousVersion, string CurrentVersion,
    bool Verified, int Removed, int Preserved, int Pending);

/// <summary>Per-installation startup bookkeeping. Prepare runs off the UI thread; successful
/// startup is committed only after the main window opens. Missing manifests disable cleanup.</summary>
public sealed class InstallationMaintenance
{
    private sealed record State(int Schema, string Installation, string? LastStartedVersion,
        DateTimeOffset? LastStartedUtc, string? VerifiedManifest, string? PreparedManifest,
        PackageFile[] Files, PackageFile[] Pending);

    private readonly string installation;
    private readonly string userDirectory;
    private readonly string version;
    private readonly string stateDirectory;
    private readonly Func<bool> isInUse;
    private PackageManifest? package;
    private string? fingerprint;
    public string StatePath => ScopedPaths.Child(stateDirectory, "startup.json");
    public string BackupDirectory => ScopedPaths.Child(stateDirectory, "RemovedFiles");

    public InstallationMaintenance(string installationDirectory, string userDataDirectory, string currentVersion,
        Func<bool>? installationInUse = null)
    {
        installation = Path.TrimEndingDirectorySeparator(ScopedPaths.Ordinary(installationDirectory, true));
        userDirectory = ScopedPaths.Ordinary(userDataDirectory);
        version = currentVersion;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation.ToUpperInvariant())));
        stateDirectory = ScopedPaths.Child(userDirectory, "Data/Updates/" + id);
        isInUse = installationInUse ?? OtherInstallationProcess;
    }

    public InstallationUpdateResult Prepare()
    {
        if (!File.Exists(ScopedPaths.Child(installation, PackageManifest.FileName)))
            return new(null, version, false, 0, 0, 0);
        (package, fingerprint) = PackageManifest.Read(installation, version);
        using var statePin = PinnedDirectory.Open(stateDirectory, create: true);
        using var gate = AppDataFileLock.Acquire(ScopedPaths.Child(stateDirectory, "startup.lock"));
        var state = ReadState();
        if (state.VerifiedManifest == fingerprint && state.LastStartedVersion == version && state.Pending.Length == 0)
            return new(state.LastStartedVersion, version, false, 0, 0, 0);

        // Validate the entire replacement before any cleanup. Partial ZIP overlays must not
        // remove the old files which may be needed to diagnose or recover the installation.
        foreach (var file in package.Files)
        {
            var path = ScopedPaths.Child(installation, file.Path);
            using var parent = PinnedDirectory.Open(Path.GetDirectoryName(path)!);
            if (!PackageManifest.HashFile(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"更新文件缺失或内容不完整：{file.Path}。请退出后完整覆盖解压新版 ZIP。");
        }
        var current = package.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = package.Retired.Concat(state.Files).Concat(state.Pending)
            .Where(f => !current.Contains(f.Path))
            .DistinctBy(f => f.Path.ToUpperInvariant() + ":" + f.Sha256.ToUpperInvariant()).ToArray();
        // Going back to an older executable is not an instruction to erase newer resources.
        var downgrade = ReleaseNumber(state.LastStartedVersion) > ReleaseNumber(version);
        if (downgrade) candidates = [];
        if (candidates.Length > 16384) throw new InvalidDataException("待清理记录过多，未执行清理。");
        state = state with { PreparedManifest = fingerprint, Pending = candidates };
        WriteState(state); // Durable intent precedes the first removal.
        var pending = new List<PackageFile>();
        var removed = 0;
        var preserved = 0;
        var busy = candidates.Length > 0 && isInUse();
        foreach (var group in candidates.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var entry = group.First();
            try
            {
                var path = ScopedPaths.Child(installation, entry.Path);
                // User data may be configured inside an installation by an older version.
                if (Within(path, userDirectory)) { preserved++; continue; }
                if (!File.Exists(path)) continue;
                if (busy) { pending.AddRange(group); continue; }
                var hash = PackageManifest.HashFile(path);
                var matching = group.FirstOrDefault(f => f.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
                if (matching is null) { preserved++; continue; }
                if (RetiredPackageFile.Remove(installation, matching, BackupDirectory, version)) removed++;
                else preserved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                pending.AddRange(group); // Occupied or unsafe targets remain untouched; retry later.
            }
        }
        WriteState(state with { Pending = pending.ToArray() });
        return new(state.LastStartedVersion, version, true, removed, preserved, pending.Count);
    }

    public void MarkStartupSuccessful()
    {
        if (package is null || fingerprint is null) return;
        using var statePin = PinnedDirectory.Open(stateDirectory);
        using var gate = AppDataFileLock.Acquire(ScopedPaths.Child(stateDirectory, "startup.lock"));
        var state = ReadState();
        if (state.PreparedManifest != fingerprint && state.VerifiedManifest != fingerprint) return;
        // An installer/another version may have replaced the package while the UI was opening.
        var (_, currentFingerprint) = PackageManifest.Read(installation, version);
        if (currentFingerprint != fingerprint) return;
        WriteState(state with { LastStartedVersion = version, LastStartedUtc = DateTimeOffset.UtcNow,
            VerifiedManifest = fingerprint, Files = package.Files });
    }

    private State ReadState()
    {
        if (!File.Exists(StatePath)) return new(1, installation, null, null, null, null, [], []);
        if (new FileInfo(StatePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("升级记录过大，未执行清理。");
        var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(StatePath));
        if (state is null || state.Schema != 1 || !string.Equals(state.Installation, installation, StringComparison.OrdinalIgnoreCase)
            || state.Files is null || state.Pending is null || state.Files.Length > 4096 || state.Pending.Length > 16384)
            throw new InvalidDataException("升级记录无效，未执行清理。");
        foreach (var file in state.Files.Concat(state.Pending)) PackageManifest.ValidateFile(file);
        return state;
    }

    private void WriteState(State state)
    {
        var temporary = ScopedPaths.Child(stateDirectory, "startup.pending.json");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(true);
        }
        File.Move(temporary, StatePath, overwrite: true);
    }

    private bool OtherInstallationProcess()
    {
        foreach (var name in new[] { "RimWise", "RimWise.SteamBridge", "RimWise.Decompiler", "RimSortWorkbench" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (process.HasExited) continue;
                        var file = process.MainModule?.FileName;
                        if (file is null || Within(Path.GetFullPath(file), installation)) return true;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    { return true; } // Cannot prove that the installation is idle.
                }
            }
        }
        return false;
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static Version ReleaseNumber(string? value) => Version.TryParse(value?.Split('-')[0], out var parsed) ? parsed : new(0, 0);
}
