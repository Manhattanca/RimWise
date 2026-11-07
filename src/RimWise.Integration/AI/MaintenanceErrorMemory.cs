using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RimWise.Integration.Game;

namespace RimWise.Integration.AI;

/// <summary>Task-local identities of fully captured errors, never a certificate that they are fixed.</summary>
internal static partial class MaintenanceErrorMemory
{
    [GeneratedRegex(@"\[Ref\s+[^\]]+\]|0x[0-9a-f]+|\b\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(?:\.\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex VolatileValues();

    internal static string[] Capture(OrdinaryGameReport report)
    {
        // A completed journey and complete issue blocks are required. Unknown or abbreviated
        // evidence must receive a fresh diagnosis rather than matching only its short title.
        if (!report.MapSuccessVerified || !report.CaptureCompleted || report.Logs.Any(l => l.Truncated)) return [];
        var captured = report.Logs.Where(l => l.Kind == "game-errors")
            .Select(l => l.Issues.Sum(i => (long)i.Count) + l.ExcludedErrors.Sum(i => (long)i.Count)).DefaultIfEmpty(0).Max();
        if (report.ProbeErrors > captured) return [];
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var log in report.Logs.Where(l => !GameTestDiagnostics.IsHelperDiagnostic(l)))
        {
            Dictionary<string, int>? blocks;
            try { blocks = PlayerLogReader.ExactErrorCounts(log.Path, text => VolatileValues().Replace(text.Replace("\r", ""), "")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
            if (blocks is null) return [];
            // Display grouping may combine different stacks under the same title. Scan every
            // original block, just as insertion comparison does, before reusing a diagnosis.
            foreach (var hash in blocks.Keys)
                signatures.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(log.Kind + ":" + hash))));
            if (signatures.Count > 100) return [];
        }
        return signatures.Order(StringComparer.Ordinal).ToArray();
    }

    internal static bool Covered(MaintenanceGameReport report, IReadOnlyList<string> diagnosed) =>
        report.StartupVerified && report.CanDiagnose && MaintenanceFailureSignature.Blocker(report) is null
        && report.ErrorSignatures.Length > 0 && report.ErrorSignatures.All(s => diagnosed.Contains(s, StringComparer.Ordinal));

    internal static bool Valid(string[]? values) => values is { Length: <= 20000 }
        && values.All(MaintenanceFailureSignature.Valid)
        && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
}
