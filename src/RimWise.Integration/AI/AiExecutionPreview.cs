namespace RimWise.Integration.AI;

/// <summary>Explicit boundary between AI suggestions and future mutating executors.</summary>
public sealed class AiExecutionPreview
{
    public AiChangePreview Create(AiValidationResult validation) => AiChangePreview.From(validation);
}
