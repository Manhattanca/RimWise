using RimWise.Integration.Configuration;

namespace RimWise.Integration.Steam;

public sealed record SteamGameInstallation(string LibraryDirectory, GamePaths Paths)
{
    public string DisplayPath => Paths.GameDirectory;
}

public sealed record SteamDiscoveryResult(
    IReadOnlyList<SteamGameInstallation> Installations,
    IReadOnlyList<string> WorkshopDirectories,
    IReadOnlyList<string> Warnings);
