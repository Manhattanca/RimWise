using System.Text.RegularExpressions;
using RimWise.Core;
using RimWise.Integration.AI;

namespace RimWise.Integration.Game;

/// <summary>Select root errors before repeated noise, distribute space across every captured source,
/// and redact locally before anything can enter a model request or its recovery checkpoint.</summary>
internal static partial class DiagnosticLogContext
{
    [GeneratedRegex(@"https?://[^\s<>""']+|\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Addresses();
    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n<>""']*|(?<![\w<:])/(?:Users|home|tmp|var|mnt|Volumes)/[^\r\n<>""']*", RegexOptions.IgnoreCase)]
    private static partial Regex Paths();
    [GeneratedRegex(@"\b(?:api[_-]?key|token|password|secret|authorization|cookie)\b[""']?\s*[:=]\s*(?:Bearer\s+)?(?:[""'][^""'\r\n]*[""']|[^\s,;<>]+)|\bBearer\s+[^\s,;<>]+|\bsk-[A-Za-z0-9_-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex Credentials();
    [GeneratedRegex(@"ignore\s+(?:all\s+)?previous\s+instructions|system\s*prompt|忽略.{0,12}(?:指令|要求)|执行.{0,8}(?:命令|脚本)", RegexOptions.IgnoreCase)]
    private static partial Regex Instructions();
    [GeneratedRegex(@"(?:ParentName\s*=\s*[""']|<(?:defName|workerClass|thingClass|className|hediffClass|Class)>)([A-Za-z_][A-Za-z0-9_.+`-]{0,159})")]
    private static partial Regex XmlSymbols();
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_`]*(?:\.[A-Za-z_][A-Za-z0-9_`+]*)+)\b")]
    private static partial Regex CodeSymbols();

