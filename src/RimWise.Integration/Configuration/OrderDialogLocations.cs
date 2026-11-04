namespace RimWise.Integration.Configuration;

/// <summary>Navigation preferences only; never creates or writes a game directory.</summary>
public sealed class OrderDialogLocations(string profile)
{
    private sealed record Locations(Dictionary<string, string> Imports, Dictionary<string, string> Exports);
    private AppJsonFile<Locations> Store => new(profile, "order-dialog-locations", keepPreviousVersions: false);

    public string? Resolve(string kind, string configDirectory)
    {
        var state = Store.Load() ?? new(new(), new());
        string? saves = null;
        if (!string.IsNullOrWhiteSpace(configDirectory))
            saves = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(configDirectory)) ?? configDirectory, "Saves");
        var candidates = kind == "rws"
            ? new[] { saves, Get(state.Imports, kind) }
            : new[] { Get(state.Exports, kind), Get(state.Imports, kind), kind is "xml" or "all" ? configDirectory : null };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }

    public void Remember(string path, bool exported)
    {
        var kind = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (kind is not ("xml" or "json" or "rml" or "rws")) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var store = Store;
        var state = store.Load() ?? new(new(), new());
        // A malformed optional preference should never prevent a valid import/export.
        state = new(state.Imports ?? new(), state.Exports ?? new());
        var destinations = exported ? state.Exports : state.Imports;
        destinations[kind] = directory;
        destinations["all"] = directory;
        store.Save(state);
    }

    private static string? Get(Dictionary<string, string>? values, string kind)
        => values is not null && values.TryGetValue(kind, out var value) ? value : null;
}
