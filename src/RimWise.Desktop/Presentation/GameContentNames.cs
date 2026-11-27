namespace RimWise.Desktop.Presentation;

internal static class GameContentNames
{
    // Steam's translated titles where provided, followed by the game's official
    // Simplified Chinese expansion labels. Package identifiers remain unchanged.
    internal static string DlcName(string packageId) => packageId.ToLowerInvariant() switch
    {
        "ludeon.rimworld.royalty" => "皇权",
        "ludeon.rimworld.ideology" => "文化",
        "ludeon.rimworld.biotech" => "生物技术",
        "ludeon.rimworld.anomaly" => "异象",
        "ludeon.rimworld.odyssey" => "奥德赛",
        _ => packageId
    };
}

public sealed record DlcStatus(string Label, string CompactLabel, bool? IsInstalled, bool IsEnabled = false)
{
    public bool IsActive => IsEnabled || IsInstalled == true;
    public string StatusMark => IsActive ? "✓" : "×";
    public string StatusTooltip => IsEnabled ? Label + "：已启用"
        : IsInstalled switch { true => Label + "：已安装（当前排序未启用）", false => Label + "：未安装", _ => Label + "：等待扫描" };
}
