namespace RimWise.Integration.AI;

internal static class AiOutputBudget
{
    internal static int? Next(MaintenanceLimits limits, int? previousLimit, bool exhausted)
    {
        var current = previousLimit ?? limits.OutputTokensPerRequest;
        if (!exhausted) return current;
        // Never resend the same paid attempt. Only a changed, freshly tested order
        // may reach this policy; at the ceiling continue local trials without AI.
        return limits.ExpandOutputBudget && current < 32768 ? Math.Min(32768, current * 2) : null;
    }
}
