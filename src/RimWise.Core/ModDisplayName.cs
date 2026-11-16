namespace RimWise.Core;

/// <summary>Presentation only; identifiers remain the authority for every operation.</summary>
public static class ModDisplayName
{
    public static string Resolve(ModCatalogue? catalogue, GameVersion? version, string identifier)
    {
        var name = catalogue?.Resolve(identifier, version).UniqueInstallation?.Metadata?.Name;
        return string.IsNullOrWhiteSpace(name) ? identifier : name.Trim();
    }
}
