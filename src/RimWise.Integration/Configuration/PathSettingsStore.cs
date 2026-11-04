namespace RimWise.Integration.Configuration;

/// <summary>Stores path preferences independently from other application settings.</summary>
public sealed class PathSettingsStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredSettings> file = new(applicationDirectory, "paths");
    public string SettingsPath => file.SettingsPath;

    public GamePaths? Load()
    {
        var settings = file.Load();
        if (settings is null) return null;
        if (settings.SchemaVersion != 1 || settings.Paths is null)
            throw new InvalidDataException("路径配置版本不受支持。");
        return settings.Paths.Normalize();
    }

    public void Save(GamePaths paths) => file.Save(new StoredSettings(1, paths.Normalize()));
    private sealed record StoredSettings(int SchemaVersion, GamePaths? Paths);
}
