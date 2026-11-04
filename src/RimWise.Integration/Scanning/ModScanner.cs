using System.Xml;
using RimWise.Core;
using RimWise.Integration.Configuration;

namespace RimWise.Integration.Scanning;

public sealed record ScanProgress(int Processed, string CurrentName);
public sealed record ModScanResult(GameVersionDetection DetectedVersion, GameVersion? TargetVersion,
    ModCatalogue Catalogue, IReadOnlyList<CatalogueDiagnostic> Diagnostics)
{
    public IReadOnlyList<string> ActivityRoots { get; init; } = [];
}

/// <summary>Reads metadata first; optional content inspection runs separately from the first display.</summary>
public sealed class ModScanner
{
    public const int MaximumDirectories = 20000;

    /// <summary>Reads only confirmed downloaded items; never enumerates the workshop library.</summary>
    public IReadOnlyList<ModInstallation> ReadWorkshopItems(string workshopRoot, IEnumerable<string> ids,
        GameVersion? target, CancellationToken cancellationToken = default)
    {
        var requested = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Any(id => !ulong.TryParse(id, out var number) || number == 0
            || number.ToString(System.Globalization.CultureInfo.InvariantCulture) != id))
            throw new InvalidDataException("工坊编号无效。");
        workshopRoot = Path.GetFullPath(workshopRoot);
        ReadOnlyMetadata.CheckPath(workshopRoot, workshopRoot);
        var result = new List<ModInstallation>();
        var times = new ModUpdateTimeReader();
        foreach (var id in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(workshopRoot, id);
            ReadOnlyMetadata.CheckPath(directory, workshopRoot);
            var installation = ReadInstallation(directory, workshopRoot, ModSource.Workshop,
                Path.Combine(directory, "About", "About.xml"), target)
                ?? throw new InvalidDataException($"工坊模组 {id} 的文件尚未完整下载，或已经移除；请等待 Steam 完成下载后重试。");
            var content = ModContentReader.Read(directory, cancellationToken, times.SteamTime(installation));
            result.Add(installation with { LastUpdatedUtc = content.LastUpdatedUtc,
                CodeKind = content.CodeKind, ContentKinds = content.Kinds });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result.AsReadOnly();
    }

