using System.Text.Json;
using RimWise.Integration.Game;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;
using RimWise.Integration.Configuration;

namespace RimWise.Integration.Diagnostics;

public sealed record TestDataCleanup(long BeforeBytes, long AfterBytes, int RemovedFiles, int CompressedFiles,
    bool Busy, int ProtectedFiles)
{
    public bool WithinLimit => AfterBytes <= TestDataRetention.MaximumBytes;
}

public sealed record TestStorageFile(string Path, long Bytes);
public sealed record TestStorageSummary(long TotalBytes, long ProtectedEvidenceBytes, long CompletedEvidenceBytes,
    long CacheBytes, long OtherBytes, IReadOnlyList<TestStorageFile> LargestOtherFiles);

/// <summary>Only disposable test artifacts and UI logs are managed here. Recovery journals,
/// credentials, sorting backups and token ledgers are never deletion candidates.</summary>
public static class TestDataRetention
{
    public const long MaximumBytes = 100_000_000;
    private const long GameBytes = 90_000_000;
    private const long UiBytes = 10_000_000;
    private static readonly string[] Caches = ["TextureCache", "ShaderCache", "BundleCache"];

    public static TestDataCleanup MaintainProfile(string profile, bool clearLogs = false, bool preserveLastRun = true)
    {
        var root = ScopedPaths.Child(profile, "Data/GameTests");
        Directory.CreateDirectory(root);
        // Shares the launcher's lease, also covering the delay before Steam starts a process.
        FileStream lease;
        try { lease = new(ScopedPaths.Child(root, "ordinary-game-test.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return new(0, 0, 0, 0, true, 0); }
        using (lease)
        {
            if (GameProcessService.IsAnyRunning()) return new(0, 0, 0, 0, true, 0);
            // This lease also excludes a live launcher before Steam creates its process.
            // Startup cleanup only detaches owned remnants; it never stops a player's game.
            try
            {
                var paths = new PathSettingsStore(profile).Load();
                if (paths is not null && Directory.Exists(paths.GameDirectory))
                    TestHelperMount.CleanInterruptedOwnedRoot(paths.GameDirectory, root);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
            { DiagnosticEventLog.Current?.Record("test-helper", "startup-cleanup-failed", ex.Message); }
            var game = MaintainOwnedRoot(root, clearLogs, ProtectedSessions(profile, root, preserveLastRun));
            var ui = MaintainUi(ScopedPaths.Child(profile, "Data/Diagnostics"), clearLogs);
            return new(game.BeforeBytes + ui.BeforeBytes, game.AfterBytes + ui.AfterBytes,
                game.RemovedFiles + ui.RemovedFiles, game.CompressedFiles + ui.CompressedFiles,
                false, game.ProtectedFiles + ui.ProtectedFiles);
        }
    }

    // Caller owns ordinary-game-test.lock and has confirmed the game has exited.
    internal static TestDataCleanup MaintainOwnedRoot(string root, bool clearLogs = false, HashSet<string>? protectedSessions = null)
    {
        root = ScopedPaths.Ordinary(root, true);
        var before = Size(root);
        var removed = 0; var compressed = 0;
        var evictable = new List<string>();
        foreach (var session in Directory.EnumerateDirectories(root, "game-*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(session)[5..], "N", out _)) continue;
            ScopedPaths.Ordinary(session, true);
            // Unfinished journals remain for process/helper recovery after a crash.
            var completed = Finished(session) && protectedSessions?.Contains(session) != true;
            foreach (var attempt in Directory.EnumerateDirectories(session, "attempt-*"))
            {
                ScopedPaths.Ordinary(attempt, true);
                foreach (var name in Caches)
                {
                    var cache = ScopedPaths.Child(attempt, name);
                    if (Directory.Exists(cache)) removed += DeleteCache(cache);
                }
                foreach (var temporary in Files(attempt).Where(p => p.EndsWith(".compressing", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    var original = temporary[..^".compressing".Length];
                    if (IsEvidence(original) && File.Exists(StoredEvidence.Resolve(original)))
                    { Delete(temporary); removed++; }
                }
                foreach (var file in Files(attempt).Where(IsEvidence).Select(LogicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(StoredEvidence.Resolve).ToArray())
                {
                    if (clearLogs && completed)
                    {
                        Delete(file); removed++;
                        if (!file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && File.Exists(file + ".gz"))
                        { Delete(file + ".gz"); removed++; }
                        continue;
                    }
                    if (!file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                    { StoredEvidence.Compress(file); compressed++; }
                    if (completed) evictable.Add(StoredEvidence.Resolve(file));
                }
            }
        }
        // Static analysis cache is reproducible, not an AI task or billing receipt.
        var analysis = ScopedPaths.Child(root, "AnalysisCache");
        if (Directory.Exists(analysis)) evictable.AddRange(Files(analysis));
        // Recovery/configuration and unknown Mod output are not disposable logs. Counting
        // them here used to evict every useful log, then permanently reject new tests.
        var total = Files(root).Where(IsEvidence).Sum(f => new FileInfo(f).Length)
            + evictable.Where(f => !IsEvidence(f)).Sum(f => new FileInfo(f).Length);
        foreach (var file in evictable.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).OrderBy(File.GetLastWriteTimeUtc))
        {
            if (total <= GameBytes && !clearLogs) break;
            var length = new FileInfo(file).Length;
            Delete(file); removed++; total -= length;
        }
        return new(before, Size(root), removed, compressed, false,
            Files(root).Count(file => IsEvidence(file) && !evictable.Contains(file, StringComparer.OrdinalIgnoreCase)));
    }

    private static TestDataCleanup MaintainUi(string root, bool clear)
    {
        if (!Directory.Exists(root)) return new(0, 0, 0, 0, false, 0);
        var before = Size(root); var removed = 0; var compressed = 0; var busy = 0;
        var candidates = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "ui-*.jsonl*")
            .Where(p => p.EndsWith(".jsonl") || p.EndsWith(".jsonl.gz"))
            .Select(LogicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(StoredEvidence.Resolve).ToArray())
        {
            if (!(file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                // Active UI writer denies this exclusive read. Do not truncate its log.
                using (var probe = new FileStream(ScopedPaths.Ordinary(file, true), FileMode.Open, FileAccess.Read, FileShare.None)) { }
                if (clear)
                {
                    Delete(file); removed++;
                    if (!file.EndsWith(".gz") && File.Exists(file + ".gz")) { Delete(file + ".gz"); removed++; }
                    continue;
                }
                if (!file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) { StoredEvidence.Compress(file); compressed++; }
                candidates.Add(StoredEvidence.Resolve(file));
            }
            catch (IOException) { busy++; }
        }
        var total = Size(root);
        foreach (var file in candidates.OrderBy(File.GetLastWriteTimeUtc))
        {
            if (total <= UiBytes) break;
            var length = new FileInfo(file).Length;
            Delete(file); removed++; total -= length;
        }
        return new(before, Size(root), removed, compressed, false, busy);
    }

    private static bool Finished(string session)
    {
        var path = ScopedPaths.Child(session, "finished.json");
        if (File.Exists(path))
        {
            using var input = File.OpenRead(path);
            if (input.Length > 1024 * 1024) return false;
            try
            {
                using var json = JsonDocument.Parse(input);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("ProcessStopped", out var stopped) && stopped.ValueKind == JsonValueKind.True)
                    return true;
            }
            catch (JsonException) { return false; }
        }
        // Crash recovery writes per-attempt receipts after verifying/stopping the owned process,
        // but never writes finished.json. Old recovered sessions must not remain immortal.
        // This only authorizes retention; it does not turn interrupted tests into successful tests.
        var launched = Directory.EnumerateDirectories(session, "attempt-*")
            .Where(a => File.Exists(ScopedPaths.Child(a, "launch.json"))).ToArray();
        return launched.Length > 0 && launched.All(Recovered);
    }

    private static bool Recovered(string attempt)
    {
        var path = ScopedPaths.Child(attempt, "recovered.json");
        if (!File.Exists(path)) return false;
        using var input = File.OpenRead(ScopedPaths.Ordinary(path, true));
        if (input.Length is 0 or > 64 * 1024) return false;
        try
        {
            using var json = JsonDocument.Parse(input);
            var record = json.RootElement;
            return record.ValueKind == JsonValueKind.Object
                && record.TryGetProperty("RecoveredUtc", out var time) && time.ValueKind == JsonValueKind.String
                && time.TryGetDateTime(out var recovered) && recovered != default && recovered <= DateTime.UtcNow.AddMinutes(1)
                && record.TryGetProperty("Matched", out var matched) && matched.ValueKind is JsonValueKind.True or JsonValueKind.False
                && record.TryGetProperty("OldLogsAreNewEvidence", out var evidence) && evidence.ValueKind == JsonValueKind.False;
        }
        catch (JsonException) { return false; }
    }

    // Size information, never file contents. Includes non-exportable artifacts so quota failures
    // remain diagnosable even when the ordinary diagnostic archive is small.
    public static TestStorageSummary InspectStorage(string profile) =>
        InspectRoot(ScopedPaths.Child(profile, "Data/GameTests"), profile);

    private static TestStorageSummary InspectRoot(string root, string? profile)
    {
        if (!Directory.Exists(root)) return new(0, 0, 0, 0, 0, []);
        var protectedSessions = profile is null ? [] : ProtectedSessions(profile, root);
        var completed = Directory.EnumerateDirectories(root, "game-*")
            .Where(s => Guid.TryParseExact(Path.GetFileName(s)[5..], "N", out _)
                && !protectedSessions.Contains(s) && Finished(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        long protectedBytes = 0, completedBytes = 0, cacheBytes = 0, otherBytes = 0;
        var largest = new List<TestStorageFile>();
        foreach (var file in Files(root))
        {
            var bytes = new FileInfo(file).Length;
            var relative = Path.GetRelativePath(root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts[0] == "AnalysisCache" || parts.Skip(1).SkipLast(1).Any(p => Caches.Contains(p))) cacheBytes += bytes;
            else if (IsEvidence(file))
            {
                if (completed.Contains(Path.Combine(root, parts[0]))) completedBytes += bytes;
                else protectedBytes += bytes;
            }
            else
            {
                otherBytes += bytes;
                largest.Add(new(relative, bytes));
                largest = largest.OrderByDescending(f => f.Bytes).Take(10).ToList();
            }
        }
        return new(protectedBytes + completedBytes + cacheBytes + otherBytes, protectedBytes, completedBytes,
            cacheBytes, otherBytes, largest);
    }

    internal static void EnsureTestSpace(string root)
    {
        if (MaintainTestRoot(root).AfterBytes <= GameBytes) return;
        var summary = InspectRoot(root, ProfileForRoot(root));
        // Retention is a cleanup target, not a game-test budget. Pending evidence cannot
        // be deleted to satisfy it; nor should it prevent the task from ever completing.
        // Real filesystem/write failures still propagate from the launcher.
        DiagnosticEventLog.Current?.Record("test-storage", "retained-data-over-target", JsonSerializer.Serialize(summary));
    }

    private static bool IsEvidence(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name == "output_log.txt"
            || name is "result.json" or "diagnostic-context.json" or "diagnostic-sources.json";
    }

    private static IEnumerable<string> Files(string directory)
    {
        ScopedPaths.Ordinary(directory, true);
        foreach (var file in Directory.EnumerateFiles(directory)) yield return ScopedPaths.Ordinary(file, true);
        foreach (var child in Directory.EnumerateDirectories(directory))
            foreach (var file in Files(child)) yield return file;
    }

    private static long Size(string root) => Files(root).Sum(file => new FileInfo(file).Length);

    private static int DeleteCache(string directory)
    {
        // Validate the complete tree before deleting any entry; never follow a Mod-created link.
        var files = Files(directory).ToArray();
        foreach (var file in files) Delete(file);
        DeleteEmptyTree(directory);
        return files.Length;
    }

    private static void DeleteEmptyTree(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(ScopedPaths.Ordinary(directory, true))) DeleteEmptyTree(child);
        using var pins = new PinnedDirectorySet();
        pins.PinParent(directory);
        Directory.Delete(ScopedPaths.Ordinary(directory, true), recursive: false);
    }

    internal static string LogicalPath(string path) => path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

    // Recovery envelopes contain the selected evidence themselves. Still retain referenced
    // originals for review until the user has finished or discarded the task.
    private static HashSet<string> ProtectedSessions(string profile, string root, bool preserveLastRun = true)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Keep the last run available for the explicit Save last log action after restart.
        try
        {
            var logs = new AutomaticSortingLogStore(profile);
            if (preserveLastRun && logs.LoadLastRun() is { } last)
            {
                if (last.EndedUtc is null) last = logs.Capture(last.StartedUtc, resumedTask: last.TaskIds.FirstOrDefault());
                foreach (var name in last.Sessions) result.Add(ScopedPaths.Child(root, name));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        { DiagnosticEventLog.Current?.Record("test-storage", "last-log-index-unavailable", ex.GetType().Name); }
        foreach (var task in new AiTaskRecoveryStore(profile).InspectUsage())
        {
            if (task.Checkpoint is null || task.Problem is not null)
            { result.UnionWith(Directory.EnumerateDirectories(root, "game-*")); continue; }
            if (task.Checkpoint.Finished && !task.Active) continue;
            var feedback = task.Checkpoint.Feedback;
            // A crash between Testing and TestSaved has no log paths yet. Until the task
            // resumes or is discarded, its evidence cannot be distinguished from older sessions.
            if (task.Checkpoint.Kind == AiTaskKind.Maintenance && task.Checkpoint.Attempts > 0
                && (feedback is null || feedback.Attempt < task.Checkpoint.Attempts))
            { result.UnionWith(Directory.EnumerateDirectories(root, "game-*")); continue; }
            foreach (var path in (feedback?.LogPaths ?? []).Concat(feedback?.StartupFailure?.LogPaths ?? []))
            {
                var relative = Path.GetRelativePath(root, StoredEvidence.Resolve(path));
                var name = relative.Split(Path.DirectorySeparatorChar)[0];
                if (name.StartsWith("game-", StringComparison.Ordinal) && Guid.TryParseExact(name[5..], "N", out _))
                    result.Add(ScopedPaths.Child(root, name));
            }
        }
        return result;
    }

    internal static TestDataCleanup MaintainTestRoot(string root)
    {
        var profile = ProfileForRoot(root);
        return MaintainOwnedRoot(root, protectedSessions: profile is null ? null : ProtectedSessions(profile, root));
    }

    private static string? ProfileForRoot(string root)
    {
        var data = Path.GetDirectoryName(root);
        return data is not null && Path.GetFileName(data).Equals("Data", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(data) : null;
    }

    internal static void MaintainAfterTest(string root)
    {
        try
        {
            if (GameProcessService.IsAnyRunning()) return;
            var result = MaintainTestRoot(root);
            DiagnosticEventLog.Current?.Record("test-storage", "cleanup", $"bytes={result.AfterBytes}; removed={result.RemovedFiles}; withinLimit={result.WithinLimit}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { DiagnosticEventLog.Current?.Record("test-storage", "cleanup-failed", ex.Message); }
    }

    private static void Delete(string file)
    {
        using var pins = new PinnedDirectorySet();
        pins.PinParent(ScopedPaths.Ordinary(file, true));
        File.Delete(ScopedPaths.Ordinary(file, true));
    }
}
