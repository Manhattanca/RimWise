namespace RimWise.Formats;

/// <summary>Export choices group tools that share the same wire format.</summary>
public sealed record OrderExportTarget(string Name, string Pattern, OrderFileFormat Format);

public static class OrderExportTargets
{
    public static IReadOnlyList<OrderExportTarget> All { get; } = Array.AsReadOnly(new[]
    {
        new OrderExportTarget("原版 / RimPy", "*.xml", OrderFileFormat.ModsConfigXml),
        new OrderExportTarget("RimSort", "*.json", OrderFileFormat.RimSortJson),
        new OrderExportTarget("RimCrow", "*.rml", OrderFileFormat.RmlXml)
    });

    public static string DialogFilter => string.Join("|", All.Select(target => $"{target.Name} ({target.Pattern})|{target.Pattern}"));
}
