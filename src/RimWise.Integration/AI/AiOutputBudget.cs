namespace RimWise.Integration.AI;

internal static class AiOutputBudget
{
    internal static int? Next(MaintenanceLimits limits, int? previousLimit, bool exhausted, AiSettings? settings = null)
    {
        var current = previousLimit ?? limits.OutputTokensPerRequest;
        // Official Flash shares its allowance between reasoning and the final answer.
        // A measured 16K reasoning-only reply cannot be repaired by replaying the paid
        // request. Reserve a larger first allowance only for new, opted-in unbudgeted
        // tasks. Keep explicit smaller limits, old tasks, relays and other models intact.
        if (previousLimit is null && !exhausted && limits.ExpandOutputBudget && !limits.TokenLimitEnabled
            && current == 16384 && settings is not null
            && string.Equals(settings.Model.Trim(), "deepseek-flash", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == "https" && endpoint.IdnHost == "api.deepseek.com" && endpoint.IsDefaultPort)
            return 32768;
        if (!exhausted) return current;
        // Never resend the same paid attempt. Only a changed, freshly tested order
        // may reach this policy; at the ceiling continue local trials without AI.
        return limits.ExpandOutputBudget && current < 32768 ? Math.Min(32768, current * 2) : null;
    }
}
