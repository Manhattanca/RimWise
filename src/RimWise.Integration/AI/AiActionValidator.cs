namespace RimWise.Integration.AI;

/// <summary>Validates suggestions only. It never writes files or starts processes.</summary>
public sealed class AiActionValidator
{
    public AiValidationResult Validate(
        IEnumerable<AiAction> actions,
        AiPermissionLevel level,
        IEnumerable<string> currentOrder,
        IEnumerable<string> installedMods,
        IEnumerable<string> retainedMods)
    {
        var actionsList = actions.ToArray();
        var currentList = currentOrder.Where(IsIdentifier).ToArray();
        var current = currentList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = installedMods.Where(IsIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = retainedMods.Where(IsIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<AiAction>();
        var rejected = new List<string>();
        if (!Enum.IsDefined(level)) return new([], ["权限级别无效。"]);

        foreach (var action in actionsList.Take(500))
        {
            var kind = action.NormalizedKind;
            var packageId = action.PackageId?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(action.TargetPath)) { rejected.Add("排序动作不得指定文件路径。"); continue; }
            if (!IsIdentifier(packageId)) { rejected.Add("动作缺少有效模组标识。"); continue; }
            if (kind is "reorder" or "move")
            {
                if (!current.Contains(packageId)) { rejected.Add($"排序动作目标不在当前排序中：{packageId}"); continue; }
                if (action.TargetIndex is null or < 1 || action.TargetIndex > currentList.Length) { rejected.Add($"排序位置无效：{packageId}"); continue; }
                accepted.Add(action with { PackageId = packageId, TargetPath = null });
                continue;
            }
            if (kind is "add" or "addmod")
            {
                if (!installed.Contains(packageId)) { rejected.Add($"新增动作目标不是已安装模组：{packageId}"); continue; }
                if (level == AiPermissionLevel.Level1OrderAndRequiredDependencies && !action.RequiredDependency)
                { rejected.Add($"一级权限只允许添加必要前置：{packageId}"); continue; }
                accepted.Add(action with { PackageId = packageId, TargetPath = null });
                continue;
            }
            if (kind is "remove" or "removemod")
            {
                if (level == AiPermissionLevel.Level1OrderAndRequiredDependencies)
                { rejected.Add("一级权限禁止移除模组。"); continue; }
                if (!current.Contains(packageId)) { rejected.Add($"移除动作目标不在当前排序中：{packageId}"); continue; }
                if (retained.Contains(packageId)) { rejected.Add($"必须保留模组不得移除：{packageId}"); continue; }
                accepted.Add(action with { PackageId = packageId, TargetPath = null });
                continue;
            }
            if (kind is "modifycode" or "patch" or "localmod")
            {
                rejected.Add("代码修改尚未开放：独立审查、受控构建和恢复流程未完成验收。");
                continue;
            }
            rejected.Add($"不支持的 AI 动作：{action.Kind}");
        }

        if (actionsList.Length > 500) rejected.Add("AI 动作数量超过 500，已拒绝超出部分。");
        return new AiValidationResult(accepted, rejected);
    }

    private static bool IsIdentifier(string value) => AiModIdentifier.IsValid(value);
}
