using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Safety;
using System.IO.Compression;
using System.Text.Json;

namespace RimWise.Integration.Diagnostics;

public sealed record AutomaticLogSettings(bool AutoSave = false, string Directory = "");
public sealed record AutomaticLogRun(DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc,
    string[] Sessions, string[] TaskIds);

/// <summary>Only preferences and an index, not a second copy of logs or credentials.</summary>
public sealed partial class AutomaticSortingLogStore(string profile)
{
    private readonly AppJsonFile<AutomaticLogSettings> settings = new(profile, "automatic-log-settings", keepPreviousVersions: false);
    private readonly AppJsonFile<RunDocument> lastRun = new(profile, "automatic-log-last", 256 * 1024, keepPreviousVersions: false);
    private sealed record RunDocument(AutomaticLogRun? Run);

    public string DefaultDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profile))!, "RimWise-Logs");
    public AutomaticLogSettings LoadSettings() => settings.Load() ?? new(false, DefaultDirectory);

    public AutomaticLogSettings SaveSettings(AutomaticLogSettings value)
    {
        var directory = ValidateDirectory(value.Directory);
        settings.Load(); // Preserve optimistic concurrency validation in AppJsonFile.
        settings.Save(value with { Directory = directory });
        return value with { Directory = directory };
    }

    public string ValidateDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("请选择完整的日志保存路径。");
        var full = ScopedPaths.Ordinary(Path.GetFullPath(directory)).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("日志导出目录请选择用户数据目录之外的位置，避免与运行记录混在一起。");
        if (File.Exists(full)) throw new ArgumentException("请选择文件夹，不能选择文件。");
        return full + (full.EndsWith(':') ? Path.DirectorySeparatorChar : "");
    }

    public AutomaticLogRun? LoadLastRun()
    {
        var value = lastRun.Load()?.Run;
        if (value is not null) Validate(value);
        return value;
    }

    // Only our named diagnostic archives in the selected directory; never recurse into
    // an arbitrary user-selected folder or remove unrelated ZIPs based on extension.
    public (int Removed, int Skipped) DeleteExportedLogs(string? selectedDirectory = null)
    {
        using var operation = AcquireExportLease();
        var directory = ValidateDirectory(selectedDirectory ?? LoadSettings().Directory);
        if (!Directory.Exists(directory)) return (0, 0);
        var removed = 0;
        var skipped = 0;
        using var pins = new PinnedDirectorySet();
        pins.PinParent(Path.Combine(directory, "log-cleanup"));
        foreach (var file in Directory.EnumerateFiles(directory, "RimWise-sorting-*.zip"))
        {
            try
            {
                ScopedPaths.Ordinary(file, true);
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var manifest = zip.GetEntry("manifest.json");
                    if (manifest is null || manifest.Length > 1024 * 1024) { skipped++; continue; }
                    using var input = manifest.Open();
                    using var json = JsonDocument.Parse(input);
                    if (!json.RootElement.TryGetProperty("format", out var format)
                        || format.GetString() != "RimWiseDiagnostic/1") { skipped++; continue; }
                }
                File.Delete(ScopedPaths.Ordinary(file, true));
                removed++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            { skipped++; }
        }
        return (removed, skipped);
    }

    public void SaveRun(AutomaticLogRun value)
    {
        Validate(value);
        lastRun.Load();
        lastRun.Save(new(value));
    }

    // Discover task/session identities even if the runner threw before returning its result.
    // No raw task contents or arbitrary paths are persisted in the index.
    public AutomaticLogRun Capture(DateTimeOffset started, DateTimeOffset? ended = null, string? resumedTask = null)
    {
        var tasks = new AiTaskRecoveryStore(profile).InspectUsage()
            .Where(t => t.Id == resumedTask || t.Checkpoint?.StartedUtc >= started
                && (ended is null || t.Checkpoint.StartedUtc <= ended)).ToArray();
        var root = ScopedPaths.Child(profile, "Data/GameTests");
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(root))
            foreach (var path in Directory.EnumerateDirectories(root, "game-*"))
                if (IsSession(Path.GetFileName(path)) && System.IO.Directory.GetCreationTimeUtc(path) >= started.UtcDateTime
                    && (ended is null || System.IO.Directory.GetCreationTimeUtc(path) <= ended.Value.UtcDateTime))
                    sessions.Add(Path.GetFileName(path));
        foreach (var task in tasks)
            foreach (var path in (task.Checkpoint?.Feedback?.LogPaths ?? [])
                .Concat(task.Checkpoint?.Feedback?.StartupFailure?.LogPaths ?? []))
            {
                var name = Path.GetRelativePath(root, StoredEvidence.Resolve(path)).Split(Path.DirectorySeparatorChar)[0];
                if (IsSession(name)) sessions.Add(name);
            }
        return new(started, ended, sessions.Order(StringComparer.Ordinal).ToArray(), tasks.Select(t => t.Id).ToArray());
    }

    private static bool IsSession(string name) => name.StartsWith("game-", StringComparison.Ordinal)
        && Guid.TryParseExact(name[5..], "N", out _);

    private static void Validate(AutomaticLogRun value)
    {
        if (value.StartedUtc == default || value.EndedUtc < value.StartedUtc
            || value.Sessions is null || value.TaskIds is null || value.Sessions.Length > 2000 || value.TaskIds.Length > 2000
            || value.Sessions.Any(s => s is null || !IsSession(s))
            || value.TaskIds.Any(s => !Guid.TryParseExact(s, "N", out _)))
            throw new InvalidDataException("上次自动排序日志索引无效。");
    }
}
