namespace RimWise.Integration.AI;

public sealed record AiAction(
    string Kind,
    string? PackageId,
    int? TargetIndex,
    bool RequiredDependency,
    string? TargetPath,
    string? Reason)
{
    public string NormalizedKind => Kind?.Trim().ToLowerInvariant() ?? "";
}

public sealed record AiValidationResult(
    IReadOnlyList<AiAction> Accepted,
    IReadOnlyList<string> Rejected)
{
    public bool IsBlocked => Rejected.Count > 0;
}

public sealed record AiChangePreview(
    IReadOnlyList<AiAction> Actions,
    IReadOnlyList<string> Rejected,
    bool WritesFiles)
{
    public static AiChangePreview From(AiValidationResult validation) =>
        new(validation.Accepted, validation.Rejected, false);
}
