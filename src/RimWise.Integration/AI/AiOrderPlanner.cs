using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Core;

namespace RimWise.Integration.AI;

public sealed class AiOrderPlan
{
    public IReadOnlyList<string> Before { get; }
    public IReadOnlyList<string> After { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<string> Rejected { get; }
    public IReadOnlyList<string> Notices { get; }
    // Only the validated planner can mint plans; callers cannot mutate the approved result.
    internal AiOrderPlan(string[] before, string[] after, string fingerprint, string[] rejected, string[] notices)
    {
        Before = Array.AsReadOnly((string[])before.Clone()); After = Array.AsReadOnly((string[])after.Clone());
        Fingerprint = fingerprint; Rejected = Array.AsReadOnly((string[])rejected.Clone()); Notices = Array.AsReadOnly((string[])notices.Clone());
    }
    public bool CanApply => Rejected.Count == 0 && !Before.SequenceEqual(After, StringComparer.Ordinal);
}

/// <summary>Sequential, all-or-nothing planning against trusted catalogue metadata.</summary>
public static class AiOrderPlanner
{
    /// <summary>Keep trial-excluded mods without discarding the reorders made before or during testing.
    /// The reduced combination remains the only tested result; this full order is validated, not test-certified.</summary>
    public static AiOrderPlan RetainedTrialOrder(IReadOnlyList<string> original, IReadOnlyList<string> tested,
        ModCatalogue catalogue, IReadOnlyList<string> retained, GameVersion version,
        IReadOnlyList<CustomOrderRule> rules, IReadOnlyList<ConflictRemovalApproval> approvals,
        IReadOnlyList<TrialExclusion> trials, ModCatalogue? validationCatalogue = null)
    {
        var verified = ConfirmedTrialOrder(original, tested, catalogue, retained, version, rules, approvals,
            trials, true, validationCatalogue);
        if (verified.Rejected.Count > 0) return verified;
        var full = tested.ToArray();
        foreach (var trial in trials.Reverse())
        {
            var removed = trial.RemovedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var template = trial.TestedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var survivors = new Queue<string>(full.Where(template.Contains));
            // Reinsert excluded rows in their previous slots, preserving the latest relative order
            // of survivors. Dependencies added later remain present and are sorted below.
            full = trial.TestedOrder.Select(id => removed.Contains(id) ? id : survivors.Dequeue())
                .Concat(full.Where(id => !template.Contains(id))).ToArray();
        }
        var constraints = validationCatalogue ?? catalogue;
        full = ExpandRequiredOrder(full, constraints, version);
        var sorted = new LoadOrderSorter().Sort(constraints, full, version, version, rules, deferAdvisoryCycles: true);
        return PreparedOrder(original, sorted.Order, catalogue, retained, version, rules, approvals, constraints);
    }

    /// <summary>Final publication of an experimentally tested combination requires a separate player decision.</summary>
    public static AiOrderPlan ConfirmedTrialOrder(IReadOnlyList<string> original, IReadOnlyList<string> target,
        ModCatalogue catalogue, IReadOnlyList<string> retained, GameVersion version,
        IReadOnlyList<CustomOrderRule> rules, IReadOnlyList<ConflictRemovalApproval> approvals,
        IReadOnlyList<TrialExclusion> trials, bool playerApproved, ModCatalogue? validationCatalogue = null)
    {
        if (!playerApproved || trials.Count == 0) throw new InvalidOperationException("试排除结果尚未获得玩家确认。");
        return TrialExclusionPlanning.Plan(original, target, catalogue, retained, version, rules, trials, approvals, validationCatalogue);
    }

    /// <summary>Trusted preparation/result path. Removal authority comes only from replayed user decisions,
    /// never from model actions or an elevated permission level. Large orders need no per-row AI actions.</summary>
    public static AiOrderPlan PreparedOrder(IReadOnlyList<string> original, IReadOnlyList<string> target,
        ModCatalogue catalogue, IReadOnlyList<string> retained, GameVersion version,
        IReadOnlyList<CustomOrderRule> rules, IReadOnlyList<ConflictRemovalApproval> approvals,
        ModCatalogue? validationCatalogue = null)
    {
        var constraints = validationCatalogue ?? catalogue;
        var expanded = ExpandRequiredOrder(original, constraints, version, allowMissing: true);
        var allowed = DeclaredConflictResolution.Replay(constraints, expanded, retained, version, approvals);
        // All missing entries must have explicit, revalidated user approval before strict planning.
        ExpandRequiredOrder(allowed, constraints, version);
        var rejected = new List<string>();
        var deferred = new List<string>();
        if (target.Count > 20000 || target.Distinct(StringComparer.OrdinalIgnoreCase).Count() != target.Count
            || !allowed.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(target))
            rejected.Add("候选排序包含非必要新增项或未经用户确认的移出项。");
        if (rejected.Count == 0)
        {
            var verified = new LoadOrderSorter().Validate(constraints, target, version, version, rules, deferAdvisoryCycles: true);
            deferred.AddRange(verified.Diagnostics.Where(d => d.Code is "OrderingCycleDeferred" or "InsertionOrderAdjusted").Select(d => d.Message).Distinct());
            if (!verified.Complete || verified.Changed) rejected.Add(verified.FailureReason ?? "候选排序仍违反先后关系。");
        }
        var notices = original.Except(target, StringComparer.OrdinalIgnoreCase).Select(id => "用户选择移出排序：" + id + "（安装文件保留）")
            .Concat(target.Except(original, StringComparer.OrdinalIgnoreCase).Select(id => "加入必要前置：" + id)).Concat(deferred).ToArray();
        return new(original.ToArray(), rejected.Count == 0 ? target.ToArray() : original.ToArray(),
            Fingerprint(original, catalogue, retained, rules, version), rejected.ToArray(), notices);
    }

