using System.Text;
using System.Text.RegularExpressions;
using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

public sealed record GameLogIssue(string Summary, int Count, string[] RelatedMods, string Detail);
public sealed record GameLogSnapshot(string Path, DateTimeOffset LastWriteUtc, string Text,
    IReadOnlyList<GameLogIssue> Issues, bool Truncated)
{
    // Only the UI/AI context is abbreviated; Issues and Warnings scan the entire file.
    public bool ContextAbbreviated { get; init; }
    public string? ModResetError { get; init; }
    public IReadOnlyList<GameLogIssue> Warnings { get; init; } = [];
    public IReadOnlyList<GameLogIssue> ExcludedErrors { get; init; } = [];
    public string Kind { get; init; } = "startup";
}

public static partial class PlayerLogReader
{
    // Unlike display grouping, this preserves every distinct full block, including numeric IDs and stacks.
    // Any abbreviated block makes comparison unavailable; raw logs are still retained normally.
    internal static Dictionary<string, int>? ExactErrorCounts(string path, Func<string, string>? normalize = null)
    {
        using var stream = new FileStream(ScopedPaths.Ordinary(path, true), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        IEnumerable<string> Lines() { while (reader.ReadLine() is { } line) yield return line; }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in Blocks(Lines(), false))
        {
            if (block.Length >= 32767) return null;
            var end = block.IndexOf('\n');
            var entry = TimestampedEntry().Match(end < 0 ? block : block[..end]);
            if (entry.Success && entry.Groups["level"].Value == "ERROR"
                && KnownLogExclusions.Matches(entry.Groups["message"].Value, block)) continue;
            var text = entry.Success ? entry.Groups["level"].Value + ":" + entry.Groups["message"].Value
                + (end < 0 ? "" : block[end..]) : block;
            if (normalize is not null) text = normalize(text);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            counts[hash] = checked(counts.GetValueOrDefault(hash) + 1);
            if (counts.Count > 20000) return null;
        }
        return counts;
    }
    // Legacy name: bounds the in-memory context excerpt, never the captured file or scan.
    public const int MaximumBytes = 16 * 1024 * 1024;
    [GeneratedRegex(@"0x[0-9a-fA-F]+|\b\d{3,}\b")]
    private static partial Regex VolatileNumbers();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+\s+(?<level>TRACE|DEBUG|INFO|WARN|ERROR|FATAL)\s+(?<message>.*)$")]
    private static partial Regex TimestampedEntry();
    [GeneratedRegex(@"\b(?:exception|error)\b|\bCould not\b|\bFailed to\b", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyError();
    [GeneratedRegex(@"^(?:Unhandled exception[.:]?\s*)?(?:[A-Za-z_][A-Za-z0-9_+\x60]*\.)*[A-Za-z_][A-Za-z0-9_+\x60]*Exception\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionHeader();
    public static GameLogSnapshot Read(string path, ModCatalogue? catalogue = null)
    {
        path = ScopedPaths.Ordinary(path, true);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var head = new StringBuilder();
        var tail = new Queue<string>();
        var tailLength = 0;
        long characters = 0;
        var reset = new GameModResetDetection.Evidence();
        const int half = MaximumBytes / 2;
        IEnumerable<string> Lines(bool collectText = false)
        {
            stream.Position = 0;
            using var snapshot = new LogSnapshotStream(stream, length);
            using var reader = new StreamReader(snapshot, Encoding.UTF8, true);
            while (reader.ReadLine() is { } line)
            {
                if (collectText)
                {
                    reset.Observe(line);
                    var value = line + "\n";
                    characters += value.Length;
                    var prefix = Math.Min(value.Length, half - head.Length);
                    if (prefix > 0) head.Append(value.AsSpan(0, prefix));
                    value = value[prefix..];
                    if (value.Length > 0)
                    {
                        if (value.Length > half) value = value[^half..];
                        tail.Enqueue(value); tailLength += value.Length;
                        while (tail.Count > 1 && tailLength > half) tailLength -= tail.Dequeue().Length;
                    }
                }
                yield return line;
            }
        }
        var issues = ParseSeverity(Lines(true), catalogue, false);
        var warnings = ParseSeverity(Lines(), catalogue, true);
        var excluded = ParseSeverity(Lines(), catalogue, false, true);
        var abbreviated = characters > head.Length + tailLength;
        var text = head + (abbreviated ? "\n[Context abbreviated; full log retained on disk.]\n" : "") + string.Concat(tail);
        return new(path, File.GetLastWriteTimeUtc(path), text, issues, false)
            { Warnings = warnings, ExcludedErrors = excluded, ContextAbbreviated = abbreviated, ModResetError = reset.Text };
    }
    public static IReadOnlyList<GameLogIssue> Parse(string text, ModCatalogue? catalogue = null)
        => ParseSeverity(text, catalogue, false);
    public static IReadOnlyList<GameLogIssue> ParseWarnings(string text, ModCatalogue? catalogue = null)
        => ParseSeverity(text, catalogue, true);
    internal static IReadOnlyList<GameLogIssue> ParseExcludedErrors(string text, ModCatalogue? catalogue = null)
        => ParseSeverity(text, catalogue, false, excludedOnly: true);

    // Details normalize newlines, but the captured text may still use CRLF. Match the header
    // so chronological selection does not silently fall back to the frequency-sorted list.
    internal static int OccurrenceOffset(string text, GameLogIssue issue)
    {
        var lineEnd = issue.Detail.IndexOf('\n');
        var header = lineEnd < 0 ? issue.Detail : issue.Detail[..lineEnd];
        return Math.Max(0, text.IndexOf(header.TrimEnd('\r'), StringComparison.Ordinal));
    }

    private static IReadOnlyList<GameLogIssue> ParseSeverity(string text, ModCatalogue? catalogue, bool warnings, bool excludedOnly = false)
        => ParseSeverity(TextLines(text), catalogue, warnings, excludedOnly);

    private static IEnumerable<string> TextLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static IEnumerable<string> Blocks(IEnumerable<string> lines, bool warnings)
    {
        StringBuilder? current = null;
        var structuredBlock = false;
        foreach (var line in lines)
        {
            var entry = TimestampedEntry().Match(line);
            // Severity is authoritative in structured logs. An INFO timing named ErrorCheckPatches
            // or a WARN translation summary is not a game error. Stack frames belong to their header.
            if (!entry.Success && !(!warnings && ExceptionHeader().IsMatch(line)) && current is not null && (structuredBlock || line.StartsWith(' ')
                || line.StartsWith('<') || line.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("--->", StringComparison.Ordinal) || line.StartsWith("Caused by:", StringComparison.Ordinal)
                || line.StartsWith("at ", StringComparison.Ordinal) || line.StartsWith("[Ref ", StringComparison.Ordinal)))
            {
                if (current.Length < 32767) current.Append('\n').Append(line.AsSpan(0, Math.Min(line.Length, 32767 - current.Length)));
                continue;
            }
            var error = entry.Success ? warnings ? entry.Groups["level"].Value == "WARN" : entry.Groups["level"].Value is "ERROR" or "FATAL"
                : !line.StartsWith(' ') && (warnings ? line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) : LegacyError().IsMatch(line) || ExceptionHeader().IsMatch(line));
            if (error)
            {
                if (current is not null) yield return current.ToString();
                current = new StringBuilder(line);
                structuredBlock = entry.Success;
            }
            else if (current is not null) { yield return current.ToString(); current = null; }
        }
        if (current is not null) yield return current.ToString();
    }

    private static IReadOnlyList<GameLogIssue> ParseSeverity(IEnumerable<string> lines, ModCatalogue? catalogue, bool warnings, bool excludedOnly = false)
    {
        var groups = new Dictionary<string, (string Detail, int Count)>(StringComparer.Ordinal);
        foreach (var block in Blocks(lines, warnings))
        {
            var headerEnd = block.IndexOf('\n');
            var header = headerEnd < 0 ? block : block[..headerEnd];
            var entry = TimestampedEntry().Match(header);
            if (!warnings)
            {
                // Filter each occurrence before grouping: an unrelated error with an identical
                // header must remain visible even when a separate flavor message was approved.
                var excluded = entry.Success && entry.Groups["level"].Value == "ERROR"
                    && KnownLogExclusions.Matches(entry.Groups["message"].Value, block);
                if (excluded != excludedOnly) continue;
            }
            var key = VolatileNumbers().Replace(entry.Success ? entry.Groups["message"].Value : header, "#");
            groups[key] = groups.TryGetValue(key, out var previous)
                ? (previous.Detail, previous.Count == int.MaxValue ? int.MaxValue : previous.Count + 1) : (block, 1);
        }
        return groups.Select(group =>
            {
                var detail = group.Value.Detail;
                var files = DiagnosticPatchEvidence.ReferencedFiles(detail).ToArray();
                var folderOwners = EmptyContentOwners(detail, catalogue).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var related = (catalogue?.Installations ?? []).Where(mod => mod.PackageId.Length > 3
                    && (detail.Contains(mod.PackageId, StringComparison.OrdinalIgnoreCase)
                        || folderOwners.Contains(mod.PackageId)
                        || files.Any(path => DiagnosticPatchEvidence.IsWithin(path, mod.Directory))))
                    .Select(mod => mod.PackageId).Distinct().ToArray();
                return new GameLogIssue(group.Key, group.Value.Count, related, detail);
            }).OrderByDescending(issue => issue.Count).Take(2000).ToArray();
    }

    // This game diagnostic explicitly lists load folders. Treat exact, absolute folder
    // lines as attribution hints only; never read a path supplied by the log.
    private static IEnumerable<string> EmptyContentFolders(string detail)
    {
        if (!detail.Contains("did not load any content. Following load folders were used:", StringComparison.Ordinal))
            yield break;
        foreach (var line in detail.Split('\n').Skip(1))
        {
            var path = line.Trim();
            if (path.StartsWith("- ", StringComparison.Ordinal)) path = path[2..].TrimStart();
            if (path.Length == 0) continue;
            if (!Path.IsPathFullyQualified(path)) yield break;
            yield return path;
        }
    }

    private static IEnumerable<string> EmptyContentOwners(string detail, ModCatalogue? catalogue)
    {
        foreach (var folder in EmptyContentFolders(detail))
        {
            ModInstallation[] owners;
            try
            {
                var normalized = ScopedPaths.Normalize(folder).TrimEnd(Path.DirectorySeparatorChar);
                owners = (catalogue?.Installations ?? []).Where(mod =>
                    normalized.Equals(ScopedPaths.Normalize(mod.Directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    || DiagnosticPatchEvidence.IsWithin(normalized, mod.Directory)).ToArray();
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { continue; }
            if (owners.Length == 1) yield return owners[0].PackageId;
        }
    }
}
