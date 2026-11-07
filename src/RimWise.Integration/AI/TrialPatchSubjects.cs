using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RimWise.Integration.AI;

/// <summary>Batch only patch owners explicitly named by an error and corroborated by local XML.</summary>
internal static partial class TrialPatchSubjects
{
    [GeneratedRegex(@"Patch operation\s+\S+\(([^\r\n]{1,2048})\)\s+failed\s+\[([^\]\r\n]{1,512})\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PatchFailure();

    internal static IReadOnlyDictionary<string, string[]> Find(MaintenanceGameReport report)
    {
        var targets = new List<(string Package, string Target)>();
        foreach (var error in report.Errors)
        {
            var match = PatchFailure().Match(error);
            if (!match.Success) continue;
            foreach (var file in report.LogContext?.Files ?? [])
            {
                if (file.Kind != "active-patch" || file.OrderIndex <= 0
                    || !file.PackageId.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var reader = XmlReader.Create(new StringReader(file.Text), new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 3072 });
                    var xml = XElement.Load(reader);
                    if (xml.DescendantsAndSelf().Any(e => e.Name.LocalName == "xpath"
                        && e.Value.Trim() == match.Groups[1].Value.Trim()))
                        targets.Add((file.PackageId, file.DefinitionType + ":" + file.DefinitionName));
                }
                catch (XmlException) { /* Truncated or malformed evidence cannot justify a batch. */ }
            }
        }
        return targets.GroupBy(t => t.Package, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Target).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }
}
