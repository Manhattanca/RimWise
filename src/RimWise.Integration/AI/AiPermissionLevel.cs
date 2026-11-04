namespace RimWise.Integration.AI;

/// <summary>Describes the maximum changes the AI may suggest for the current session.</summary>
public enum AiPermissionLevel
{
    Level1OrderAndRequiredDependencies = 1,
    Level2ModManagement = 2,
    Level3CodeAdaptation = 3
}
