using RimWise.Core;

namespace RimWise.Integration.Scanning;

public sealed record GameVersionDetection(GameVersion? Version, string RawText, string EvidenceFile);

public static class GameVersionDetector
{
    public static GameVersionDetection Detect(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return new(null, string.Empty, string.Empty);
        var file = Path.Combine(gameDirectory, "Version.txt");
        if (!File.Exists(file)) return new(null, string.Empty, file);
        var raw = ReadOnlyMetadata.ReadText(file, gameDirectory, 4096);
        return new(GameVersion.Parse(raw.Split(['\r', '\n'], 2)[0]), raw, file);
    }
}
