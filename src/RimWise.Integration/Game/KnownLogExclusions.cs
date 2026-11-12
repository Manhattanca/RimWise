namespace RimWise.Integration.Game;

/// <summary>Explicitly approved non-failure messages. Never exclude a whole mod or error category.</summary>
internal static class KnownLogExclusions
{
    internal const string GodHandFlavorMessage = "我修复了大部分的报错，但是我保留了一部分，我觉得保留了一部分红字，才知道你玩的是RIMWORLD";

    internal static bool Matches(string message, string detail)
    {
        if (message.StartsWith("[Verse] ", StringComparison.Ordinal)) message = message[8..];
        if (IsOverlayVulkanManifestError(message)) return true;
        if (!message.Equals(GodHandFlavorMessage, StringComparison.Ordinal)) return false;
        // Match both the exact message and its originating frame, not a substring of a real exception.
        return detail.Split('\n').Any(line => line.Trim().Equals("当然，你可以在设置中关闭这条报错", StringComparison.Ordinal))
            && detail.Split('\n').Select(line => line.Trim()).Any(line =>
                line.Equals("at GodHandMod.FlavorErrorDisplayer.ShowFlavorError()", StringComparison.Ordinal)
                || line.StartsWith("at GodHandMod.FlavorErrorDisplayer.ShowFlavorError() [", StringComparison.Ordinal));
    }

    // The user explicitly approved these three external overlay configuration errors.
    // Validate the complete loader message and vendor/file pair; never open its path.
    internal static bool IsOverlayVulkanManifestError(string message)
    {
        const string prefix = "[Vulkan] [General] [Loader Message] loader_get_json: Failed to open JSON file ";
        if (message.StartsWith("[Verse] ", StringComparison.Ordinal)) message = message[8..];
        if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var path = message[prefix.Length..].Trim();
        if (path.Length > 1 && path[0] is '\"' or '\'' && path[^1] == path[0]) path = path[1..^1];
        if (path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) return false;
        var parts = path.Replace('/', '\\').Split('\\');
        if (parts.Any(p => p is "." or "..")) return false;
        var file = parts[^1];
        return (file.Equals("ow-vulkan-overlay64.json", StringComparison.OrdinalIgnoreCase)
                || file.Equals("ow-graphics-vulkan64.json", StringComparison.OrdinalIgnoreCase))
                && parts.Contains("Overwolf", StringComparer.OrdinalIgnoreCase)
            || file.Equals("EOSOverlayVkLayer-Win64.json", StringComparison.OrdinalIgnoreCase)
                && parts.Contains("Epic Online Services", StringComparer.OrdinalIgnoreCase);
    }
}
