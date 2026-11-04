namespace RimWise.Integration.AI;

/// <summary>Exact error-block hashes from a completed control run. No model verdict can grant an exemption.</summary>
public sealed record InsertionEvidence(bool Completed, Dictionary<string, int> Errors, string[] LogPaths)
{
    public static bool Matches(InsertionEvidence? baseline, InsertionEvidence? candidate) =>
        baseline is { Completed: true } && candidate is { Completed: true }
        && candidate.Errors.Count > 0 && candidate.Errors.All(pair =>
            baseline.Errors.TryGetValue(pair.Key, out var count) && pair.Value <= count);

    internal void Validate()
    {
        if (Errors is null || Errors.Count > 20000 || Errors.Any(p => p.Key.Length != 64
            || !p.Key.All(char.IsAsciiHexDigit) || p.Value < 1) || LogPaths is null || LogPaths.Length > 20
            || LogPaths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32768 || p.Any(char.IsControl)
                || !Path.IsPathFullyQualified(p))) throw new InvalidDataException("原排序对照证据无效。");
    }

    internal InsertionEvidence Copy() => this with { Errors = new(Errors), LogPaths = LogPaths.ToArray() };
}
