using RimWise.Formats;

namespace RimWise.Integration.Configuration;

/// <summary>Package identities are portable across computers; installation paths are not.</summary>
public sealed class ModColorStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredColors> file = new(applicationDirectory, "mod-colors", 4 * 1024 * 1024);
    public Dictionary<string, string> Load()
    {
        var value = file.Load();
        if (value is null) return new(StringComparer.OrdinalIgnoreCase);
        if (value.Schema != 1 || value.Colors is null) throw new InvalidDataException("颜色标记配置不受支持。");
        return new(OrderColors.Normalize(value.Colors), StringComparer.OrdinalIgnoreCase);
    }
    public void Save(IReadOnlyDictionary<string, string> colors) => file.Save(new(1, new(OrderColors.Normalize(colors), StringComparer.OrdinalIgnoreCase)));
    private sealed record StoredColors(int Schema, Dictionary<string, string> Colors);
}
