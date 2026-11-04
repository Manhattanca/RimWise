namespace RimWise.Core;

public sealed record OrderDifference(int Index, string Identifier, string Change)
{
    public string IndexLabel => Index == 0 ? "—" : Index.ToString();
}

public sealed class OrderComparison
{
    public IReadOnlyList<OrderDifference> Entries { get; }
    public int Added { get; }
    public int Removed { get; }
    public int Moved { get; }
    public int TargetCount { get; }

    public OrderComparison(LoadOrder before, LoadOrder after)
    {
        var oldPositions = Positions(before);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new List<OrderDifference>();
        foreach (var (id, index) in after.PackageIds.Select((id, index) => (id, index)))
        {
            var occurrence = seen.GetValueOrDefault(id);
            seen[id] = occurrence + 1;
            var existed = oldPositions.TryGetValue((id, occurrence), out var oldIndex);
            if (!existed) Added++;
            else if (oldIndex != index) Moved++;
            entries.Add(new(index + 1, id, !existed ? "新增" : oldIndex == index ? "保留" : $"原第 {oldIndex + 1} 项"));
        }
        Removed = before.PackageIds.Count - (after.PackageIds.Count - Added);
        var newPositions = Positions(after);
        foreach (var (key, index) in oldPositions)
            if (!newPositions.ContainsKey(key)) entries.Add(new(0, key.Id, $"移出（原第 {index + 1} 项）"));
        TargetCount = after.PackageIds.Count;
        Entries = entries.AsReadOnly();
    }

    private static Dictionary<(string Id, int Occurrence), int> Positions(LoadOrder order)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var positions = new Dictionary<(string, int), int>();
        for (var index = 0; index < order.PackageIds.Count; index++)
        {
            var id = order.PackageIds[index];
            var occurrence = seen.GetValueOrDefault(id);
            seen[id] = occurrence + 1;
            positions[(id, occurrence)] = index;
        }
        return positions;
    }
}
