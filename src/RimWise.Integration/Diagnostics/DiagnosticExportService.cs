using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Diagnostics;

public sealed record DiagnosticExportResult(string Path, long Bytes, int Files, long ReportedTokens,
    long ReservedTokens, int UnknownTasks)
{
    public int OmissionCount { get; init; }
    public int RecoveredRecords { get; init; }
    public string RecoveryNotice => RecoveredRecords == 0 ? "" : $"已保留并脱敏恢复 {RecoveredRecords} 项残缺日志记录，详见诊断包恢复说明。";
    public string IncompleteNotice => OmissionCount == 0 ? "" : $"有 {OmissionCount} 项内容未完整导出，诊断包中已保留遗漏说明。";
}

/// <summary>Positive allowlist: credentials, raw settings, saves, caches and recovery envelopes never enter the ZIP.</summary>
public static class DiagnosticExportService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new DiagnosticTokenUsageConverter() }
    };
    private static readonly HashSet<string> GameFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "runtime-Player.log", "Player.log", "output_log.txt", "stdout.log", "stderr.log", "launch.json", "launch-failure.log", "result.json",
        "session.json", "process.json", "finished.json", "recovered.json",
        "diagnostic-sources.json", "diagnostic-context.json", "ModsConfig.xml",
        "intended-ModsConfig.xml", "observed-ModsConfig.xml",
        "workbench-helper-failure.txt", "workbench-progress.xml", "workbench-signal.xml", "workbench-probe.xml",
        "workbench-messages.log", "workbench-factions.txt", "workbench-startup.xml", "workbench-log-locations.xml", "workbench-loaded-order.xml"
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
        var redactor = DiagnosticRedactor.ForExport(new LocalEncryptedSecretStore(root).ReadDiagnosticSecrets(settings.SecretName), cancellationToken);
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
        var exclusions = new List<object>();
        var recoveries = new List<object>();
        var recoveredRecords = 0;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void WriteEntry(string name, Action<Stream> write)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var safeName = redactor.Text(name);
                    var entry = archive.CreateEntry(safeName, CompressionLevel.Fastest);
                    using var output = entry.Open();
                    using var counted = new DiagnosticEvidenceWriter.CountingStream(output);
                    write(counted);
                    entries.Add(new { file = safeName, uncompressedBytes = counted.Written });
                }
                void Write(string name, string sanitized) => WriteEntry(name, output =>
                {
                    using var writer = new StreamWriter(output, new UTF8Encoding(false), 65536, leaveOpen: true);
                    writer.Write(sanitized);
                });
                void WriteObject(string name, object value) => Write(name, redactor.Json(JsonSerializer.Serialize(value, Json)));
                try { WriteObject("test-storage.json", TestDataRetention.InspectStorage(root)); }
                catch (Exception ex) when (ReadFailure(ex))
                {
                    // This is a derived disk-usage summary, not source evidence. A denied
                    // cache/junction must not prevent exporting the safe diagnostic files.
                    WriteObject("test-storage.json", new { unavailable = true, reason = ex.GetType().Name,
                        detail = redactor.Text(ex.Message) });
                    exclusions.Add(new { file = "test-storage.json", reason = "storage-summary-unavailable" });
                }
                // Keep a compact summary alongside the complete request history.
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
                WriteObject("ai-audit.json", new AiAuditStore(root).Load().Where(a => InRun(a.Timestamp, run)).ToArray());

                foreach (var file in EvidenceFiles(root, exclusions, redactor, run))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var logical = TestDataRetention.LogicalPath(file);
                    var relative = Path.GetRelativePath(root, logical).Replace('\\', '/');
                    try
                    {
                        ScopedPaths.Ordinary(file, true);
                        WriteEntry(relative, output => DiagnosticEvidenceWriter.Write(file, logical, output, redactor,
                            cancellationToken, (line, reason) =>
                            {
                                recoveredRecords++;
                                recoveries.Add(new { file = redactor.Text(relative), line, reason });
                            }, run is null ? null : timestamp => InRun(timestamp, run)));
                    }
                    catch (Exception ex) when (ReadFailure(ex))
                    {
                        // Only the temporary ZIP has been written. Never claim success
                        // after discarding key evidence or overwrite the previous package.
                        throw new IOException("诊断文件无法安全导出：" + redactor.Text(relative)
                            + "。请检查文件是否可读或正在被清理后重试；原日志及已有诊断包未覆盖。错误类型：" + ex.GetType().Name, ex);
                    }
                }
                Write("README.txt", "RimWise diagnostic export\nCompact English field names; original game errors and AI advice are preserved.\n"
                    + "Includes current order, latest validated task checkpoints, AI action audits, recent UI events and game test evidence.\n"
                    + "Token totals count each task once, including deleted-task receipts. Request records contain input, output, cache and reasoning usage when reported.\n"
                    + "Unknown usage remains an estimate. Other applications and provider wallet totals are not counted.\n"
                    + "No credential files, raw AI settings, raw HTTP traffic, save games or Mod binaries are exported. Known keys and credential fields are redacted.\n"
                    + (run is null ? "All retained UI logs and game sessions. " : "Selected run sessions and UI time interval; task usage includes prior requests on resumed tasks. ")
                    + "No per-file or archive-size truncation. Files are snapshots at open time. Text/JSONL are processed record by record; one JSON document or long record still requires memory proportional to that record.\n"
                    + "Malformed JSON is preserved as labelled sanitized records. See manifest recoveries and deliberate security exclusions. Unreadable evidence fails the export instead of being silently skipped.\n"
                    + "UI events before this feature was installed cannot be reconstructed. Paths and Mod names can remain; review before public sharing.\n");
                WriteObject("manifest.json", new { format = "RimWiseDiagnostic/1", createdUtc = DateTimeOffset.UtcNow,
                    maximumFileBytes = (long?)null, maximumArchiveBytes = (long?)null,
                    entries = entries.ToArray(), omissions = Array.Empty<object>(), recoveries = recoveries.ToArray(), exclusions = exclusions.ToArray() });
            }
            cancellationToken.ThrowIfCancellationRequested();
            ScopedPaths.Ordinary(target);
            File.Move(temporary, target, overwrite: true); // Save dialog has already confirmed replacement.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(target, new FileInfo(target).Length, entries.Count, reported, reserved, unknown) { RecoveredRecords = recoveredRecords };
    }

    private static bool InRun(DateTimeOffset timestamp, AutomaticLogRun? run) => run is null
        || timestamp >= run.StartedUtc && timestamp <= (run.EndedUtc ?? DateTimeOffset.UtcNow);

    private static IEnumerable<string> EvidenceFiles(string root, List<object> exclusions, DiagnosticRedactor redactor, AutomaticLogRun? run)
    {
        var result = new List<string>();
        bool Ordinary(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                exclusions.Add(new { file = redactor.Text(Path.GetRelativePath(root, path)), reason = "security-boundary-link" });
                return false;
            }
            ScopedPaths.Ordinary(path, true);
            return true;
        }
        void Visit(string path, int depth)
        {
            if (!Ordinary(path)) return;
            foreach (var file in Directory.EnumerateFiles(path))
                if (IsGameEvidence(Path.GetFileName(TestDataRetention.LogicalPath(file))) && Ordinary(file)) result.Add(file);
            if (depth > 0) foreach (var directory in Directory.EnumerateDirectories(path)) Visit(directory, depth - 1);
        }
        foreach (var group in new[] { "Data/Diagnostics", "Data/GameTests" })
        {
            var path = Path.Combine(root, group);
            if (!Directory.Exists(path) || !Ordinary(path)) continue;
            var ui = group.EndsWith("Diagnostics", StringComparison.Ordinal);
            var candidates = (ui ? Directory.EnumerateFiles(path)
                .Where(p => (Path.GetFileName(p).StartsWith("ui-", StringComparison.Ordinal)
                    && (p.EndsWith(".jsonl") || p.EndsWith(".jsonl.gz")))
                    || (Path.GetFileName(p).StartsWith("crash-", StringComparison.Ordinal) && p.EndsWith(".json")))
                .Where(Ordinary).Select(TestDataRetention.LogicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(StoredEvidence.Resolve)
                : Directory.EnumerateDirectories(path, "game-*").Where(Ordinary))
                .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
            // Different filename prefixes must not push every crash behind UI logs.
            candidates = ui ? candidates.OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
                : candidates.OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
            if (run is not null) candidates = candidates.Where(p => ui
                ? File.GetLastWriteTimeUtc(p) >= run.StartedUtc.UtcDateTime
                : run.Sessions.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (var candidate in candidates)
                if (ui) result.Add(candidate); else Visit(candidate, 3);
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
