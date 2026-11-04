using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RimWise.Integration.Game;

/// <summary>Technical identifiers observed in errors, never model supplied paths or per-mod rules.</summary>
internal sealed record DiagnosticXmlTarget(string Type, string Name, bool IsParent = false);

internal static partial class DiagnosticXmlTargets
{
    internal static bool Identifier(string value) => value.Length is > 0 and <= 160
        && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '`' or '-');
    [GeneratedRegex(@"(?<![A-Za-z0-9_.])Defs/([A-Za-z_][A-Za-z0-9_.]*)\s*\[\s*defName\s*=\s*['""']([A-Za-z_][A-Za-z0-9_.-]{0,159})['""']\s*\]")]
    private static partial Regex TargetPaths();
    [GeneratedRegex(@"(?<![A-Za-z0-9_.])Defs/([A-Za-z_][A-Za-z0-9_.]*)\s*\[\s*@Name\s*=\s*['""']([A-Za-z_][A-Za-z0-9_.-]{0,159})['""']\s*\]")]
    private static partial Regex ParentPaths();
    [GeneratedRegex(@"Could not resolve cross-reference(?: to|:\s*No) ([A-Za-z_][A-Za-z0-9_.]{0,159}) named ['""']?([A-Za-z_][A-Za-z0-9_.-]{0,159})(?=['""'\s,(]|$)")]
    private static partial Regex MissingReferences();
    [GeneratedRegex(@"Could not find parent (?:node )?named ['""']([A-Za-z_][A-Za-z0-9_.-]{0,159})['""'] for node ['""']([A-Za-z_][A-Za-z0-9_.]{0,159})['""']")]
    private static partial Regex MissingParents();
    [GeneratedRegex(@"<([A-Za-z_][A-Za-z0-9_.]{0,159})\s+[^<>\r\n]{0,1024}?\bParentName\s*=\s*['""']([A-Za-z_][A-Za-z0-9_.-]{0,159})['""']")]
    private static partial Regex ParentAttributes();

    internal static IEnumerable<DiagnosticXmlTarget> FromError(string detail)
    {
        foreach (Match match in TargetPaths().Matches(detail))
            yield return new(match.Groups[1].Value, match.Groups[2].Value);
        foreach (Match match in ParentPaths().Matches(detail))
            yield return new(match.Groups[1].Value, match.Groups[2].Value, true);
        foreach (Match match in MissingReferences().Matches(detail))
        {
            var type = match.Groups[1].Value;
            // Built-in Def type names in logs are qualified; their XML nodes are not.
            if (type.StartsWith("Verse.", StringComparison.Ordinal) || type.StartsWith("RimWorld.", StringComparison.Ordinal))
                type = type[(type.LastIndexOf('.') + 1)..];
            yield return new(type, match.Groups[2].Value);
        }
        foreach (Match match in MissingParents().Matches(detail))
            yield return new(match.Groups[2].Value, match.Groups[1].Value, true);
        foreach (Match match in ParentAttributes().Matches(detail))
            yield return new(match.Groups[1].Value, match.Groups[2].Value, true);
    }

    internal static IEnumerable<DiagnosticXmlTarget> FromOperation(XElement operation) => operation.DescendantsAndSelf()
        .Where(n => n.Name.LocalName == "xpath").SelectMany(n => TargetPaths().Matches(n.Value.Trim())
            .Select(m => new DiagnosticXmlTarget(m.Groups[1].Value, m.Groups[2].Value)).Concat(ParentPaths().Matches(n.Value.Trim())
                .Select(m => new DiagnosticXmlTarget(m.Groups[1].Value, m.Groups[2].Value, true))));
}
