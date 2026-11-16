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
