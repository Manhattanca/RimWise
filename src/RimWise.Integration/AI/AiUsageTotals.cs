namespace RimWise.Integration.AI;

/// <summary>Local billing evidence. Reservations are estimates, never provider-reported tokens.</summary>
public sealed record AiUsageTotals(long ReportedTokens, long UnreportedReservation,
    int UnknownTasks, int ActiveTasks, int DamagedTasks)
{
    public long BudgetOccupancy => checked(ReportedTokens + UnreportedReservation);

    public static AiUsageTotals Read(AiTaskRecoveryStore store)
    {
        var tasks = store.Inspect();
        var deleted = store.InspectDeletedUsage();
        return new(
            checked(tasks.Sum(t => (long)(t.Checkpoint?.ReportedTokens ?? 0)) + deleted.Sum(t => (long)t.ReportedTokens)),
            checked(tasks.Where(t => t.Checkpoint?.UnknownUsage == true).Sum(t => (long)t.Checkpoint!.ReservedTokens)
                + deleted.Where(t => t.UnknownUsage).Sum(t => (long)t.ReservedTokens)),
            tasks.Count(t => t.Checkpoint?.UnknownUsage == true) + deleted.Count(t => t.UnknownUsage),
            tasks.Count(t => t.Active), tasks.Count(t => t.Problem is not null));
    }

    public string Description => $"本机累计已回报 {ReportedTokens:N0} token"
        + (UnknownTasks > 0 ? $"；{UnknownTasks} 项未回报，预留 {UnreportedReservation:N0} token（估算）" : "")
        + (ActiveTasks > 0 ? $"；{ActiveTasks} 项正在执行，稍后更新" : "")
        + (DamagedTasks > 0 ? $"；{DamagedTasks} 项记录无法读取，累计不完整" : "");
}
