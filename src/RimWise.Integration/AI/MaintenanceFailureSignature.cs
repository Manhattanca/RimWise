using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RimWise.Integration.Game;

namespace RimWise.Integration.AI;

/// <summary>Compare the failed operation, not the changing order or incidental startup noise.</summary>
internal static partial class MaintenanceFailureSignature
{
    [GeneratedRegex(@"\[Ref\s+[^\]]+\]|0x[0-9a-f]+|\b\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(?:\.\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex VolatileValues();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    internal static string? Blocker(MaintenanceGameReport report)
    {
        if (!string.IsNullOrWhiteSpace(report.LogContext?.BlockingError)) return report.LogContext.BlockingError;
        // Older checkpoints have no dedicated field. Only accept an explicit helper stack.
        if (report.StartupFailure?.Context.Evidence.Any(e => e.Code == "TestHelperFailure") != true) return null;
        return report.LogContext?.Excerpts.FirstOrDefault(e => e.Source == "startup"
            && e.Text.Contains("RimWise.TestHelper.", StringComparison.Ordinal))?.Text;
    }

    internal static string? Create(MaintenanceGameReport report)
    {
        if (report.FailureFingerprint is not null) return report.FailureFingerprint;
        if (!report.CanDiagnose || report.Errors.Count == 0) return null;
        string Normalize(string text) => Whitespace().Replace(VolatileValues().Replace(
            DiagnosticLogContext.Sanitize(text), ""), " ").Trim();
        var blocker = Blocker(report);
        IEnumerable<string> items;
        if (blocker is not null)
        {
            var lines = blocker.Split('\n').Select(line => line.Trim()).ToArray();
            // Reflection/JIT wrappers vary across launches without changing the failed mod
            // operation. Prefer verified-shape third-party frames, not framework plumbing.
            var frames = DiagnosticLogContext.ExceptionSymbols(blocker);
            items = lines.Where(line => line.Contains("Exception", StringComparison.Ordinal))
                .Concat(frames.Length > 0 ? frames : lines.Where(line => line.StartsWith("at ", StringComparison.Ordinal))
                    .Take(1).Select(line => line.IndexOf('(') is var index && index > 0 ? line[..index] : line));
        }
        else items = report.Errors.Concat(report.DiagnosticFacts.Select(f => f.Code + ":" + f.PackageId.ToLowerInvariant()));
        var normalized = items.Select(Normalize).Where(s => s.Length > 0).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0) return null;
        var phase = blocker is not null ? "journey-blocker" : string.Join(',', report.StartupFailure?.Context.Evidence.Where(e => e.Code.EndsWith("Failure", StringComparison.Ordinal))
            .Select(e => e.Code).Distinct().Order(StringComparer.Ordinal).ToArray() ?? []);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phase + "\n" + string.Join('\n', normalized))));
    }

    internal static bool Valid(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
