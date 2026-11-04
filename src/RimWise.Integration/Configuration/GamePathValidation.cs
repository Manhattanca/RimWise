using RimWise.Integration.Steam;

namespace RimWise.Integration.Configuration;

public static class GamePathValidation
{
    public static GamePaths Validate(GamePaths paths)
    {
        var normalized = paths.Normalize();
        foreach (var directory in new[] { normalized.GameDirectory, normalized.LocalModsDirectory, normalized.WorkshopDirectory, normalized.ConfigDirectory })
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                throw new ArgumentException("目录不存在：" + directory);
        if (!string.IsNullOrEmpty(normalized.GameDirectory) && !SteamGameDiscovery.IsGameDirectory(normalized.GameDirectory))
            throw new ArgumentException("游戏目录中未找到 RimWorld 程序及 Data 文件夹。");
        return normalized;
    }
}
