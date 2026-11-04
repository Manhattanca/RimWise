namespace RimWise.Integration.AI;

/// <summary>A new installation needs a new task context, with the old usage retained.</summary>
public static class DuplicateCopyRetry
{
    public static MaintenanceLimits? Remaining(MaintenanceLimits limits, AiTaskCheckpoint task)
    {
        if (task.Discarded || task.HasUnresolvedRequest || task.Attempts == 0) return null;
        var attempts = limits.AttemptLimitEnabled ? limits.MaximumAttempts - task.Attempts : limits.MaximumAttempts;
        var tokens = limits.TokenLimitEnabled ? (long)limits.TokenBudget - task.ReportedTokens - task.ReservedTokens : limits.TokenBudget;
        if (limits.AttemptLimitEnabled && attempts < 1 || limits.TokenLimitEnabled && tokens < 100) return null;
        return limits with { MaximumAttempts = attempts, TokenBudget = checked((int)tokens) };
    }

    public static void Retire(AiTaskRecoveryStore.AiTaskLease task)
    {
        if (task.Current.HasUnresolvedRequest) throw new InvalidOperationException("请求尚未完成，不能更换任务副本。");
        // Preserve immutable usage and feedback; only prevent replay with a now-different installation.
        task.Save(task.Current with { Discarded = true, Message = "用户确认重新选择模组副本；旧任务保留用量，不再恢复。" });
    }
}
