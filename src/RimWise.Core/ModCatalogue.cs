namespace RimWise.Core;

public enum ModSource { Game, Local, Workshop }
public enum ModCodeKind { Unknown, Xml, CSharp }
[Flags]
public enum ModContentKinds { None = 0, Definitions = 1, Patches = 2, Textures = 4, Audio = 8, Translation = 16, AssetBundles = 32, ManagedCode = 64 }
public enum DiagnosticSeverity { Information, Warning, Error }

public sealed record ModDependency(string PackageId, string DisplayName, string WorkshopId = "")
{
    // Set only by the trusted database projection while a required Workshop item's package is unknown.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool WorkshopIdentityOnly { get; init; }
}

public sealed record ModRequirements(
    IReadOnlyList<ModDependency> Dependencies,
    IReadOnlyList<string> LoadBefore,
    IReadOnlyList<string> LoadAfter,
    IReadOnlyList<string> IncompatibleWith)
{
    public static ModRequirements Empty { get; } = new([], [], [], []);
}

public sealed record ModMetadata(
    string Name,
    string Author,
    string PackageId,
    IReadOnlyList<string> SupportedVersions,
    string LegacyTargetVersion,
    ModRequirements Requirements,
    bool HasVersionedRequirements,
    string Description = "")
{
    public string ReleaseVersion { get; init; } = "";
}

public sealed record CatalogueDiagnostic(
    string Code, DiagnosticSeverity Severity, string Message, string Location = "", string Identifier = "")
{
    public IReadOnlyList<string> RelatedIdentifiers { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> AffectedIdentifiers => new[] { Identifier }.Concat(RelatedIdentifiers)
        .Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed record ModInstallation(
    string Directory,
    string FolderName,
    ModSource Source,
    string WorkshopId,
    ModMetadata? Metadata,
    IReadOnlyList<CatalogueDiagnostic> Diagnostics)
{
    public DateTimeOffset? LastUpdatedUtc { get; init; }
    public ModCodeKind CodeKind { get; init; }
    public ModContentKinds ContentKinds { get; init; }
    public string Name => string.IsNullOrWhiteSpace(Metadata?.Name) ? FolderName : Metadata.Name;
    public string PackageId => Metadata?.PackageId ?? string.Empty;
}

public sealed record OrderMatch(string Identifier, IReadOnlyList<ModInstallation> Candidates)
{
    public ModInstallation? UniqueInstallation => Candidates.Count == 1 ? Candidates[0] : null;
}

/// <summary>Resolves game identifiers, including local/Workshop aliases, without guessing between same-source duplicates.</summary>
public sealed class ModCatalogue
{
    private readonly Dictionary<string, List<ModInstallation>> packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ModInstallation>> folders = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ModInstallation> Installations { get; }

    public ModCatalogue(IReadOnlyList<ModInstallation> installations)
    {
        Installations = installations;
        foreach (var mod in installations)
        {
            if (!string.IsNullOrWhiteSpace(mod.PackageId)) Add(packages, mod.PackageId, mod);
            Add(folders, mod.FolderName, mod);
        }
    }

    public IReadOnlyList<ModInstallation> FindPackage(string id) =>
        packages.TryGetValue(id, out var found) ? found.AsReadOnly() : [];

    public OrderMatch Resolve(string id, GameVersion? configurationVersion)
    {
        if (configurationVersion?.UsesLegacyIdentifiers == true)
            return new OrderMatch(id, folders.TryGetValue(id, out var legacy) ? legacy.AsReadOnly() : []);
        if (packages.TryGetValue(id, out var found))
        {
            // RimWorld gives the Workshop copy a _steam suffix when a non-Workshop copy
            // owns the base identifier. Enumeration order must not change that choice.
            var nonWorkshop = found.Where(mod => mod.Source != ModSource.Workshop).ToArray();
            return new OrderMatch(id, nonWorkshop.Length > 0 ? Array.AsReadOnly(nonWorkshop) : found.AsReadOnly());
        }
        if (id.EndsWith("_steam", StringComparison.OrdinalIgnoreCase) && packages.TryGetValue(id[..^6], out found))
            return new OrderMatch(id, Array.AsReadOnly(found.Where(mod => mod.Source == ModSource.Workshop).ToArray()));
        if (configurationVersion is null && folders.TryGetValue(id, out var fallback))
            return new OrderMatch(id, fallback.AsReadOnly());
        return new OrderMatch(id, []);
    }

    public string IdentifierFor(ModInstallation mod, GameVersion? configurationVersion)
    {
        if (configurationVersion?.UsesLegacyIdentifiers == true) return mod.FolderName;
        if (mod.PackageId.Length == 0) return string.Empty;
        return mod.Source == ModSource.Workshop
            && FindPackage(mod.PackageId).Any(other => other.Source != ModSource.Workshop)
            ? mod.PackageId + "_steam" : mod.PackageId;
    }

    private static void Add(Dictionary<string, List<ModInstallation>> index, string key, ModInstallation mod)
    {
        if (!index.TryGetValue(key, out var entries)) index[key] = entries = [];
        entries.Add(mod);
    }
}