    /// <summary>Keep each active installation's identifier (including _steam) while adding required installations once.</summary>
    public static string[] ExpandRequiredOrder(IReadOnlyList<string> original, ModCatalogue catalogue, GameVersion version,
        bool allowMissing = false)
    {
        var result = original.ToList();
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in original)
        {
            var match = catalogue.Resolve(id, version);
            if (allowMissing && match.Candidates.Count == 0) { packages.Add(id); continue; }
            var mod = match.UniqueInstallation ?? throw new InvalidDataException("模组未安装或安装不唯一：" + id);
            if (!directories.Add(mod.Directory)) throw new InvalidDataException("排序含同一安装的重复标识。");
            packages.Add(mod.PackageId);
        }
        for (var index = 0; index < result.Count; index++)
        {
            var mod = catalogue.Resolve(result[index], version).UniqueInstallation;
            if (allowMissing && mod is null) continue;
            foreach (var dependency in (mod ?? throw new InvalidDataException("模组未安装：" + result[index])).Metadata?.Requirements.Dependencies ?? [])
            {
                if (packages.Contains(dependency.PackageId)) continue;
                var match = catalogue.Resolve(dependency.PackageId, version);
                if (allowMissing && match.Candidates.Count == 0)
                {
                    packages.Add(dependency.PackageId); result.Add(dependency.PackageId);
                    if (result.Count > 20000) throw new InvalidDataException("排序超过支持的记录数量。");
                    continue;
                }
                var required = match.UniqueInstallation ?? throw new InvalidDataException("必要前置未安装或安装不唯一：" + dependency.PackageId);
                if (!directories.Add(required.Directory)) throw new InvalidDataException("前置安装包含重复标识。");
                packages.Add(required.PackageId);
                result.Add(catalogue.IdentifierFor(required, version));
                if (result.Count > 20000) throw new InvalidDataException("排序超过支持的记录数量。");
            }
        }
        return result.ToArray();
    }

