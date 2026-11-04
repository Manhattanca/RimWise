namespace RimWise.Integration.Configuration;

/// <summary>User-selected directories, not authorization to write to those directories.</summary>
public sealed record GamePaths
{
    public string GameDirectory { get; init; } = string.Empty;
    public string LocalModsDirectory { get; init; } = string.Empty;
    public string WorkshopDirectory { get; init; } = string.Empty;
    public string TargetVersionOverride { get; init; } = string.Empty;
    public string ConfigDirectory { get; init; } = string.Empty;

    public GamePaths Normalize() => this with
    {
        GameDirectory = NormalizeDirectory(GameDirectory),
        LocalModsDirectory = NormalizeDirectory(LocalModsDirectory),
        WorkshopDirectory = NormalizeDirectory(WorkshopDirectory),
        ConfigDirectory = NormalizeDirectory(ConfigDirectory),
        TargetVersionOverride = NormalizeVersion(TargetVersionOverride)
    };

    private static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return RimWise.Core.GameVersion.Parse(value)?.Series
            ?? throw new ArgumentException("目标版本请输入 1.7、1.6、1.0 或 0.19 等版本号；留空则自动识别。");
    }

    private static string NormalizeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Trim();
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("请选择完整的目录路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
}
