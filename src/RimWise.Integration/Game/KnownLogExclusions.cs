namespace RimWise.Integration.Game;

/// <summary>Explicitly approved non-failure messages. Never exclude a whole mod or error category.</summary>
internal static class KnownLogExclusions
{
    internal const string GodHandFlavorMessage = "我修复了大部分的报错，但是我保留了一部分，我觉得保留了一部分红字，才知道你玩的是RIMWORLD";

    internal static bool Matches(string message, string detail)
    {
        if (message.StartsWith("[Verse] ", StringComparison.Ordinal)) message = message[8..];
        if (!message.Equals(GodHandFlavorMessage, StringComparison.Ordinal)) return false;
        // Match both the exact message and its originating frame, not a substring of a real exception.
        return detail.Split('\n').Any(line => line.Trim().Equals("当然，你可以在设置中关闭这条报错", StringComparison.Ordinal))
            && detail.Split('\n').Select(line => line.Trim()).Any(line =>
                line.Equals("at GodHandMod.FlavorErrorDisplayer.ShowFlavorError()", StringComparison.Ordinal)
                || line.StartsWith("at GodHandMod.FlavorErrorDisplayer.ShowFlavorError() [", StringComparison.Ordinal));
    }
}
