namespace RimWise.Integration.AI;

/// <summary>A preview grants no execution permission. The controller checks and journals the selection again.</summary>
public sealed record ConflictChoicePreview(bool CanContinue, IReadOnlyList<string> SelectedIds,
    IReadOnlyList<string> RemovedIds, string Message)
{
    public string[] RetentionOverrideIds { get; init; } = [];
    public static ConflictChoicePreview Create(DeclaredConflict conflict, IReadOnlyList<string> selected,
        bool allowRetainedRemoval = false)
    {
        var ids = selected.ToArray();
        var options = conflict.Options.Where(o => ids.Contains(o.Identifier, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (ids.Length == 0)
            return new(false, ids, [], "请选择需要移出的模组；其余模组保留。当前组合尚未改变。");
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length || options.Length != ids.Length)
            return new(false, ids, [], "选择包含不属于当前冲突组合的模组。");
        if (options.Any(o => o.BlockedReason is not null && (!allowRetainedRemoval || !o.CanOverrideRetention)))
            return new(false, ids, [], "所选移出项涉及必须保留的模组或前置，请重新选择。");
        var removed = options.SelectMany(o => o.RemovedIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var overrides = options.SelectMany(o => o.RetentionOverrideIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (conflict.MissingIds.Length > 0)
        {
            if (conflict.MissingIds.Any(id => !removed.Contains(id, StringComparer.OrdinalIgnoreCase)))
                return new(false, ids, removed, "仍有未安装的模组；请确认所有缺失项的移出，或停止任务后完成下载。");
            return new(true, ids, removed, "已确认移出缺失模组及其依赖项，可以继续排序；安装文件与订阅保持不变。")
                { RetentionOverrideIds = overrides };
        }
        if (!conflict.MemberIds.Any(id => removed.Contains(id, StringComparer.OrdinalIgnoreCase)))
            return new(false, ids, [], "当前组合尚未改变。");
        return new(true, ids, removed, "已打散当前组合，可以尝试继续测试；是否解决报错以复测结果为准。"
            + (overrides.Length == 0 ? "" : " 本次选择将覆盖所选项及连带项的必须保留保护。"))
            { RetentionOverrideIds = overrides };
    }

    internal ConflictRemovalApproval Approve(DeclaredConflict conflict) => CanContinue
        ? new(SelectedIds[0], RemovedIds.ToArray())
        { SelectedIds = SelectedIds.ToArray(), RuntimeEvidence = conflict.RuntimeEvidence?.Copy(), MissingIds = conflict.MissingIds.ToArray(),
            RetentionOverrideIds = RetentionOverrideIds.ToArray() }
        : throw new InvalidDataException("冲突选择尚未通过校验。");
}