    public ModScanResult InspectContent(ModScanResult metadata, CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        var times = new ModUpdateTimeReader();
        var installations = new List<ModInstallation>();
        foreach (var mod in metadata.Catalogue.Installations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mod.Source == ModSource.Game) installations.Add(mod);
            else
            {
                var content = ModContentReader.Read(mod.Directory, cancellationToken,
                    mod.Source == ModSource.Workshop ? times.SteamTime(mod) : null);
                installations.Add(mod with { LastUpdatedUtc = content.LastUpdatedUtc, CodeKind = content.CodeKind, ContentKinds = content.Kinds });
            }
            if (installations.Count % 25 == 0) progress?.Report(new(installations.Count, ""));
        }
        return metadata with { Catalogue = new(installations) };
    }

    public ModScanResult Scan(GamePaths paths, CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null, GameVersion? targetOverride = null, bool inspectContent = true)
    {
        paths = paths.Normalize();
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<CatalogueDiagnostic>();
        var version = new GameVersionDetection(null, string.Empty, string.Empty);
        try { version = GameVersionDetector.Detect(paths.GameDirectory); }
        catch (Exception ex) when (ReadFailure(ex)) { diagnostics.Add(new("VersionReadFailed", DiagnosticSeverity.Warning, ex.Message, paths.GameDirectory)); }
        var target = targetOverride ?? GameVersion.Parse(paths.TargetVersionOverride) ?? version.Version;
        if (target is null)
            diagnostics.Add(new("GameVersionUnknown", DiagnosticSeverity.Warning, "未识别游戏版本，可在设置中指定；版本条件规则尚不能完整判断。", version.EvidenceFile));
        var roots = new List<(string Path, ModSource Source)>();
        if (paths.GameDirectory.Length > 0) roots.Add((Path.Combine(paths.GameDirectory, "Data"), ModSource.Game));
        var local = paths.LocalModsDirectory.Length > 0 ? paths.LocalModsDirectory
            : paths.GameDirectory.Length > 0 ? Path.Combine(paths.GameDirectory, "Mods") : string.Empty;
        if (local.Length > 0) roots.Add((local, ModSource.Local));
        if (paths.WorkshopDirectory.Length > 0) roots.Add((paths.WorkshopDirectory, ModSource.Workshop));
        if (roots.Count == 0) diagnostics.Add(new("NoScanPaths", DiagnosticSeverity.Warning, "请先设置游戏或模组目录。"));
        var installations = new List<ModInstallation>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;
        var updateTimes = new ModUpdateTimeReader();
        var activityRoots = new List<string>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadOnlyMetadata.CheckPath(root.Path, root.Path);
                var candidates = Directory.EnumerateDirectories(root.Path).Take(MaximumDirectories + 1).Order(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visited.Add(Path.GetFullPath(directory))) continue;
                    if (++processed > MaximumDirectories)
                    {
                        diagnostics.Add(new("ScanLimit", DiagnosticSeverity.Error, $"目录超过 {MaximumDirectories} 项，扫描未完整。", root.Path));
                        return Finish();
                    }
                    var about = Path.Combine(directory, "About", "About.xml");
                    if (root.Source == ModSource.Game && !File.Exists(about)) continue;
                    var installation = ReadInstallation(directory, root.Path, root.Source, about, target);
                    if (installation is null) continue;
                    if (inspectContent && root.Source != ModSource.Game)
                    {
                        var content = ModContentReader.Read(directory, cancellationToken,
                            root.Source == ModSource.Workshop ? updateTimes.SteamTime(installation) : null);
                        installation = installation with { LastUpdatedUtc = content.LastUpdatedUtc,
                            CodeKind = content.CodeKind, ContentKinds = content.Kinds };
                    }
                    installations.Add(installation);
                    if (processed % 25 == 0) progress?.Report(new(processed, Path.GetFileName(directory)));
                }
                if (root.Source != ModSource.Game) activityRoots.Add(root.Path);
            }
            catch (Exception ex) when (ReadFailure(ex))
            { diagnostics.Add(new("ScanRootUnreadable", DiagnosticSeverity.Warning, $"无法完整扫描所选目录：{ex.Message}", root.Path)); }
        }
        return Finish();

        ModScanResult Finish()
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(processed, string.Empty));
            var catalogue = new ModCatalogue(installations.AsReadOnly());
            diagnostics.AddRange(CatalogueAnalysis.Inspect(catalogue, target));
            return new(version, target, catalogue, diagnostics.AsReadOnly()) { ActivityRoots = activityRoots.AsReadOnly() };
        }
    }

    private static ModInstallation? ReadInstallation(string directory, string boundary, ModSource source, string about, GameVersion? target)
    {
        var issues = new List<CatalogueDiagnostic>();
        ModMetadata? metadata = null;
        var folder = Path.GetFileName(directory);
        var workshopId = source == ModSource.Workshop && folder.All(char.IsAsciiDigit) ? folder : string.Empty;
        try
        {
            ReadOnlyMetadata.CheckPath(directory, boundary);
            if (!File.Exists(about) && File.Exists(about + ".disabled"))
                throw new InvalidDataException("缺少 About/About.xml，发现 About.xml.disabled。元数据文件已被改名，无法正常识别此模组；请核对模组来源及禁用原因。");
            metadata = new ModMetadataReader().Read(about, boundary, target);
            if (metadata.PackageId.Length == 0 && target?.UsesLegacyIdentifiers != true)
                issues.Add(new("PackageIdUnknown", DiagnosticSeverity.Warning,
                    $"{folder} 未声明 packageId；可能是旧格式，尚未推断现代标识。", directory));
            var published = Path.Combine(directory, "About", "PublishedFileId.txt");
            if (workshopId.Length == 0 && File.Exists(published))
            {
                var value = ReadOnlyMetadata.ReadText(published, boundary, 128);
                if (value.Length > 0 && value.All(char.IsAsciiDigit)) workshopId = value;
            }
        }
        // Steam can leave an empty item directory after unsubscribe, or while downloading.
        // Only confirmed absence excludes it: malformed, disabled or inaccessible metadata
        // still needs its read diagnostic, and local legacy folders retain their old handling.
        catch (Exception ex) when (source == ModSource.Workshop && metadata is null
            && ex is FileNotFoundException or DirectoryNotFoundException)
        { return null; }
        catch (Exception ex) when (ReadFailure(ex))
        { issues.Add(new("MetadataUnreadable", DiagnosticSeverity.Warning, $"{folder} 信息未完整读取：{ex.Message}", directory)); }
        return new(directory, folder, source, workshopId, metadata, issues.AsReadOnly());
    }

    private static bool ReadFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or ArgumentException;
}
