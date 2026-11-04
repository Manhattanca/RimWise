using System.Xml;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Selects diagnostic content roots using the game's version fallback and load conditions.
/// This is only file selection: XML patches and mod code are never executed.</summary>
internal static class DiagnosticLoadFolders
{
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
            using var stream = new FileStream(ScopedPaths.Ordinary(loadFile, true), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 65536 });
            var nodes = XDocument.Load(reader).Root?.Elements().ToArray() ?? [];
            var selected = nodes.FirstOrDefault(n => Key(n) == version.Series && n.HasElements)
                ?? nodes.Where(n => Parsed(Key(n)) is { } v && v <= current)
                    .OrderByDescending(n => Key(n), StringComparer.Ordinal).FirstOrDefault()
                ?? nodes.FirstOrDefault(n => Key(n) == "default");
            if (selected is not null)
            {
                var result = new List<string>();
                foreach (var node in selected.Elements().Take(32))
                {
                    if (node.HasElements || node.Attributes().Any(a => a.Name.LocalName is not
                        ("IfModActive" or "IfModActiveAll" or "IfModNotActive")))
                    { limited = true; continue; }
                    bool IsActive(string id) => activeIds.Contains(WithoutSteamSuffix(id));
                    string[] Ids(string attribute) => node.Attribute(attribute)?.Value.Split(',').Select(s => s.Trim()).ToArray() ?? [];
                    var any = Ids("IfModActive"); var all = Ids("IfModActiveAll"); var none = Ids("IfModNotActive");
                    if (any.Length > 0 && !any.Any(IsActive) || !all.All(IsActive) || none.Any(IsActive)) continue;
                    var relative = node.Value.Trim();
                    var path = relative is "/" or "\\" or "" ? directory : ScopedPaths.Child(directory, relative);
                    if (Directory.Exists(path)) result.Add(path);
                }
                if (selected.Elements().Count() > 32) limited = true;
                return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        var folders = new List<string>();
        var exact = ScopedPaths.Child(directory, version.Series);
        if (Directory.Exists(exact)) folders.Add(exact);
        else
        {
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

    private static string Key(XElement node) => node.Name.LocalName.ToLowerInvariant().TrimStart('v');
    private static string WithoutSteamSuffix(string id) => id.EndsWith("_steam", StringComparison.OrdinalIgnoreCase)
        ? id[..^6] : id;
    private static Version? Parsed(string text) => GameVersion.Parse(text) is { } v
        ? new(v.Major, v.Minor, v.Build ?? 0) : null;
}
