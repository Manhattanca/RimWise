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
        // First setup starts with custom service; never replace a saved provider.
        if (stored is null) return AiSettings.Default with { Endpoint = "" };
        if (stored.SchemaVersion != 1 || stored.Settings is null)
            throw new InvalidDataException("AI 配置版本不受支持。");
        // The former opt-in switch is retired; credentials still gate actual requests.
        return (stored.Settings with { Enabled = true }).Normalize();
    }

    public void Save(AiSettings settings) => file.Save(new StoredSettings(1, (settings with { Enabled = true }).Normalize()));

    private sealed record StoredSettings(int SchemaVersion, AiSettings? Settings);
}
