namespace RimWise.Integration.AI;

/// <summary>Bounded technical facts only. Log prose, paths and instructions never leave the machine.</summary>
public sealed record AiRuntimeObservation(string Code, string PackageId, int Count);

public sealed record AiRuntimeContext(IReadOnlyList<AiRuntimeObservation> Observations)
{
    public const string VulkanWarningCode = "VulkanConfigurationReadError";
    public const string VulkanWarningText = "检测到 Overwolf／Epic 的 Vulkan 配置读取错误，已忽略此项；原始记录保留在日志中。";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVulkanConfigurationWarning => Observations.Any(o => o.Code == VulkanWarningCode);

    private static readonly HashSet<string> Codes = ["TextureMipmaps", "MissingCrossReference",
        "KeyBindingConflict", "UninitializedDefOf", "TranslationWarning", "NumericConversion",
        "ModMetadataWarning", "UnknownWarning", VulkanWarningCode];

    internal AiRuntimeContext CopyValidated()
    {
        if (Observations is null || Observations.Count is < 1 or > 100
            || Observations.Any(o => o is null || !Codes.Contains(o.Code) || o.Count is < 1 or > 100000
                || !AiModIdentifier.IsValidOrEmpty(o.PackageId)))
            throw new InvalidDataException("运行警告上下文无效。");
        return this with { Observations = Observations.ToArray() };
    }
}
