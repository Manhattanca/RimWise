namespace RimWise.Integration.Configuration;

public enum ThemePreference { System, Light, Dark }

public sealed class AppearanceSettingsStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredSettings> file = new(applicationDirectory, "appearance");
    public string SettingsPath => file.SettingsPath;

    public ThemePreference Load()
    {
        var settings = file.Load();
        if (settings is null) return ThemePreference.System;
        if (settings.SchemaVersion != 1 || !Enum.IsDefined(settings.Theme))
            throw new InvalidDataException("外观配置版本或主题无效。");
        return settings.Theme;
    }

    public void Save(ThemePreference preference)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        file.Save(new StoredSettings(1, preference));
    }

    private sealed record StoredSettings(int SchemaVersion, ThemePreference Theme);
}
