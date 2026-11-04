using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Only the exact XML file named by a captured error in an active installation is inspected.
/// File ownership is an observation, never proof of causality or permission to change the mod.</summary>
internal static partial class DiagnosticPatchEvidence
{
    [GeneratedRegex(@"^[ \t]*file:[ \t]*([^\r\n]+?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FileLines();

    internal static IEnumerable<string> ReferencedFiles(string detail) => FileLines().Matches(detail)
        .Select(m => m.Groups[1].Value.Trim().Trim('"')).Take(8);

    internal static bool IsWithin(string file, string directory)
    {
        if (!Path.IsPathFullyQualified(file)) return false;
        try
        {
            var path = ScopedPaths.Normalize(file);
            var root = ScopedPaths.Normalize(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return false; }
    }

    internal static string Expand(GameLogIssue issue, ModCatalogue catalogue, IReadOnlySet<string> active, ref int remaining)
    {
        var result = issue.Detail;
        foreach (var file in ReferencedFiles(issue.Detail))
        {
            if (remaining <= 0) break;
            var owners = catalogue.Installations.Where(m => active.Contains(m.PackageId) && IsWithin(file, m.Directory)).ToArray();
            if (owners.Length != 1 || !Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            --remaining;
            try
            {
                using var stream = new FileStream(ScopedPaths.Ordinary(file, true), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 64 * 1024) continue;
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 64 * 1024 });
                var document = XDocument.Load(reader);
                // Select the matching operation, not unrelated siblings or the whole installation.
                var operations = document.Root?.Elements().ToArray() ?? [];
                var operation = operations.FirstOrDefault(node => node.DescendantsAndSelf()
                    .Any(n => n.Name.LocalName == "xpath" && n.Value.Length > 0 && issue.Detail.Contains(n.Value, StringComparison.Ordinal)))
                    ?? (operations.Length == 1 ? operations[0] : null);
                var xml = operation?.ToString(SaveOptions.DisableFormatting) ?? "";
                if (xml.Length > 2048) xml = xml[..2048] + " [patch XML truncated]";
                result += "\nReferenced patch file owner (not proven cause): " + owners[0].PackageId
                    + "\n" + (operation is null ? "Multiple patch operations; no exact operation matched the error." : xml);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
            { result += "\nReferenced patch XML unavailable; file was not read."; }
        }
        return result;
    }
}
