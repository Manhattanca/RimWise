namespace RimWise.Integration.Configuration;

public enum ModBadgeDisplay { Icons, Text }

public sealed record ListDisplaySettings(ModBadgeDisplay Badges = ModBadgeDisplay.Icons,
    double DetailsWidth = 0.95, double AvailableWidth = 1, double OrderWidth = 1.15,
    int AvailableSource = 0, int OrderSource = 0, double RowHeight = 38);

public sealed class ListDisplaySettingsStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredSettings> file = new(applicationDirectory, "list-display");
    public string SettingsPath => file.SettingsPath;
    private sealed record StoredSettings(int SchemaVersion, ListDisplaySettings Display);

    public ListDisplaySettings Load()
    {
        var stored = file.Load();
        if (stored is null) return new();
        if (stored.SchemaVersion != 1) throw new InvalidDataException("列表设置版本无效。");
        Validate(stored.Display);
        return stored.Display;
    }

    public void Save(ListDisplaySettings settings)
    {
        Validate(settings);
        file.Save(new(1, settings));
    }

    private static void Validate(ListDisplaySettings settings)
    {
        if (settings is null || !Enum.IsDefined(settings.Badges)
            || settings.AvailableSource is < 0 or > 2 || settings.OrderSource is < 0 or > 2
            || !double.IsFinite(settings.RowHeight) || settings.RowHeight is < 28 or > 64
            || new[] { settings.DetailsWidth, settings.AvailableWidth, settings.OrderWidth }
                .Any(width => !double.IsFinite(width) || width < 0.001 || width > 10_000))
            throw new InvalidDataException("列表显示设置无效。");
    }
}
