using System.Text;
using System.Xml;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Selects diagnostic content roots using the game's version fallback and load conditions.
/// This is only file selection: XML patches and mod code are never executed.</summary>
internal static class DiagnosticLoadFolders
{
    private const int MaximumSelectedEntries = 32;
    private const int MaximumSectionElements = 2_000_000;
    private const int MaximumConditionIds = 20_000;
    private const int MaximumAttributeCharacters = 1_048_576;
    private const int MaximumEntryCharacters = 65_536;
    private const long StreamingThresholdBytes = 16L * 1024 * 1024;
    private const long MaximumSmallDocumentCharacters = 16L * 1024 * 1024;
    private const long MaximumFileBytes = 256L * 1024 * 1024;

    internal static IReadOnlyList<string> Select(string directory, GameVersion? version,
        IReadOnlySet<string> activePackages, ref bool limited)
    {
        directory = ScopedPaths.Ordinary(directory, true);
        if (version is null) { limited = true; return []; }
        var activeIds = activePackages.Select(WithoutSteamSuffix).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = new Version(version.Major, version.Minor, version.Build ?? 0);
        var loadFile = ScopedPaths.Child(directory, "LoadFolders.xml");
        if (File.Exists(loadFile))
        {
            try
            {
                using var stream = new FileStream(ScopedPaths.Ordinary(loadFile, true), FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaximumFileBytes)
                {
                    limited = true;
                    return [];
                }

                if (stream.Length <= StreamingThresholdBytes)
                {
                    using var reader = CreateReader(stream, MaximumSmallDocumentCharacters);
                    var selected = ReadSmallDocument(reader, directory, current, version.Series,
                        activeIds, ref limited);
                    if (selected is not null) return selected;
                }
                else
                {
                    using var reader = CreateReader(stream, 0);
                    var selected = ReadLargeDocument(reader, directory, current, version.Series,
                        activeIds, ref limited);
                    if (selected is not null)
                    {
                        limited |= selected.Limited;
                        return selected.Paths;
                    }
                }
            }
            catch (XmlException)
            {
                // LoadFolders.xml is optional diagnostic metadata. Do not infer a
                // different folder layout after malformed XML.
                limited = true;
                return [];
            }
        }
        return Fallback(directory, version);
    }

