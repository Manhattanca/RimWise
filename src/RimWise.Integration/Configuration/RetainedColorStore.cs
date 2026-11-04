using RimWise.Formats;

namespace RimWise.Integration.Configuration;

/// <summary>Persistent color rules are separate from explicit per-mod retention and portable color metadata.</summary>
public sealed class RetainedColorStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredColors> file = new(applicationDirectory, "retained-colors");

    public HashSet<string> Load()
    {
        var stored = file.Load();
        if (stored is null) return new(StringComparer.OrdinalIgnoreCase);
        if (stored.Schema != 1 || stored.Colors is null) throw new InvalidDataException("必须保留颜色配置不受支持。");
        return Normalize(stored.Colors);
    }

    public void Save(IEnumerable<string> colors) => file.Save(new(1, Normalize(colors).Order(StringComparer.Ordinal).ToArray()));

    private static HashSet<string> Normalize(IEnumerable<string> colors)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var color in colors)
        {
            if (!OrderColors.IsColor(color)) throw new InvalidDataException("必须保留颜色格式无效。");
            result.Add(color.ToUpperInvariant());
            if (result.Count > 256) throw new InvalidDataException("必须保留颜色规则过多。");
        }
        return result;
    }

    private sealed record StoredColors(int Schema, string[] Colors);
}
