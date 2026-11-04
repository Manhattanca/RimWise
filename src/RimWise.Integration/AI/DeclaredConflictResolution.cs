using RimWise.Core;

namespace RimWise.Integration.AI;

public sealed record ConflictRemovalApproval(string SelectedId, string[] RemovedIds)
{
    // Empty for checkpoints created before multiple selection was introduced.
    public string[] SelectedIds { get; init; } = [];
    public RuntimeConflictEvidence? RuntimeEvidence { get; init; }
    public string[] MissingIds { get; init; } = [];
    // Only a player decision may override retention. Replayed against current metadata.
    public string[] RetentionOverrideIds { get; init; } = [];
}
public sealed record ConflictRemovalOption(string Identifier, string Name, IReadOnlyList<string> RemovedIds,
    string? BlockedReason)
{
    public string[] RetentionOverrideIds { get; init; } = [];
    public bool CanOverrideRetention => BlockedReason is not null && RetentionOverrideIds.Length > 0;
}
public sealed record DeclaredConflict(string FirstId, string SecondId, string Reason,
    IReadOnlyList<ConflictRemovalOption> Options)
{
    public RuntimeConflictEvidence? RuntimeEvidence { get; init; }
    public string[] MissingIds { get; init; } = [];
    public IReadOnlyList<string> MemberIds => MissingIds.Length > 0 ? MissingIds : RuntimeEvidence?.Members ?? [FirstId, SecondId];
}

/// <summary>Only installed metadata can establish this conflict. Model prose and ordinary game errors cannot.</summary>
public static class DeclaredConflictResolution
{
    public static DeclaredConflict? Find(ModCatalogue catalogue, IReadOnlyList<string> order,
        IReadOnlyList<string> retained, GameVersion version)
    {
        var mods = order.ToDictionary(id => id, id => catalogue.Resolve(id, version).UniqueInstallation
            ?? throw new InvalidDataException("冲突检查需要唯一的已安装模组：" + id), StringComparer.OrdinalIgnoreCase);
        var packages = mods.GroupBy(item => item.Value.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, mod) in mods)
            foreach (var incompatible in mod.Metadata?.Requirements.IncompatibleWith ?? [])
            {
                if (!packages.TryGetValue(incompatible, out var other) || other.Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
                return new(id, other, $"{mod.Metadata?.Name ?? id} 声明与 {mods[other].Metadata?.Name ?? other} 不兼容；调整先后顺序无法消除该声明。",
                    Options(catalogue, order, retained, version, [id, other]));
            }
        return null;

    }

