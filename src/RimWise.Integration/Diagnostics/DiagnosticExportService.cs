using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Diagnostics;

public sealed record DiagnosticExportResult(string Path, long Bytes, int Files, long ReportedTokens,
    long ReservedTokens, int UnknownTasks);

/// <summary>Positive allowlist: credentials, raw settings, saves, caches and recovery envelopes never enter the ZIP.</summary>
public static class DiagnosticExportService
{
    private const long MaximumFileBytes = 32 * 1024 * 1024;
    private const long MaximumArchiveBytes = 256 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new DiagnosticTokenUsageConverter() }
    };
    private static readonly HashSet<string> GameFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "runtime-Player.log", "Player.log", "output_log.txt", "launch.json", "launch-failure.log", "result.json",
        "session.json", "process.json", "finished.json", "recovered.json",
        "diagnostic-sources.json", "diagnostic-context.json", "ModsConfig.xml",
        "intended-ModsConfig.xml", "observed-ModsConfig.xml",
        "workbench-helper-failure.txt", "workbench-progress.xml", "workbench-signal.xml", "workbench-probe.xml",
        "workbench-messages.log", "workbench-factions.txt", "workbench-startup.xml", "workbench-log-locations.xml"
    };

    public static DiagnosticExportResult Export(string userDataDirectory, string destination,
        object? currentState = null, CancellationToken cancellationToken = default, AutomaticLogRun? run = null)
    {
        var root = ScopedPaths.Ordinary(Path.GetFullPath(userDataDirectory), true);
        var target = ScopedPaths.Ordinary(Path.GetFullPath(destination));
        if (target.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择用户数据目录之外的导出位置。");
        // Fail closed if a stored key cannot be decrypted: do not export unchecked error text.
        var settings = new AiSettingsStore(root).Load();
        var redactor = new DiagnosticRedactor(new LocalEncryptedSecretStore(root).ReadDiagnosticSecrets(settings.SecretName));
        var store = new AiTaskRecoveryStore(root);
        var taskSnapshot = store.InspectUsage().Where(t => run is null || run.TaskIds.Contains(t.Id)).ToArray();
        var deleted = store.InspectDeletedUsage().Where(t => run is null || run.TaskIds.Contains(t.Id)).ToArray();
        // Cleanup may publish a receipt between these two reads. Never count that task twice.
        var deletedIds = deleted.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var tasks = taskSnapshot.Where(t => !deletedIds.Contains(t.Id)).ToArray();
        var reported = checked(tasks.Sum(t => (long)(t.Checkpoint?.ReportedTokens ?? 0)) + deleted.Sum(t => (long)t.ReportedTokens));
        var reserved = checked(tasks.Where(t => t.Checkpoint?.UnknownUsage == true).Sum(t => (long)t.Checkpoint!.ReservedTokens)
            + deleted.Where(t => t.UnknownUsage).Sum(t => (long)t.ReservedTokens));
        var unknown = tasks.Count(t => t.Checkpoint?.UnknownUsage == true) + deleted.Count(t => t.UnknownUsage);
        var entries = new List<object>();
        var omissions = new List<object>();
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        long total = 0;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Write(string name, string sanitized)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = Encoding.UTF8.GetBytes(sanitized);
                    if (name != "manifest.json" && (bytes.LongLength > MaximumFileBytes || total + bytes.LongLength > MaximumArchiveBytes - 1024 * 1024))
                    { omissions.Add(new { file = redactor.Text(name), reason = "size-limit" }); return; }
                    var entry = archive.CreateEntry(redactor.Text(name), CompressionLevel.Fastest);
                    using var output = entry.Open();
                    output.Write(bytes);
                    total += bytes.LongLength;
                    entries.Add(new { file = redactor.Text(name), uncompressedBytes = bytes.Length });
                }
                void WriteObject(string name, object value) => Write(name, redactor.Json(JsonSerializer.Serialize(value, Json)));
                try { WriteObject("test-storage.json", TestDataRetention.InspectStorage(root)); }
                catch (Exception ex) when (ReadFailure(ex))
                { omissions.Add(new { file = "test-storage.json", reason = ex.GetType().Name }); }
                // Keep the totals available even if a long request history exceeds the per-file limit.
                WriteObject("token-summary.json", new
                {
                    reportedTokens = reported, unreportedEstimatedTokens = reserved, unknownUsageTasks = unknown,
                    activeTasks = tasks.Count(t => t.Active), damagedTasks = tasks.Count(t => t.Problem is not null),
                    scope = run is null ? "Only locally recorded RimWise requests. Unknown usage is an estimate."
                        : "Tasks used by the last run, including earlier requests when a task was resumed. Unknown usage is an estimate."
                });
                WriteObject("token-usage.json", new
                {
                    reportedTokens = reported, unreportedEstimatedTokens = reserved, unknownUsageTasks = unknown,
                    activeTasks = tasks.Count(t => t.Active), damagedTasks = tasks.Count(t => t.Problem is not null),
                    tasks = tasks.Select(t => new { t.Id, t.Active, t.Problem, t.Checkpoint?.StartedUtc,
                        t.Checkpoint?.Kind, t.Checkpoint?.Phase, t.Checkpoint?.ReportedTokens, t.Checkpoint?.ReservedTokens,
                        t.Checkpoint?.UnknownUsage, t.Checkpoint?.UsageRequests, t.Checkpoint?.UsagePeriods }),
                    deletedTasks = deleted,
                    scope = "Only locally recorded RimWise requests. Unknown usage is an estimate, never zero. Provider wallet totals are excluded."
                });
                WriteObject("application.json", new
                {
                    version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                    collector = Assembly.GetEntryAssembly()?.GetName().Name,
                    productVersion = ApplicationBuildIdentity.ProductVersion(Assembly.GetEntryAssembly()),
                    components = ApplicationBuildIdentity.Capture(),
                    runtime = Environment.Version.ToString(), operatingSystem = Environment.OSVersion.VersionString,
                    settings.Enabled, settings.Provider, settings.Model, settings.PermissionLevel, settings.StreamResponses,
                    providerHost = Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) ? uri.Host : "",
                    currentState, logProblem = DiagnosticEventLog.Current?.Problem,
                    droppedEvents = DiagnosticEventLog.Current?.DroppedEvents ?? 0
                });
                foreach (var task in tasks)
                    WriteObject("tasks/" + task.Id + ".json", task);
                if (run is not null) WriteObject("automatic-run.json", run);
                try { WriteObject("ai-audit.json", new AiAuditStore(root).Load().Where(a => InRun(a.Timestamp, run)).ToArray()); }
                catch (Exception ex) when (ReadFailure(ex)) { omissions.Add(new { file = "ai-audit.json", reason = ex.GetType().Name }); }

                foreach (var file in EvidenceFiles(root, omissions, redactor, run))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var logical = TestDataRetention.LogicalPath(file);
                    var relative = Path.GetRelativePath(root, logical).Replace('\\', '/');
                    try
                    {
                        ScopedPaths.Ordinary(file, true);
                        var content = StoredEvidence.ReadText(file, (int)MaximumFileBytes);
                        if (Path.GetExtension(logical).Equals(".json", StringComparison.OrdinalIgnoreCase))
                            Write(relative, redactor.Json(content));
                        else if (Path.GetExtension(logical).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
                        {
                            var lines = new StringBuilder();
                            var invalid = 0;
                            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            {
                                try
                                {
                                    if (run is not null)
                                    {
                                        using var entry = JsonDocument.Parse(line);
                                        if (!entry.RootElement.TryGetProperty("timestampUtc", out var time)
                                            || !time.TryGetDateTimeOffset(out var timestamp) || !InRun(timestamp, run)) continue;
                                    }
                                    lines.AppendLine(redactor.Json(line));
                                }
                                catch (JsonException) { invalid++; }
                            }
                            if (invalid > 0) omissions.Add(new { file = redactor.Text(relative), reason = "incomplete-or-invalid-lines", count = invalid });
                            Write(relative, lines.ToString());
                        }
                        else Write(relative, redactor.Text(content));
                    }
                    catch (Exception ex) when (ReadFailure(ex))
                    { omissions.Add(new { file = redactor.Text(relative), reason = ex.GetType().Name }); }
                }
                Write("README.txt", "RimWise diagnostic export\nCompact English field names; original game errors and AI advice are preserved.\n"
                    + "Includes current order, latest validated task checkpoints, AI action audits, recent UI events and game test evidence.\n"
                    + "Token totals count each task once, including deleted-task receipts. Request records contain input, output, cache and reasoning usage when reported.\n"
                    + "Unknown usage remains an estimate. Other applications and provider wallet totals are not counted.\n"
                    + "No credential files, raw AI settings, raw HTTP traffic, save games or Mod binaries are exported. Known keys and credential fields are redacted.\n"
                    + (run is null ? "Recent 20 UI logs and 10 game sessions. " : "Selected run sessions and UI time interval; task usage includes prior requests on resumed tasks. ")
                    + "32 MiB per file and 256 MiB total before compression. See manifest omissions.\n"
                    + "UI events before this feature was installed cannot be reconstructed. Paths and Mod names can remain; review before public sharing.\n");
                WriteObject("manifest.json", new { format = "RimWiseDiagnostic/1", createdUtc = DateTimeOffset.UtcNow,
                    maximumFileBytes = MaximumFileBytes, maximumArchiveBytes = MaximumArchiveBytes,
                    entries = entries.ToArray(), omissions = omissions.ToArray() });
            }
            cancellationToken.ThrowIfCancellationRequested();
            ScopedPaths.Ordinary(target);
            File.Move(temporary, target, overwrite: true); // Save dialog has already confirmed replacement.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(target, new FileInfo(target).Length, entries.Count, reported, reserved, unknown);
    }

    private static bool InRun(DateTimeOffset timestamp, AutomaticLogRun? run) => run is null
        || timestamp >= run.StartedUtc && timestamp <= (run.EndedUtc ?? DateTimeOffset.UtcNow);

    private static IEnumerable<string> EvidenceFiles(string root, List<object> omissions, DiagnosticRedactor redactor, AutomaticLogRun? run)
    {
        var result = new List<string>();
        void Visit(string path, int depth)
        {
            try
            {
                ScopedPaths.Ordinary(path);
                foreach (var file in Directory.EnumerateFiles(path))
                    if (IsGameEvidence(Path.GetFileName(TestDataRetention.LogicalPath(file)))) result.Add(file);
                if (depth > 0) foreach (var directory in Directory.EnumerateDirectories(path)) Visit(directory, depth - 1);
            }
            catch (Exception ex) when (ReadFailure(ex))
            { omissions.Add(new { file = redactor.Text(Path.GetRelativePath(root, path)), reason = ex.GetType().Name }); }
        }
        foreach (var group in new[] { "Data/Diagnostics", "Data/GameTests" })
        {
            try
            {
                var path = ScopedPaths.Child(root, group);
                if (!Directory.Exists(path)) continue;
                var ui = group.EndsWith("Diagnostics", StringComparison.Ordinal);
                var candidates = (ui ? Directory.EnumerateFiles(path)
                    .Where(p => (Path.GetFileName(p).StartsWith("ui-", StringComparison.Ordinal)
                        && (p.EndsWith(".jsonl") || p.EndsWith(".jsonl.gz")))
                        || (Path.GetFileName(p).StartsWith("crash-", StringComparison.Ordinal) && p.EndsWith(".json")))
                    .Select(TestDataRetention.LogicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(StoredEvidence.Resolve)
                    : Directory.EnumerateDirectories(path, "game-*"))
                    .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
                // Different filename prefixes must not push every crash behind UI logs.
                candidates = ui ? candidates.OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
                    : candidates.OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
                if (run is not null) candidates = candidates.Where(p => ui
                    ? File.GetLastWriteTimeUtc(p) >= run.StartedUtc.UtcDateTime
                    : run.Sessions.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).ToArray();
                var limit = run is not null ? candidates.Length : ui ? 20 : 10;
                if (candidates.Length > limit) omissions.Add(new { file = group, reason = "recent-session-limit", count = candidates.Length - limit });
                foreach (var candidate in candidates.Take(limit))
                    if (ui) result.Add(candidate); else Visit(candidate, 3);
            }
            catch (Exception ex) when (ReadFailure(ex)) { omissions.Add(new { file = group, reason = ex.GetType().Name }); }
        }
        return result.Select(TestDataRetention.LogicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(StoredEvidence.Resolve);
    }

    private static bool IsGameEvidence(string name) => GameFiles.Contains(name)
        || System.Text.RegularExpressions.Regex.IsMatch(name,
            @"^(external-startup-[0-9]+\.log|crash-[0-9]+-(error\.log|Player\.log|output_log\.txt|crash\.log|crash-[a-zA-Z0-9_-]+\.txt))$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool ReadFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException;
}
