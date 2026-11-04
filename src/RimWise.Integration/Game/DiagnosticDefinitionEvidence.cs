using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Read-only comparison of failed patch targets with installed XML. Never executes patch classes,
/// follows model-supplied paths, or claims that disk contents prove the live patched state.</summary>
internal static partial class DiagnosticDefinitionEvidence
{
    private sealed record ParentLocation(string Id, int Index, string File, DiagnosticXmlTarget Target);
    [GeneratedRegex(@"\bVersion:\s*(\d+\.\d+)\.")]
    private static partial Regex RuntimeVersion();

    internal static IReadOnlyList<AiModFileEvidence> Collect(OrdinaryGameReport report, ModCatalogue catalogue,
        IReadOnlyList<string> order, GameVersion? configuredVersion, CancellationToken token, out bool limited)
    {
        limited = false;
        var observed = report.Logs.Select(l => RuntimeVersion().Match(l.Text)).FirstOrDefault(m => m.Success);
        var version = observed is null ? configuredVersion : GameVersion.Parse(observed.Groups[1].Value);
        var active = order.Select((id, index) => (Id: id, Index: index + 1, Mod: catalogue.Resolve(id, version).UniqueInstallation))
            .Where(m => m.Mod is not null).ToArray();
        var activePackages = active.Select(m => m.Mod!.PackageId).Concat(order).ToHashSet(StringComparer.OrdinalIgnoreCase);
        token.ThrowIfCancellationRequested();
        var targets = new HashSet<DiagnosticXmlTarget>();
        var observedIssues = report.Logs.SelectMany(l => l.Issues).Take(128).ToArray();
        foreach (var target in observedIssues.SelectMany(i => DiagnosticXmlTargets.FromError(i.Detail)))
            if (targets.Count < 8 && DiagnosticXmlTargets.Identifier(target.Type)
                && DiagnosticXmlTargets.Identifier(target.Name)) targets.Add(target); else limited = true;
        var exact = new List<(string Id, int Index, XElement Operation)>();
        foreach (var issue in observedIssues)
        foreach (var file in DiagnosticPatchEvidence.ReferencedFiles(issue.Detail))
        {
            token.ThrowIfCancellationRequested();
            var owners = active.Where(m => DiagnosticPatchEvidence.IsWithin(file, m.Mod!.Directory)).ToArray();
            if (owners.Length != 1 || !file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var operations = Read(file, 65536).Root?.Elements().ToArray() ?? [];
                var operation = operations.FirstOrDefault(n => n.DescendantsAndSelf().Any(x => x.Name.LocalName == "xpath"
                    && x.Value.Length > 0 && issue.Detail.Contains(x.Value, StringComparison.Ordinal)))
                    ?? (operations.Length == 1 ? operations[0] : null);
                if (operation is null) continue;
                exact.Add((owners[0].Id, owners[0].Index, operation));
                foreach (var target in DiagnosticXmlTargets.FromOperation(operation))
                    if (targets.Count < 8 && DiagnosticXmlTargets.Identifier(target.Type)
                        && DiagnosticXmlTargets.Identifier(target.Name)) targets.Add(target); else limited = true;
            }
            catch (Exception ex) when (Unavailable(ex)) { limited = true; }
        }
        if (targets.Count == 0) return [];
        var result = new List<AiModFileEvidence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var characters = 0;
        var evidenceLimited = false;
        var neededParents = new HashSet<DiagnosticXmlTarget>(targets.Where(t => t.IsParent));
        var parents = new List<ParentLocation>();
        void Add(string kind, string id, int index, DiagnosticXmlTarget target, XElement element)
        {
            var text = DiagnosticLogContext.Sanitize(element.ToString(SaveOptions.DisableFormatting));
            if (text.Length == 0 || !seen.Add(kind + id + target + text)) return;
            if (text.Length > 3072) { text = text[..3030] + " [XML evidence truncated]"; evidenceLimited = true; }
            if (kind == "active-reference" && (result.Count(f => f.Kind == kind) >= 6
                || result.Where(f => f.Kind == kind).Sum(f => f.Text.Length) + text.Length > 8192))
            { evidenceLimited = true; return; }
            if (result.Count >= 24 || characters + text.Length > 32768) { evidenceLimited = true; return; }
            result.Add(new(kind, id, index, target.Type, target.Name, text)); characters += text.Length;
            if (kind != "active-patch" && element.Attribute("ParentName")?.Value is { Length: > 0 and <= 160 } parent)
            {
                if (DiagnosticXmlTargets.Identifier(element.Name.LocalName) && DiagnosticXmlTargets.Identifier(parent))
                    neededParents.Add(new(element.Name.LocalName, parent, true));
                else evidenceLimited = true;
            }
        }
        // Preserve the failing operation before unrelated definitions or other patches can fill the quota.
        foreach (var item in exact)
        foreach (var target in DiagnosticXmlTargets.FromOperation(item.Operation).Where(targets.Contains))
            Add("active-patch", item.Id, item.Index, target, item.Operation);

        var activePaths = active.Select(m => m.Mod!.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = active.Select(m => (m.Id, m.Index, Mod: m.Mod!)).Concat(catalogue.Installations
            .Where(m => m.Source == ModSource.Game && !activePaths.Contains(m.Directory))
            .Select(m => (Id: m.PackageId, Index: 0, Mod: m))).ToArray();
        long bytes = 0; var fileCount = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Give every installation a small first pass through both patches and definitions.
        // A large early mod must not consume the entire budget before later callers are read.
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = candidates.Where(m => m.Index > 0).Select(m => (Item: m, Section: "Patches"))
            .Concat(candidates.OrderBy(m => m.Mod.Source == ModSource.Game ? 1 : 0).Select(m => (Item: m, Section: "Defs")));
        foreach (var batchSize in new[] { 8, int.MaxValue })
        foreach (var entry in work)
        {
            var item = entry.Item;
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalSeconds > 15 || fileCount >= 12000 || bytes >= 48 * 1024 * 1024)
            { limited = true; goto Complete; }
            if (string.IsNullOrEmpty(item.Id)) continue;
            try
            {
                foreach (var folder in DiagnosticLoadFolders.Select(item.Mod.Directory, version, activePackages, ref limited))
                {
                    var section = entry.Section;
                    var path = ScopedPaths.Child(folder, section);
                    if (!Directory.Exists(path)) continue;
                    // No links/reparse recursion. A malformed or oversized XML is omitted explicitly.
                    var options = new EnumerationOptions { RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false, MaxRecursionDepth = 16 };
                    foreach (var file in Directory.EnumerateFiles(path, "*.xml", options).Where(file => !scanned.Contains(file)).Take(batchSize))
                    {
                        token.ThrowIfCancellationRequested();
                        scanned.Add(file);
                        if (++fileCount > 12000 || clock.Elapsed.TotalSeconds > 15) { limited = true; goto Complete; }
                        try
                        {
                            var length = new FileInfo(ScopedPaths.Ordinary(file, true)).Length;
                            if (length > 512 * 1024) { limited = true; continue; }
                            bytes += length;
                            if (bytes > 48 * 1024 * 1024) { limited = true; goto Complete; }
                            var document = ReadRelevant(file, targets, section == "Defs");
                            if (document is null) continue;
                            if (section == "Defs")
                            {
                                foreach (var definition in document.Root?.Elements() ?? [])
                                {
                                    if (definition.Attribute("Name")?.Value is { Length: > 0 and <= 160 } name)
                                    {
                                        if (parents.Count < 4096 && DiagnosticXmlTargets.Identifier(definition.Name.LocalName)
                                            && DiagnosticXmlTargets.Identifier(name)) parents.Add(new(item.Id, item.Index, file,
                                            new(definition.Name.LocalName, name, true)));
                                        else limited = true;
                                    }
                                    foreach (var target in targets)
                                    {
                                        if (!target.IsParent && definition.Name.LocalName == target.Type
                                            && definition.Element("defName")?.Value == target.Name)
                                            Add(item.Index == 0 ? "inactive-official-definition" : "active-definition",
                                                item.Id, item.Index, target, definition);
                                        // A reference is a candidate caller, never proof of its expected Def type.
                                        else if (!target.IsParent && item.Index > 0 && definition.Descendants().Any(n =>
                                            !n.HasElements && n.Name.LocalName != "defName"
                                            && (n.Value.Trim() == target.Name || n.Name.LocalName == target.Name)))
                                            Add("active-reference", item.Id, item.Index, target, definition);
                                        else if (target.IsParent && item.Index > 0 && definition.Name.LocalName == target.Type
                                            && (string?)definition.Attribute("ParentName") == target.Name)
                                            Add("active-reference", item.Id, item.Index, target, definition);
                                    }
                                }
                            }
                            else
                            {
                                foreach (var operation in document.Root?.Elements() ?? [])
                                {
                                    foreach (var target in DiagnosticXmlTargets.FromOperation(operation).Where(targets.Contains).Distinct())
                                        Add("active-patch", item.Id, item.Index, target, operation);
                                    // Added/replaced values can introduce the missing reference without
                                    // mentioning it in the destination XPath. Keep this as a caller hypothesis.
                                    foreach (var target in targets.Where(t => !t.IsParent))
                                        if (operation.Descendants("value").Any(value => value.Descendants().Any(n =>
                                            !n.HasElements && n.Name.LocalName != "defName"
                                            && (n.Value.Trim() == target.Name || n.Name.LocalName == target.Name))))
                                            Add("active-reference", item.Id, item.Index, target, operation);
                                }
                            }
                        }
                        catch (Exception ex) when (Unavailable(ex)) { limited = true; }
                    }
                }
            }
            catch (Exception ex) when (Unavailable(ex)) { limited = true; }
        }
        Complete:
        // Reopen only indexed parent candidates. This follows inheritance across files without
        // repeatedly scanning a large mod library; loops and duplicate providers remain explicit.
        var visited = new HashSet<DiagnosticXmlTarget>();
        for (var depth = 0; depth < 16; depth++)
        {
            var pending = neededParents.Where(t => !visited.Contains(t)).Take(32).ToArray();
            if (pending.Length == 0) break;
            foreach (var target in pending)
            {
                visited.Add(target);
                foreach (var location in parents.Where(p => p.Target == target))
                {
                    token.ThrowIfCancellationRequested();
                    if (++fileCount > 12000 || clock.Elapsed.TotalSeconds > 15 || bytes >= 48 * 1024 * 1024)
                    { limited = true; goto Finish; }
                    try
                    {
                        bytes += new FileInfo(ScopedPaths.Ordinary(location.File, true)).Length;
                        if (bytes > 48 * 1024 * 1024) { limited = true; goto Finish; }
                        var document = Read(location.File, 512 * 1024);
                        foreach (var definition in document.Root?.Elements().Where(n => n.Name.LocalName == target.Type
                            && (string?)n.Attribute("Name") == target.Name) ?? [])
                            Add(location.Index == 0 ? "inactive-official-parent" : "active-parent",
                                location.Id, location.Index, target, definition);
                    }
                    catch (Exception ex) when (Unavailable(ex)) { limited = true; }
                }
            }
        }
        limited |= neededParents.Any(t => !visited.Contains(t));
        Finish:
        limited |= evidenceLimited;
        return result;
    }

