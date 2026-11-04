namespace RimWise.Integration.Configuration;

/// <summary>Stores user labels outside game and mod directories.</summary>
public sealed class ModTagStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredTags> file = new(applicationDirectory, "mod-tags", 4 * 1024 * 1024);

    public Dictionary<string, string[]> Load()
    {
        var stored = file.Load();
        if (stored is null) return new(StringComparer.OrdinalIgnoreCase);
        if (stored.SchemaVersion != 1 || stored.Tags is null)
            throw new InvalidDataException("模组标签配置版本不受支持。");
        return stored.Tags.Where(item => !string.IsNullOrWhiteSpace(item.Key) && item.Value is not null)
            .ToDictionary(item => Normalize(item.Key), item => NormalizeTags(item.Value), StringComparer.OrdinalIgnoreCase);
    }

    public void Save(IReadOnlyDictionary<string, string[]> tags)
    {
        var normalized = tags.Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Select(item => new { Key = Normalize(item.Key), Tags = NormalizeTags(item.Value) })
            .Where(item => item.Tags.Length > 0)
            .ToDictionary(item => item.Key, item => item.Tags, StringComparer.OrdinalIgnoreCase);
        file.Save(new StoredTags(1, normalized));
    }

    private static string[] NormalizeTags(IEnumerable<string> tags) => tags
        .Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20).ToArray();

    private static string Normalize(string value)
    {
        if (!Path.IsPathRooted(value) && !value.Contains(Path.DirectorySeparatorChar) && !value.Contains(Path.AltDirectorySeparatorChar))
            return value.Trim();
        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (ArgumentException) { return value.Trim(); }
    }

    private sealed record StoredTags(int SchemaVersion, Dictionary<string, string[]> Tags);
}