    public static string Fingerprint(IEnumerable<string> order, ModCatalogue catalogue, IEnumerable<string> retained,
        IEnumerable<CustomOrderRule> rules, GameVersion? version) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Order = order.ToArray(), Version = version?.ToString(),
            Retained = retained.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Rules = rules.ToArray(),
            Mods = catalogue.Installations.Select(mod => new { mod.Directory, mod.FolderName, mod.PackageId, mod.Source, mod.Metadata }).ToArray() })));

    public static AiOrderPlan Plan(IEnumerable<AiAction> actions, AiPermissionLevel level, IReadOnlyList<string> order,
        ModCatalogue catalogue, IReadOnlyList<string> retained, GameVersion? version,
        IReadOnlyList<CustomOrderRule>? customRules = null)
    {
        var rules = customRules ?? [];
        var original = order.ToArray();
        var current = order.ToList();
        var rejected = new List<string>();
        var notices = new List<string>();
        if (!Enum.IsDefined(level)) rejected.Add("权限级别无效。");
        if (version is null) rejected.Add("游戏版本未知，请先确认目标版本。");
        if (order.Count > 20000 || order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != order.Count)
            rejected.Add("当前排序过大或含重复标识，请先修正。");
        var protectedIds = new HashSet<string>(retained, StringComparer.OrdinalIgnoreCase);
        foreach (var id in original)
            if (GameContentPolicy.KeepWhenClearing(id, version) || Resolve(id)?.Source == ModSource.Game) protectedIds.Add(id);
        foreach (var id in DependencyClosure(protectedIds, catalogue, version)) protectedIds.Add(id);
        var protectedDirectories = protectedIds.Select(ResolveDependency).Where(mod => mod is not null)
            .Select(mod => mod!.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = DependencyClosure(original, catalogue, version);
        var list = actions.Take(501).ToArray();
        if (list.Length > 500) rejected.Add("动作数量超过 500。");
        foreach (var action in list.Take(500))
        {
            var id = action.PackageId?.Trim() ?? "";
            if (id.Length is 0 or > AiModIdentifier.MaximumLength || id.Any(char.IsControl)) { rejected.Add("动作的模组标识无效。"); continue; }
            if (action.TargetPath is { Length: > 0 }) { rejected.Add($"排序动作不得指定文件路径：{id}"); continue; }
            var index = current.FindIndex(item => item.Equals(id, StringComparison.OrdinalIgnoreCase));
            switch (action.NormalizedKind)
            {
                case "move": case "reorder":
                    if (index < 0 || action.TargetIndex is null or < 1 || action.TargetIndex > current.Count)
                    { rejected.Add($"无效的排序位置：{id}"); break; }
                    var existingId = current[index];
                    current.RemoveAt(index); current.Insert(action.TargetIndex.Value - 1, existingId);
                    break;
                case "add": case "addmod":
                    if (index >= 0) { rejected.Add($"模组已启用：{id}"); break; }
                    if (Resolve(id) is null) { rejected.Add($"需要先安装或解决多份安装：{id}"); break; }
                    if (level == AiPermissionLevel.Level1OrderAndRequiredDependencies && !required.Contains(id) && !required.Contains(Resolve(id)!.PackageId))
                    { rejected.Add($"元数据未证实这是必要前置：{id}"); break; }
                    if (action.TargetIndex is < 1 || action.TargetIndex > current.Count + 1)
                    { rejected.Add($"新增位置无效：{id}"); break; }
                    current.Insert((action.TargetIndex ?? (current.Count + 1)) - 1, id);
                    notices.Add($"加入排序：{id}（安装文件不变）");
                    break;
                case "remove": case "removemod":
                    if (level == AiPermissionLevel.Level1OrderAndRequiredDependencies || protectedIds.Contains(id)
                        || (Resolve(id) is { } protectedMod && protectedDirectories.Contains(protectedMod.Directory)))
                    { rejected.Add($"等级或保留依赖保护禁止移出：{id}"); break; }
                    if (index < 0) { rejected.Add($"模组未启用：{id}"); break; }
                    current.RemoveAt(index); notices.Add($"移出排序：{id}（安装文件保留）");
                    break;
                default: rejected.Add($"排序执行器不接受此动作：{action.Kind}"); break;
            }
        }
        foreach (var id in original.Where(protectedIds.Contains))
            if (!current.Contains(id, StringComparer.OrdinalIgnoreCase)) rejected.Add($"受保护内容缺失：{id}");
        var activePackages = current.Select(id => Resolve(id)?.PackageId ?? id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in current)
        {
            var mod = Resolve(id);
            if (mod is null) { rejected.Add($"模组未安装或安装不唯一：{id}"); continue; }
            foreach (var dep in mod?.Metadata?.Requirements.Dependencies ?? [])
                if (!activePackages.Contains(dep.PackageId)) rejected.Add($"{id} 缺少启用的前置：{dep.PackageId}");
        }
        if (current.Select(id => Resolve(id)?.Directory ?? id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Count)
            rejected.Add("排序含同一安装的重复标识。");
        // Validate the proposed result against the same trusted graph as the
        // automatic preparation, including deferred advisory cycles. A separate
        // pairwise check would reject the same cycles again at apply time.
        if (rejected.Count == 0)
        {
            var verified = new LoadOrderSorter().Validate(catalogue, current, version, version, rules, deferAdvisoryCycles: true);
            notices.AddRange(verified.Diagnostics.Where(d => d.Code is "OrderingCycleDeferred" or "InsertionOrderAdjusted")
                .Select(d => d.Message).Distinct());
            if (!verified.Complete) rejected.Add(verified.FailureReason ?? "无法验证 AI 排序结果。");
            else if (verified.Changed) rejected.Add("AI 排序仍违反必要前置、模组声明或自定义规则的先后关系，请重新生成建议。");
        }
        return new(original, rejected.Count == 0 ? current.ToArray() : original,
            Fingerprint(original, catalogue, retained, rules, version), rejected.Distinct().ToArray(), notices.ToArray());

        ModInstallation? Resolve(string id) => catalogue.Resolve(id, version).UniqueInstallation;
        ModInstallation? ResolveDependency(string id) => Resolve(id) ?? (catalogue.FindPackage(id) is { Count: 1 } candidates ? candidates[0] : null);
    }

    public static HashSet<string> DependencyClosure(IEnumerable<string> seeds, ModCatalogue catalogue, GameVersion? version)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(seeds);
        while (pending.TryDequeue(out var id))
        {
            if (!visited.Add(id)) continue;
            var mod = catalogue.Resolve(id, version).UniqueInstallation
                ?? (catalogue.FindPackage(id) is { Count: 1 } candidates ? candidates[0] : null);
            if (mod?.PackageId is { Length: > 0 } package) visited.Add(package);
            foreach (var dep in mod?.Metadata?.Requirements.Dependencies ?? [])
                pending.Enqueue(dep.PackageId);
        }
        return visited;
    }
}
