namespace RimWise.Core;

/// <summary>Find strongly connected groups without recursion. Only advisory edges inside
/// a cyclic group may be deferred; required dependencies and official content stay intact.</summary>
public static class OrderingCycleFallback
{
    public static IReadOnlyList<string[]> DeferAdvisoryEdges(string[] order,
        Dictionary<string, HashSet<string>> edges, Dictionary<string, HashSet<string>> required,
        CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finished = new List<string>(order.Length);
        foreach (var root in order)
        {
            if (!visited.Add(root)) continue;
            var stack = new Stack<(string Id, IEnumerator<string> Targets)>();
            stack.Push((root, edges[root].GetEnumerator()));
            try
            {
                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = stack.Peek();
                    if (!frame.Targets.MoveNext())
                    {
                        stack.Pop().Targets.Dispose();
                        finished.Add(frame.Id);
                    }
                    else if (visited.Add(frame.Targets.Current))
                        stack.Push((frame.Targets.Current, edges[frame.Targets.Current].GetEnumerator()));
                }
            }
            finally { foreach (var frame in stack) frame.Targets.Dispose(); }
        }

        var reverse = order.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (from, targets) in edges)
            foreach (var to in targets) reverse[to].Add(from);
        visited.Clear();
        var groups = new List<string[]>();
        var positions = order.Select((id, index) => (id, index))
            .ToDictionary(p => p.id, p => p.index, StringComparer.OrdinalIgnoreCase);
        foreach (var root in finished.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(root)) continue;
            var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var id))
                foreach (var next in reverse[id])
                    if (visited.Add(next)) { group.Add(next); pending.Push(next); }
            if (group.Count == 1 && !edges[root].Contains(root)) continue;
            var removed = 0;
            foreach (var id in group)
                removed += edges[id].RemoveWhere(to => group.Contains(to) && !required[id].Contains(to));
            if (removed > 0) groups.Add(group.OrderBy(id => positions[id]).ToArray());
        }
        return groups;
    }
}
