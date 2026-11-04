using System.Globalization;
using System.Text.RegularExpressions;

namespace RimWise.Core;

public sealed record GameVersion(int Major, int Minor, int? Build = null)
{
    public string Series => $"{Major}.{Minor}";
    public bool UsesLegacyIdentifiers => Major == 0 || Major == 1 && Minor == 0;
    public override string ToString() => Build is { } build ? $"{Series}.{build}" : Series;

    public static GameVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512) return null;
        var match = Regex.Match(text.Trim(), @"^(?:[vV]|Version\s*[:=]?\s*)?(\d{1,3})\.(\d{1,3})(?:\.(\d{1,9})(?:\.\d{1,9})?)?(?:\s|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return null;
        return new GameVersion(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : null);
    }
}