    [GeneratedRegex(@"^\s*at\s+([A-Za-z_][A-Za-z0-9_`+]*(?:\.[A-Za-z_][A-Za-z0-9_`+]*)+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex StackMethods();

    [GeneratedRegex(@"(?:\bClass\s*=\s*[""']|(?:type(?: initializer for)?|Could not find type named)\s+[""'])([A-Za-z_][A-Za-z0-9_.+`-]{0,159})", RegexOptions.IgnoreCase)]
    private static partial Regex ConcreteTypes();

    [GeneratedRegex(@"[A-Za-z]:[\\/][^\r\n""<>]*?\.dll(?=\s|$|\))", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyPaths();

    internal static string[] SubjectSymbols(IEnumerable<string> summaries) => summaries.SelectMany(text =>
        ConcreteTypes().Matches(text).Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal).Take(64).ToArray();

    internal static AiLogContext Create(OrdinaryGameReport report, ModCatalogue catalogue,
        IReadOnlyList<string> order, CancellationToken token, GameVersion? version = null)
    {
        var excerpts = new List<AiLogExcerpt>();
        var limited = report.Logs.Any(l => l.Truncated || l.ContextAbbreviated);
        var active = order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var patchBudget = 8;
        // At most 16 KiB per source; one noisy runtime log cannot evict the crash or helper log.
        foreach (var source in new[] { "game-errors", "startup", "crash" })
        {
            var availableLogs = report.Logs.Where(l => l.Kind == source).ToArray();
            var sourceLogs = availableLogs.Take(16).ToArray();
            limited |= availableLogs.Length > sourceLogs.Length;
            limited |= sourceLogs.Any(l => l.Text.Length > 2048);
            var used = 0;
            var queues = sourceLogs.Select(log => new Queue<GameLogIssue?>(Candidates(log))).ToArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Round-robin prevents the first report from taking every slot. Keep the first error
            // and the last recorded stage/stack even when the rest is repeated warning noise.
            while (queues.Any(q => q.Count > 0))
            {
                token.ThrowIfCancellationRequested();
                for (var index = 0; index < queues.Length; index++)
                {
                    var queue = queues[index];
                    if (queue.Count == 0) continue;
                    if (used >= 16384 || excerpts.Count(e => e.Source == source) >= 16) { limited = true; break; }
                    var issue = queue.Dequeue();
                    var block = issue is null ? HeadAndTail(sourceLogs[index].Text, 2048)
                        : sourceLogs[index].Issues.Contains(issue)
                            ? DiagnosticPatchEvidence.Expand(issue, catalogue, active, ref patchBudget) : issue.Detail;
                    // Reserve a fair share for each file, including reports with large first errors.
                    var budget = Math.Min(Math.Min(4096, 16384 / queues.Length), 16384 - used);
                    var safe = Sanitize(HeadAndTail(block, 8192));
                    if (safe.Length == 0 || !seen.Add(safe)) continue;
                    if (safe.Length > budget) { safe = HeadAndTail(safe, budget); limited = true; }
                    excerpts.Add(new(source, safe)); used += safe.Length;
                    if (block.Length > 8192) limited = true;
                }
                if (limited && (used >= 16384 || excerpts.Count(e => e.Source == source) >= 16)) break;
            }
        }
        var text = string.Join('\n', excerpts.Select(e => e.Text));
        var summaries = string.Join('\n', report.Logs.Take(48)
            .SelectMany(log => log.Issues.Take(6).Concat(log.Issues.TakeLast(4)))
            .Select(issue => HeadAndTail(Sanitize(issue.Summary), 1024)));
        // Error subjects take priority over common framework frames in very long stacks.
        var symbols = BlockingSymbols(report.Logs)
            .Concat(SubjectSymbols(report.Logs.Take(48).SelectMany(log => log.Issues.Take(64)).Select(issue => issue.Summary)))
            .Concat(report.Logs.Take(48).SelectMany(log => log.Issues.Take(64))
                .SelectMany(issue => CodeSymbols().Matches(Sanitize(issue.Detail)).Select(m => m.Groups[1].Value)
                    .Where(symbol => !IsFrameworkSymbol(symbol)).Distinct(StringComparer.Ordinal).Take(8)))
            .Concat(ConcreteTypes().Matches(summaries).Select(m => m.Groups[1].Value)).Concat(XmlSymbols().Matches(summaries).Select(m => m.Groups[1].Value)).Concat(CodeSymbols().Matches(summaries).Select(m => m.Groups[1].Value))
            .Concat(XmlSymbols().Matches(text).Select(m => m.Groups[1].Value)).Concat(CodeSymbols().Matches(text).Select(m => m.Groups[1].Value))
            .Where(symbol => !IsFrameworkSymbol(symbol)).Distinct(StringComparer.Ordinal).Take(96).ToArray();
        var files = DiagnosticDefinitionEvidence.Collect(report, catalogue, order, version, token, out var fileLimited);
        var issues = report.Logs.SelectMany(l => l.Issues).ToArray();
        // Exact file plus target evidence is more specific than a broad all-versions symbol scan.
        // Preserve the fallback for failures without a captured XML file (including CLR exceptions).
        var exactPatchErrors = BlockingSymbols(report.Logs).Length == 0 && files.Count > 0 && issues.Length > 0
            && issues.All(i => DiagnosticPatchEvidence.ReferencedFiles(i.Detail).Any());
        var lookupLimited = false;
        var owners = exactPatchErrors ? [] : DiagnosticSymbolOwners.Find(catalogue, order, symbols, token, out lookupLimited,
            report.Logs.Take(48).SelectMany(log => AssemblyPaths().Matches(log.Text).Select(m => m.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(96).ToArray());
        return new AiLogContext(excerpts, owners, limited || lookupLimited || fileLimited) { Files = files }.CopyValidated();
    }

    private static IEnumerable<GameLogIssue?> Candidates(GameLogSnapshot log)
    {
        // Severity wins over position. Early repeated WARN stacks must never consume the quota
        // before concrete ERROR entries near the end of a loading log.
        int Offset(GameLogIssue issue) => PlayerLogReader.OccurrenceOffset(log.Text, issue);
        var errors = log.Issues.OrderBy(Offset).ToArray();
        foreach (var issue in errors.Take(2)) yield return issue;
        // Native reports and the last loading stage need not have a recognized severity header.
        if (!string.IsNullOrWhiteSpace(log.Text)) yield return null;
        foreach (var issue in errors.Skip(2)) yield return issue;
        foreach (var issue in log.Warnings.OrderBy(Offset)) yield return issue;
    }

    // The helper records the exception which stopped the current journey, including nested mod
    // exceptions. Select it before startup noise, but verify ownership locally before attributing it.
    internal static string[] BlockingSymbols(IEnumerable<GameLogSnapshot> logs) => logs
        .Where(GameTestDiagnostics.IsHelperDiagnostic)
        // Only actual exception frames qualify. Harmony's appended patch inventory lists
        // interceptors that may never have run, so it must not create blocking candidates.
        .SelectMany(log => StackMethods().Matches(Sanitize(log.Text)).Select(m => m.Groups[1].Value))
        .Where(symbol => !IsFrameworkSymbol(symbol)).Distinct(StringComparer.Ordinal).Take(32).ToArray();

    internal static bool IsFrameworkSymbol(string symbol) => new[] {
        "System.", "UnityEngine.", "Verse.", "RimWorld.", "HarmonyLib.", "MonoMod.", "RimWise.TestHelper."
    }.Any(prefix => symbol.StartsWith(prefix, StringComparison.Ordinal));

    private static string HeadAndTail(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        const string omitted = "\n[context omitted]\n";
        if (maximum <= omitted.Length) return text[..maximum];
        var head = (maximum - omitted.Length) * 2 / 3;
        var tail = maximum - omitted.Length - head;
        return text[..head] + omitted + text[^tail..];
    }

    internal static string Sanitize(string text) => string.Join('\n', text.Replace("\r", "").Split('\n')
        .Where(line => !Instructions().IsMatch(line))
        .Select(line => Paths().Replace(Addresses().Replace(Credentials().Replace(line, "[credential]"), "[address]"), "[path]")))
        .Trim();
}
