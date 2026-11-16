namespace RimWise.Integration.Marketplace;

/// <summary>One identity has immutable versions, ordered by numeric SemVer precedence.</summary>
public static class MarketplaceVersions
{
    public static IReadOnlyList<IReadOnlyList<MarketplaceEntry>> Group(IEnumerable<MarketplaceEntry> entries) => entries
        .GroupBy(entry => entry.Author + "/" + entry.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => (IReadOnlyList<MarketplaceEntry>)group.OrderByDescending(entry => entry.Version,
            Comparer<string>.Create(Compare)).ToArray()).ToArray();

    public static int Compare(string? left, string? right)
    {
        if (left == right) return 0;
        var a = (left ?? "").Split('-', 2); var b = (right ?? "").Split('-', 2);
        var an = a[0].Split('.'); var bn = b[0].Split('.');
        for (int i = 0; i < Math.Max(an.Length, bn.Length); i++)
        {
            var result = Number(i < an.Length ? an[i] : "0").CompareTo(Number(i < bn.Length ? bn[i] : "0"));
            if (result != 0) return result;
        }
        if (a.Length != b.Length) return a.Length == 1 ? 1 : -1;
        if (a.Length == 1) return string.CompareOrdinal(left, right);
        var ap = a[1].Split('.'); var bp = b[1].Split('.');
        for (int i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            bool numericA = ap[i].All(char.IsAsciiDigit), numericB = bp[i].All(char.IsAsciiDigit);
            int result = numericA && numericB ? Number(ap[i]).CompareTo(Number(bp[i]))
                : numericA != numericB ? numericA ? -1 : 1 : string.CompareOrdinal(ap[i], bp[i]);
            if (result != 0) return result;
        }
        return ap.Length.CompareTo(bp.Length);
    }

    private static System.Numerics.BigInteger Number(string value) =>
        System.Numerics.BigInteger.TryParse(value, out var number) ? number : 0;
}
