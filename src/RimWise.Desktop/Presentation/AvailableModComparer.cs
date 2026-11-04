using System.Collections;

namespace RimWise.Desktop.Presentation;

public enum AvailableModSortField { Name, PackageId, Author, RecentlyAdded, RecentlyUpdated, ModFormat }

/// <summary>Orders library rows for display without changing the enabled load order.</summary>
internal sealed class AvailableModComparer(AvailableModSortField field, bool descending)
    : IComparer, IComparer<InstalledModRow>
{
    // Pinyin is cached per row; package IDs and final ties keep stable ordinal comparison.
    private readonly StringComparer text = StringComparer.OrdinalIgnoreCase;

    public int Compare(InstalledModRow? left, InstalledModRow? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        if (field == AvailableModSortField.ModFormat)
        {
            var leftFormat = FormatRank(left);
            var rightFormat = FormatRank(right);
            // Unread or unknown types remain last in both directions. Within a
            // format group, names stay A–Z so reversing groups remains easy to scan.
            if ((leftFormat == int.MaxValue) != (rightFormat == int.MaxValue))
                return leftFormat == int.MaxValue ? 1 : -1;
            var formatResult = leftFormat.CompareTo(rightFormat);
            return formatResult == 0 ? CompareNames(left, right) : descending ? -formatResult : formatResult;
        }
        if (field is AvailableModSortField.RecentlyAdded or AvailableModSortField.RecentlyUpdated)
        {
            var leftTime = field == AvailableModSortField.RecentlyAdded ? left.AddedUtc : left.UpdatedUtc;
            var rightTime = field == AvailableModSortField.RecentlyAdded ? right.AddedUtc : right.UpdatedUtc;
            // Unknown history stays at the end; equal/unknown dates always fall back to A–Z names.
            if (leftTime.HasValue != rightTime.HasValue) return leftTime.HasValue ? -1 : 1;
            var timeResult = Nullable.Compare(leftTime, rightTime);
            if (timeResult != 0) return descending ? -Math.Sign(timeResult) : Math.Sign(timeResult);
            return CompareNames(left, right);
        }
        var leftKey = Key(left);
        var rightKey = Key(right);
        // Missing authors stay at the end in either direction, instead of looking like an author named “—”.
        var leftMissing = string.IsNullOrWhiteSpace(leftKey);
        var rightMissing = string.IsNullOrWhiteSpace(rightKey);
        if (leftMissing != rightMissing) return leftMissing ? 1 : -1;
        var result = text.Compare(leftKey, rightKey);
        if (result == 0) result = CompareNames(left, right);
        return descending ? -Math.Sign(result) : Math.Sign(result);
    }

    int IComparer.Compare(object? left, object? right) => Compare(left as InstalledModRow, right as InstalledModRow);

    private static int FormatRank(InstalledModRow row) => row.CodeKindIcon switch
    {
        "C#" => 0, "XML" => 1, "翻译" => 2, "资源" => 3, "混合" => 4, _ => int.MaxValue
    };

    private int CompareNames(InstalledModRow left, InstalledModRow right)
    {
        var result = text.Compare(left.NameSortKey, right.NameSortKey);
        if (result == 0) result = text.Compare(left.Name, right.Name);
        if (result == 0) result = text.Compare(left.PackageId, right.PackageId);
        if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(left.Installation.Directory, right.Installation.Directory);
        return Math.Sign(result);
    }

    private string Key(InstalledModRow row) => field switch
    {
        AvailableModSortField.PackageId => row.PackageId,
        AvailableModSortField.Author => row.Author,
        _ => row.NameSortKey
    };
}
