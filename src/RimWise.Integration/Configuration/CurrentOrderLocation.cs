namespace RimWise.Integration.Configuration;

/// <summary>Locates the current user's configuration without scanning other profiles or drives.</summary>
public static class CurrentOrderLocation
{
    public static string Resolve(GamePaths paths) => Resolve(paths,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Resolve(GamePaths paths, string userProfile)
    {
        if (!string.IsNullOrWhiteSpace(paths.ConfigDirectory))
            return Path.Combine(paths.Normalize().ConfigDirectory, "ModsConfig.xml");
        // UserProfile is independent of APPDATA/LOCALAPPDATA redirection by build tools.
        if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile))
            throw new InvalidDataException("无法识别用户目录，请在路径设置中选择游戏配置目录。");
        return Path.Combine(Path.GetFullPath(userProfile), "AppData", "LocalLow", "Ludeon Studios",
            "RimWorld by Ludeon Studios", "Config", "ModsConfig.xml");
    }
}
