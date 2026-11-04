namespace RimWise.Integration.AI;

/// <summary>Deterministic removal among already validated choices. Never overrides protection.</summary>
internal static class AutomaticRemovalChoice
{
    internal static string[]? Select(DeclaredConflict conflict)
    {
        var allowed = conflict.Options.Where(o => o.BlockedReason is null)
            .OrderBy(o => o.RemovedIds.Count).ThenBy(o => o.Identifier, StringComparer.Ordinal).ToArray();
        if (conflict.MissingIds.Length == 0)
            return allowed.Select(o => new[] { o.Identifier })
                .FirstOrDefault(ids => ConflictChoicePreview.Create(conflict, ids).CanContinue);
        var selected = new List<string>();
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in allowed)
        {
            if (!conflict.MissingIds.Any(id => !removed.Contains(id) && option.RemovedIds.Contains(id, StringComparer.OrdinalIgnoreCase))) continue;
            selected.Add(option.Identifier);
            removed.UnionWith(option.RemovedIds);
            if (ConflictChoicePreview.Create(conflict, selected).CanContinue) return selected.ToArray();
        }
        return null;
    }
}