    private static XDocument Read(string path, int maximum)
    {
        using var stream = new FileStream(ScopedPaths.Ordinary(path, true), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximum) throw new IOException("XML evidence exceeds limit.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = maximum });
        return XDocument.Load(reader);
    }

    private static XDocument? ReadRelevant(string path, IReadOnlySet<DiagnosticXmlTarget> targets, bool includeParents)
    {
        using var stream = new FileStream(ScopedPaths.Ordinary(path, true), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 512 * 1024) throw new IOException("XML evidence exceeds limit.");
        // Bound the read even if the file grows after opening. StreamReader detects XML BOMs;
        // unrelated documents need not incur a full DOM allocation or entity processing.
        using var textReader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[8192]; var content = new System.Text.StringBuilder();
        while (true)
        {
            var read = textReader.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (content.Length + read > 512 * 1024) throw new IOException("XML evidence exceeds limit.");
            content.Append(buffer, 0, read);
        }
        var text = content.ToString();
        if (!targets.Any(t => text.Contains(t.Name, StringComparison.Ordinal))
            && !(includeParents && text.Contains("Name", StringComparison.Ordinal))) return null;
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 512 * 1024 });
        return XDocument.Load(reader);
    }

    private static bool Unavailable(Exception ex) => ex is IOException or UnauthorizedAccessException or XmlException
        or ArgumentException or NotSupportedException;
}
