using System.Text.Json;
using RimWise.Core;

namespace RimWise.Integration.AI;

/// <summary>Captured before adding rows. Keep originals; their order yields only to mod requirements.</summary>
public static class InsertionScope
{
    public static string Capture(IEnumerable<string> order) => JsonSerializer.Serialize(order.ToArray());

    public static string[] Read(string? json)
    {
        if (json is null) return [];
        if (json.Length > 6_000_000) throw new InvalidDataException("新增模式的原排序记录过大。");
        var order = JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidDataException("原排序记录缺失。");
        if (order.Length > 20000 || order.Any(id => !AiModIdentifier.IsValid(id))
            || order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != order.Length)
            throw new InvalidDataException("新增模式的原排序记录无效。");
        return order;
    }

    public static IReadOnlyList<CustomOrderRule> Rules(IReadOnlyList<CustomOrderRule> rules, string? json)
    {
        if (json is null) return rules;
        var order = Read(json);
        // Rebuild preferences from the captured baseline. Repeated database/preparation
        // projection must neither promote them to required edges nor duplicate them.
        return rules.Where(r => r.OriginalOrderIndex is null)
            .Concat(order.Zip(order.Skip(1), (a, b) => (a, b)).Select((p, index) =>
                new CustomOrderRule(p.a, p.b) { OriginalOrderIndex = index })).Distinct().ToArray();
    }

    public static void ValidateOrder(string? json, IEnumerable<string> candidate,
        IEnumerable<string>? approvedRemovals = null)
    {
        if (json is null) return;
        var approved = (approvedRemovals ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var original = Read(json).Where(id => !approved.Contains(id)).ToArray();
        var ids = original.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = candidate.Where(ids.Contains).ToArray();
        if (present.Length != original.Length || present.Distinct(StringComparer.OrdinalIgnoreCase).Count() != original.Length)
            throw new InvalidDataException("新增模式必须完整保留原有模组，不能移出或重复加入。");
        // Ordering is checked by the trusted sorter with the current catalogue/rules.
        // Recovery has no catalogue and checks membership only; publication revalidates.
    }
}
