namespace RimWise.Integration.AI;

/// <summary>Opaque metadata keys, never paths or executable input. Membership and permissions
/// are checked separately by the action executor. Keep punctuation and legacy folder aliases.</summary>
public static class AiModIdentifier
{
    internal const int MaximumLength = 512;

    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumLength && !value.Any(char.IsControl)
        && value.IndexOfAny(['/', '\\', ':']) < 0;

    internal static bool IsValidOrEmpty(string? value) => value == "" || IsValid(value);
}
