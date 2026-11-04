namespace RimWise.Integration.AI;

public sealed record AiUsageEvidence(AiUsageRequest[] Requests, AiUsagePeriod[] Periods, int IncompleteTasks)
{
    public static AiUsageEvidence Read(AiTaskRecoveryStore store)
    {
        var tasks = store.InspectUsage();
        var deleted = store.InspectDeletedUsage();
        return new(tasks.SelectMany(t => t.Checkpoint?.UsageRequests ?? []).Concat(deleted.SelectMany(t => t.UsageRequests)).ToArray(),
            tasks.SelectMany(t => t.Checkpoint?.UsagePeriods ?? []).Concat(deleted.SelectMany(t => t.UsagePeriods)).ToArray(),
            tasks.Count(t => t.Problem is not null));
    }

    public string Describe(string scope)
    {
        var periods = Periods.Where(p => p.Scope == scope).OrderBy(p => p.StartedUtc).ToArray();
        var requests = Requests.Where(r => r.Scope == scope).ToArray();
        if (periods.Length == 0) return "此密钥暂无已记录的 AI 排序任务时间；旧记录不推测归属。";
        var latest = periods[^1];
        var sums = requests.Select(r => r.Kind).Distinct().Select(kind =>
        {
            var own = requests.Where(r => r.Kind == kind).ToArray();
            return $"{(kind == AiTaskKind.Maintenance ? "自动排序并测试" : kind == AiTaskKind.Sorting ? "AI 排序" : "连接检查")}：{own.Sum(r => (long)(r.Usage?.Total ?? 0)):N0} token"
                + (own.Any(r => r.Usage is null) ? $"，{own.Count(r => r.Usage is null)} 次用量待确认" : "");
        });
        return string.Join("\n", sums) + $"\n最近任务：{latest.StartedUtc.LocalDateTime:G} — "
            + (latest.EndedUtc is { } end ? end.LocalDateTime.ToString("G") : "进行中或中断，结束时间未确认")
            + (IncompleteTasks > 0 ? "\n部分时间记录无法读取。" : "");
    }
}