    private static XmlReader CreateReader(Stream stream, long maximumCharacters)
    {
        return XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maximumCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });
    }

    private static IReadOnlyList<string>? ReadSmallDocument(XmlReader reader, string directory,
        Version current, string exactSeries, IReadOnlySet<string> activeIds, ref bool limited)
    {
        var nodes = XDocument.Load(reader).Root?.Elements().ToArray() ?? [];
        var exactKey = exactSeries.ToLowerInvariant().TrimStart('v');
        var selected = nodes.FirstOrDefault(n => Key(n) == exactKey && n.HasElements)
            ?? nodes.Where(n => Parsed(Key(n)) is { } v && v <= current)
                .OrderByDescending(n => Key(n), StringComparer.Ordinal).FirstOrDefault()
            ?? nodes.FirstOrDefault(n => Key(n) == "default");
        if (selected is null) return null;

        var result = new List<string>();
        foreach (var node in selected.Elements().Take(MaximumSelectedEntries))
        {
            if (node.HasElements || node.Attributes().Any(a => a.Name.LocalName is not
                ("IfModActive" or "IfModActiveAll" or "IfModNotActive")))
            {
                limited = true;
                continue;
            }

            bool IsActive(string id) => activeIds.Contains(WithoutSteamSuffix(id));
            string[] Ids(string attribute) => node.Attribute(attribute)?.Value
                .Split(',').Select(s => s.Trim()).ToArray() ?? [];
            var any = Ids("IfModActive");
            var all = Ids("IfModActiveAll");
            var none = Ids("IfModNotActive");
            if (any.Length > 0 && !any.Any(IsActive) || !all.All(IsActive) || none.Any(IsActive)) continue;

            var relative = node.Value.Trim();
            var path = relative is "/" or "\\" or "" ? directory : ScopedPaths.Child(directory, relative);
            if (Directory.Exists(path)) result.Add(path);
        }

        if (selected.Elements().Count() > MaximumSelectedEntries) limited = true;
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static SectionResult? ReadLargeDocument(XmlReader reader, string directory, Version current,
        string exactSeries, IReadOnlySet<string> activeIds, ref bool limited)
    {
        if (!reader.Read() || reader.NodeType != XmlNodeType.Element)
        {
            limited = true;
            return null;
        }

        var rootDepth = reader.Depth;
        if (reader.IsEmptyElement) return null;
        var exactKey = exactSeries.ToLowerInvariant().TrimStart('v');
        SectionResult? exact = null;
        SectionResult? best = null;
        Version? bestVersion = null;
        SectionResult? @default = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == rootDepth) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1) continue;

            var key = Key(reader.LocalName);
            if (key == exactKey)
            {
                if (exact is null) exact = ReadSection(reader, directory, activeIds);
                else SkipElement(reader);
                continue;
            }
            if (key == "default")
            {
                if (@default is null) @default = ReadSection(reader, directory, activeIds);
                else SkipElement(reader);
                continue;
            }

            var parsed = Parsed(key);
            if (parsed is not null && parsed <= current && (bestVersion is null || parsed > bestVersion))
            {
                bestVersion = parsed;
                best = ReadSection(reader, directory, activeIds);
            }
            else SkipElement(reader);
        }

        return exact ?? best ?? @default;
    }

    private static SectionResult ReadSection(XmlReader reader, string directory,
        IReadOnlySet<string> activeIds)
    {
        var sectionDepth = reader.Depth;
        var paths = new List<string>();
        var limited = false;
        var elements = 0;
        if (reader.IsEmptyElement) return new(paths.ToArray(), false);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == sectionDepth) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != sectionDepth + 1) continue;

            elements++;
            if (elements > MaximumSectionElements)
            {
                limited = true;
                SkipElement(reader);
                continue;
            }

            var entryNumber = elements;
            var entry = ReadEntry(reader, activeIds);
            if (entry.Invalid) limited = true;
            // Preserve the previous behavior: only the first 32 direct child
            // elements participate, even when earlier entries are filtered out.
            if (entryNumber > MaximumSelectedEntries || entry.Invalid || entry.Path is null) continue;
            var path = entry.Path is "/" or "\\" or "" ? directory : ScopedPaths.Child(directory, entry.Path);
            if (Directory.Exists(path)) paths.Add(path);
        }

        if (elements > MaximumSelectedEntries) limited = true;
        return new(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), limited);
    }

    private static EntryResult ReadEntry(XmlReader reader, IReadOnlySet<string> activeIds)
    {
        var invalid = false;
        for (var i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.LocalName is not ("IfModActive" or "IfModActiveAll" or "IfModNotActive")) invalid = true;
            if (reader.Value.Length > MaximumAttributeCharacters) invalid = true;
        }
        reader.MoveToElement();

        var any = Ids(reader.GetAttribute("IfModActive"), ref invalid);
        var all = Ids(reader.GetAttribute("IfModActiveAll"), ref invalid);
        var none = Ids(reader.GetAttribute("IfModNotActive"), ref invalid);
        var text = new StringBuilder();
        var nested = false;
        var depth = reader.Depth;
        if (!reader.IsEmptyElement)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    nested = true;
                    SkipElement(reader);
                    continue;
                }
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                    XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    AppendValue(reader, text, ref invalid);
                }
            }
        }

        if (nested || invalid || any.Length > 0 && !any.Any(IsActive) || !all.All(IsActive) || none.Any(IsActive))
            return new(null, true);
        return new(text.ToString().Trim(), false);

        bool IsActive(string id) => activeIds.Contains(WithoutSteamSuffix(id));
    }

    private static string[] Ids(string? value, ref bool invalid)
    {
        if (value is null) return [];
        if (value.Length > MaximumAttributeCharacters) { invalid = true; return []; }
        var result = value.Split(',').Select(s => s.Trim()).ToArray();
        if (result.Length > MaximumConditionIds) invalid = true;
        return result;
    }

    private static void AppendValue(XmlReader reader, StringBuilder text, ref bool invalid)
    {
        if (!reader.CanReadValueChunk)
        {
            if (text.Length + reader.Value.Length > MaximumEntryCharacters) invalid = true;
            else text.Append(reader.Value);
            return;
        }

        var buffer = new char[4096];
        int read;
        while ((read = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
        {
            if (text.Length + read > MaximumEntryCharacters) invalid = true;
            else text.Append(buffer, 0, read);
        }
    }

    private static void SkipElement(XmlReader reader)
    {
        if (reader.IsEmptyElement) return;
        var depth = reader.Depth;
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) return;
    }

    private static IReadOnlyList<string> Fallback(string directory, GameVersion version)
    {
        var folders = new List<string>();
        var exact = ScopedPaths.Child(directory, version.Series);
        if (Directory.Exists(exact)) folders.Add(exact);
        else
        {
            var current = new Version(version.Major, version.Minor, version.Build ?? 0);
            var versions = Directory.EnumerateDirectories(directory)
                .Select(path => (Path: path, Version: Parsed(Path.GetFileName(path))))
                .Where(v => v.Version is { Major: > 0 }).ToArray();
            var fallback = versions.Where(v => v.Version <= current).OrderByDescending(v => v.Version).FirstOrDefault();
            // With no older directory, the game uses the earliest available version.
            fallback = fallback.Path is null ? versions.OrderBy(v => v.Version).FirstOrDefault() : fallback;
            if (fallback.Path is not null) folders.Add(ScopedPaths.Ordinary(fallback.Path, true));
        }
        var common = ScopedPaths.Child(directory, "Common");
        if (Directory.Exists(common)) folders.Add(common);
        folders.Add(directory);
        return folders;
    }

    private sealed record EntryResult(string? Path, bool Invalid);
    private sealed record SectionResult(string[] Paths, bool Limited);

    private static string Key(XElement node) => Key(node.Name.LocalName);
    private static string Key(string name) => name.ToLowerInvariant().TrimStart('v');
    private static string WithoutSteamSuffix(string id) => id.EndsWith("_steam", StringComparison.OrdinalIgnoreCase)
        ? id[..^6] : id;
    private static Version? Parsed(string text) => GameVersion.Parse(text) is { } v
        ? new(v.Major, v.Minor, v.Build ?? 0) : null;
}