    internal static IReadOnlyList<ConflictRemovalOption> Options(ModCatalogue catalogue, IReadOnlyList<string> order,
        IReadOnlyList<string> retained, GameVersion version, IReadOnlyList<string> members)
    {
        var mods = order.ToDictionary(id => id, id => catalogue.Resolve(id, version).UniqueInstallation,
            StringComparer.OrdinalIgnoreCase);
        var protectedIds = AiOrderPlanner.DependencyClosure(retained.Concat(order.Where(id =>
            GameContentPolicy.KeepWhenClearing(id, version) || mods[id]?.Source == ModSource.Game)), catalogue, version);
        var protectedDirectories = protectedIds.Select(id => catalogue.Resolve(id, version).UniqueInstallation?.Directory)
            .Where(path => path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var official = AiOrderPlanner.DependencyClosure(order.Where(id =>
            GameContentPolicy.KeepWhenClearing(id, version) || mods[id]?.Source == ModSource.Game), catalogue, version);
        var officialDirectories = official.Select(id => catalogue.Resolve(id, version).UniqueInstallation?.Directory)
            .Where(path => path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedClosure = AiOrderPlanner.DependencyClosure(retained, catalogue, version);
        var retainedDirectories = retainedClosure.Select(id => catalogue.Resolve(id, version).UniqueInstallation?.Directory)
            .Where(path => path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directlyRetained = retained.Select(id => catalogue.Resolve(id, version).UniqueInstallation?.Directory)
            .Where(path => path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Protection(string id)
        {
            var mod = mods[id];
            var kind = retained.Contains(id, StringComparer.OrdinalIgnoreCase) || mod is not null && directlyRetained.Contains(mod.Directory) ? "已标记为必须保留"
                : retainedClosure.Contains(id) || mod is not null && (retainedClosure.Contains(mod.PackageId) || retainedDirectories.Contains(mod.Directory))
                    ? "必须保留模组的必要前置" : "游戏本体／DLC 或其必要前置";
            return $"{mod?.Metadata?.Name ?? id}（{id}）：{kind}";
        }
        return members.Select(selected =>
        {
            var removed = Dependents(selected, catalogue, order, version);
            var blocked = removed.Where(id => protectedIds.Contains(id) || mods[id] is { } mod
                && (protectedIds.Contains(mod.PackageId) || protectedDirectories.Contains(mod.Directory))).ToArray();
            var hardBlocked = removed.Any(id => official.Contains(id) || mods[id] is { } mod
                && (official.Contains(mod.PackageId) || officialDirectories.Contains(mod.Directory)));
            return new ConflictRemovalOption(selected, mods[selected]?.Metadata?.Name ?? selected, Array.AsReadOnly(removed),
                blocked.Length > 0 ? "移出此项会影响以下受保护模组。\n" + string.Join("\n", blocked.Select(Protection)) : null)
                { RetentionOverrideIds = hardBlocked ? [] : blocked };
        }).ToArray();
    }

    public static string[] Replay(ModCatalogue catalogue, IReadOnlyList<string> original,
        IReadOnlyList<string> retained, GameVersion version, IEnumerable<ConflictRemovalApproval> approvals)
    {
        var current = original.ToArray();
        foreach (var approval in approvals)
        {
            if (approval.RetentionOverrideIds is null)
                throw new InvalidDataException("保留保护覆盖记录缺失。");
            // Replay the same deterministic sequence and the exact displayed dependent set.
            var conflict = approval.MissingIds.Length > 0
                ? MissingModResolution.Find(catalogue, current, retained, version)
                : approval.RuntimeEvidence is { } evidence
                ? RuntimeConflictResolution.Restore(evidence, catalogue, current, retained, version)
                : Find(catalogue, current, retained, version);
            if (approval.MissingIds.Length > 0 && (conflict is null
                || !conflict.MissingIds.SequenceEqual(approval.MissingIds, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("缺失模组已变化，需要重新确认移出范围。");
            var selected = approval.SelectedIds.Length == 0 ? [approval.SelectedId] : approval.SelectedIds;
            var preview = conflict is null ? null : ConflictChoicePreview.Create(conflict, selected,
                allowRetainedRemoval: approval.RetentionOverrideIds.Length > 0);
            if (preview is not { CanContinue: true }
                || !preview.RemovedIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(approval.RemovedIds)
                || !preview.RetentionOverrideIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(approval.RetentionOverrideIds))
                throw new InvalidDataException("用户确认的冲突或连带影响已变化，需要重新选择。");
            current = current.Except(preview.RemovedIds, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return current;
    }

    private static string[] Dependents(string selected, ModCatalogue catalogue, IReadOnlyList<string> order, GameVersion version)
    {
        var mods = order.ToDictionary(id => id, id => catalogue.Resolve(id, version).UniqueInstallation!, StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, mod) in mods)
            foreach (var dependency in mod?.Metadata?.Requirements.Dependencies ?? [])
            {
                if (!dependents.TryGetValue(dependency.PackageId, out var ids)) dependents[dependency.PackageId] = ids = [];
                ids.Add(id);
            }
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selected };
        var pending = new Queue<string>([selected]);
        while (pending.TryDequeue(out var id))
        {
            var package = mods[id]?.PackageId ?? (id.EndsWith("_steam", StringComparison.OrdinalIgnoreCase) ? id[..^6] : id);
            if (!dependents.TryGetValue(package, out var ids)) continue;
            foreach (var dependent in ids)
                if (removed.Add(dependent)) pending.Enqueue(dependent);
        }
        return order.Where(removed.Contains).ToArray();
    }
}

internal sealed class MaintenancePauseException(string message) : InvalidOperationException(message);
