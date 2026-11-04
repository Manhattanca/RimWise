namespace RimWise.Core;

/// <summary>One visible issue per observation, while retaining every affected row and source path.</summary>
public static class DiagnosticSummary
{
    public static IReadOnlyList<CatalogueDiagnostic> Consolidate(IEnumerable<CatalogueDiagnostic> issues) => issues
        .GroupBy(issue => (issue.Code, issue.Severity, issue.Message,
            // Only these diagnostics intentionally emit one annotation per endpoint.
            // Identical parser/version messages on unrelated mods remain distinct issues.
            Target: issue.Code is "CustomRuleViolated" or "DeclaredConflict" or "DuplicateActiveInstallation"
                ? "" : issue.Identifier.ToUpperInvariant()))
        .Select(group =>
        {
            var first = group.First();
            var identifiers = group.SelectMany(issue => issue.AffectedIdentifiers)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return first with
            {
                Identifier = identifiers.FirstOrDefault() ?? "",
                RelatedIdentifiers = identifiers.Skip(1).ToArray(),
                Location = string.Join("\n", group.Select(issue => issue.Location).Where(path => path.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            };
        }).ToArray();
}
