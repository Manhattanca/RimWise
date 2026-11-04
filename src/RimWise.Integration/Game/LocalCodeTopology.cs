using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

internal sealed record LocalCodePreparation(LocalCodeReport Report, string[] PreferredOrder);

/// <summary>Assembly references supply a soft provider-first preference, never a hard
/// load-order rule. Ambiguous providers and cycles preserve the player's relative order.</summary>
internal static class LocalCodeTopology
{
    internal static LocalCodePreparation Prepare(ModCatalogue catalogue, IReadOnlyList<string> order,
        GameVersion version, string testRoot, CancellationToken token)
    {
        try
        {
            var index = LocalAssemblyIndex.Build(catalogue, order, version,
                ScopedPaths.Child(testRoot, "AnalysisCache"), token);
            var report = LocalCodePreflight.Inspect(index, token);
            // Partial graphs should enrich diagnostics but must not influence sorting.
            return new(report, index.Limited ? order.ToArray() : PreferProviders(index, order, token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return new(new(0, 0, true, []), order.ToArray()); }
    }

    internal static string[] PreferProviders(AssemblyIndexResult index, IReadOnlyList<string> order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (index.Limited) return order.ToArray();
        var positions = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
        var graph = order.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var providers = index.Assemblies.GroupBy(a => a.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var consumer in index.Assemblies)
        {
            token.ThrowIfCancellationRequested();
            if (!positions.ContainsKey(consumer.PackageId)) continue;
            foreach (var reference in consumer.Metadata.References)
                if (providers.TryGetValue(reference, out var matches) && matches.Length == 1
                    && graph.TryGetValue(matches[0].PackageId, out var targets)
                    && !matches[0].PackageId.Equals(consumer.PackageId, StringComparison.OrdinalIgnoreCase))
                    targets.Add(consumer.PackageId);
        }
        OrderingCycleFallback.DeferAdvisoryEdges(order.ToArray(), graph,
            order.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), token);
        var degrees = order.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var targets in graph.Values) foreach (var target in targets) degrees[target]++;
        var ready = new SortedSet<int>(order.Where(id => degrees[id] == 0).Select(id => positions[id]));
        var result = new List<string>(order.Count);
        while (ready.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var next = order[ready.Min]; ready.Remove(ready.Min); result.Add(next);
            foreach (var target in graph[next]) if (--degrees[target] == 0) ready.Add(positions[target]);
        }
        return result.Count == order.Count ? result.ToArray() : order.ToArray();
    }
}
