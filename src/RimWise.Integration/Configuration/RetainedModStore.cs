namespace RimWise.Integration.Configuration;

/// <summary>Stores the user's protected-mod markers separately from game files and order exports.</summary>
public sealed class RetainedModStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredRetention> file = new(applicationDirectory, "retained-mods");

    public HashSet<string> Load(string profile)
    {
        var settings = file.Load();
        if (settings is null) return new(StringComparer.OrdinalIgnoreCase);
        if (settings.SchemaVersion != 1 || settings.Profiles is null)
            throw new InvalidDataException("必须保留模组配置版本不受支持。");
        return settings.Profiles.TryGetValue(NormalizeProfile(profile), out var identifiers)
            ? identifiers.Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    public void Save(string profile, IEnumerable<string> identifiers)
    {
        var settings = file.Load() ?? new StoredRetention(1, new(StringComparer.OrdinalIgnoreCase));
        if (settings.SchemaVersion != 1 || settings.Profiles is null)
            throw new InvalidDataException("必须保留模组配置版本不受支持。");
        var profiles = new Dictionary<string, string[]>(settings.Profiles, StringComparer.OrdinalIgnoreCase)
        {
            [NormalizeProfile(profile)] = identifiers.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        file.Save(new StoredRetention(1, profiles));
    }

    private static string NormalizeProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return "default";
        try { return Path.GetFullPath(profile); }
        catch (ArgumentException) { return profile.Trim(); }
    }

    private sealed record StoredRetention(int SchemaVersion, Dictionary<string, string[]> Profiles);
}
