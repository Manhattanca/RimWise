namespace RimWise.Core;

public static class GameVersionCompatibility
{
    public static void EnsureCompatibleIdentifiers(GameVersion? source, GameVersion target)
    {
        if ((source?.UsesLegacyIdentifiers == true || target.UsesLegacyIdentifiers)
            && source?.Series != target.Series)
            throw new InvalidDataException($"排序版本 {source?.Series ?? "未注明"} 与目标 {target.Series} 使用不同或未确认的旧版模组标识，无法直接转换；请用目标游戏生成配置后重新载入。");
    }
}
