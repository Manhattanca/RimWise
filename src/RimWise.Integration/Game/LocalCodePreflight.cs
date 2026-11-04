using RimWise.Integration.AI;

namespace RimWise.Integration.Game;

internal sealed record LocalCodeReport(int AssemblyCount, int CacheHits, bool Limited,
    IReadOnlyList<AiAssemblyFact> Facts);

/// <summary>Deterministic metadata observations, never speculative ordering constraints.
/// Even a unique assembly provider is not evidence that it must load before its consumer.</summary>
internal static class LocalCodePreflight
{
    internal static LocalCodeReport Inspect(AssemblyIndexResult index, CancellationToken token)
    {
        var facts = new List<AiAssemblyFact>();
        var limited = index.Limited;
        var names = index.Assemblies.GroupBy(a => a.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        void Add(string kind, string owner, string related, string symbol)
        {
            if (facts.Count >= 512) { limited = true; return; }
            var fact = new AiAssemblyFact(kind, owner, related, symbol);
            if (AiLogContext.ValidAssemblyFact(fact) && !facts.Contains(fact)) facts.Add(fact);
        }
        foreach (var assembly in index.Assemblies)
        {
            token.ThrowIfCancellationRequested();
            foreach (var reference in assembly.Metadata.References.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!names.TryGetValue(reference, out var providers)) continue;
                foreach (var provider in providers.Where(p => p.PackageId != assembly.PackageId).Take(8))
                    Add(providers.Length == 1 ? "assembly-reference" : "ambiguous-reference",
                        assembly.PackageId, provider.PackageId, reference);
                if (providers.Length > 8) limited = true;
            }
        }
        foreach (var group in names.Values.Where(g => g.Length > 1))
        {
            token.ThrowIfCancellationRequested();
            var kind = group.Select(a => a.Sha256).Distinct().Count() == 1 ? "shared-assembly" : "different-assembly-images";
            foreach (var pair in Pairs(group.Select(a => a.PackageId).Distinct().Take(8).ToArray()))
                Add(kind, pair.Left, pair.Right, group[0].Metadata.Name);
            if (group.Length > 8) limited = true;
        }
        // Identical type names are observations, not proof of a runtime collision.
        foreach (var group in index.Assemblies.SelectMany(a => a.Metadata.Types.Select(t => (Type: t, Assembly: a)))
            .GroupBy(x => x.Type, StringComparer.Ordinal).Where(g => g.Select(x => x.Assembly.PackageId).Distinct().Skip(1).Any()))
        {
            token.ThrowIfCancellationRequested();
            if (facts.Count >= 512) { limited = true; break; }
            var owners = group.Select(x => x.Assembly.PackageId).Distinct().Take(9).ToArray();
            foreach (var pair in Pairs(owners.Take(8).ToArray())) Add("shared-type-name", pair.Left, pair.Right, group.Key);
            if (owners.Length > 8) limited = true;
        }
        return new(index.Assemblies.Length, index.CacheHits, limited,
            facts.OrderBy(f => f.Kind == "assembly-reference" ? 1 : 0).ThenBy(f => f.Kind, StringComparer.Ordinal)
                .ThenBy(f => f.PackageId, StringComparer.Ordinal).ThenBy(f => f.Symbol, StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<AiAssemblyFact> SelectRelated(LocalCodeReport report, IEnumerable<string> packages)
    {
        var relevant = packages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return report.Facts.Where(f => relevant.Contains(f.PackageId) || relevant.Contains(f.RelatedPackageId))
            .Take(16).ToArray();
    }

    private static IEnumerable<(string Left, string Right)> Pairs(string[] owners)
    {
        for (var i = 0; i < owners.Length; i++)
            for (var j = i + 1; j < owners.Length; j++) yield return (owners[i], owners[j]);
    }
}
