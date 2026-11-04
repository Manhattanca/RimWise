using RimWise.Integration.Configuration;

namespace RimWise.Integration.AI;

public sealed class AiSettingsStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredSettings> file = new(applicationDirectory, "ai-settings");
    public string SettingsPath => file.SettingsPath;
    public AiTaskRecoveryStore Tasks { get; } = new(applicationDirectory);

    public AiSettings Load()
    {
        var stored = file.Load();
        if (stored is null) return AiSettings.Default;
        if (stored.SchemaVersion != 1 || stored.Settings is null)
            throw new InvalidDataException("AI 配置版本不受支持。");
        return stored.Settings.Normalize();
    }

    public void Save(AiSettings settings) => file.Save(new StoredSettings(1, settings.Normalize()));

    private sealed record StoredSettings(int SchemaVersion, AiSettings? Settings);
}
