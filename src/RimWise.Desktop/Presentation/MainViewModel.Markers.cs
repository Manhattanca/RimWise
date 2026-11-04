using System.IO;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Presentation;

public enum RecentModFilter { All, Added, Updated, Active }

public sealed partial class MainViewModel
{
    private readonly ModActivityStore activityStore = new(UserStorage.DirectoryPath);
    private readonly ModTagStore tagStore = new(UserStorage.DirectoryPath);
    private Dictionary<string, ModActivityRecord> activity = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string[]> markerTags = new(StringComparer.OrdinalIgnoreCase);
    private RecentModFilter recentFilter;

    public RecentModFilter RecentFilter
    {
        get => recentFilter;
        private set
        {
            if (recentFilter == value) return;
            recentFilter = value;
            VisibleRows.Refresh();
            VisibleAvailableMods.Refresh();
            OnPropertyChanged();
            RefreshCounts();
        }
    }

    public RelayCommand BatchMarkCommand { get; private set; } = null!;
    public IReadOnlyList<string> RetainedPackageIds => retainedIdentifiers.Concat(markerColors
        .Where(pair => retainedColors.Contains(pair.Value)).Select(pair => pair.Key))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string SelectedMarkerCountLabel
    {
        get
        {
            var count = SelectedOrderRows.Count + SelectedAvailableRows.Count;
            return count == 0 ? "未选择模组" : $"已选择 {count} 个模组";
        }
    }

    private void InitializeMarkers()
    {
        InitializeColors();
        InitializeColorRetention();
        try { markerTags = tagStore.Load(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Status = "标签记录读取失败，已使用空标签：" + ex.Message; }
        BatchMarkCommand = new RelayCommand(() => { }, () => SelectedOrderRows.Count > 0 || SelectedAvailableRows.Count > 0);
    }

    public void SetRecentFilter(RecentModFilter filter) => RecentFilter = filter;

    internal bool MatchesRecent(LoadOrderRow row) => MatchesRecent(row.IsRecentlyAdded, row.IsRecentlyUpdated);
    internal bool MatchesRecent(InstalledModRow row) => MatchesRecent(row.IsRecentlyAdded, row.IsRecentlyUpdated);

    private bool MatchesRecent(bool added, bool updated) => RecentFilter switch
    {
        RecentModFilter.Added => added,
        RecentModFilter.Updated => updated,
        RecentModFilter.Active => added || updated,
        _ => true
    };

    public void ApplyBatchMarkers(bool retain, bool? retainValue, string? tagToAdd, bool clearTags,
        IEnumerable<LoadOrderRow> orderRows, IEnumerable<InstalledModRow> availableRows)
    {
        var order = orderRows.Where(Rows.Contains).Distinct().ToArray();
        var available = availableRows.Where(AvailableMods.Contains).Distinct().ToArray();
        if (order.Length == 0 && available.Length == 0) return;
        var before = CaptureEditor();
        var names = order.Select(row => row.Name).Concat(available.Select(row => row.Name)).ToArray();
        var changed = false;

        if (retain && retainValue is { } nextRetain)
        {
            var identifiers = order.Select(row => row.PackageId).Concat(available.Select(row => row.PackageId))
                .Where(id => id.Length > 0).ToArray();
            foreach (var id in identifiers)
                if (retainedIdentifiers.Contains(id) != nextRetain)
                {
                    if (nextRetain) retainedIdentifiers.Add(id); else retainedIdentifiers.Remove(id);
                    changed = true;
                }
        }

        var keys = order.Select(MarkerKey).Concat(available.Select(MarkerKey)).ToArray();
        if (tagToAdd is not null || clearTags)
        {
            var normalizedTag = tagToAdd?.Trim() ?? "";
            if (normalizedTag.Length > 0 && normalizedTag.Length > 80)
                throw new ArgumentException("标签长度不能超过 80 个字符。", nameof(tagToAdd));
            foreach (var key in keys)
            {
                markerTags.TryGetValue(key, out var current);
                var tags = (current ?? []).ToList();
                if (clearTags) tags.Clear();
                if (normalizedTag.Length > 0 && !tags.Contains(normalizedTag, StringComparer.OrdinalIgnoreCase)) tags.Add(normalizedTag);
                if (tags.Count > 20) tags = tags.Take(20).ToList();
                if (tags.Count == 0) changed |= markerTags.Remove(key);
                else
                {
                    var next = tags.ToArray();
                    changed |= !markerTags.TryGetValue(key, out var old) || !old.SequenceEqual(next, StringComparer.OrdinalIgnoreCase);
                    markerTags[key] = next;
                }
            }
        }

        if (!changed) return;
        try
        {
            if (retain) retainedStore.Save(sourceLabel, retainedIdentifiers);
            if (tagToAdd is not null || clearTags) tagStore.Save(markerTags);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            try
            {
                if (retain) retainedStore.Save(sourceLabel, before.RetainedIdentifiers);
                if (tagToAdd is not null || clearTags) tagStore.Save(before.MarkerTags);
            }
            catch (Exception rollback) when (rollback is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
            RestoreInMemoryMarkers(before);
            Status = "批量标记未保存：" + ex.Message;
            return;
        }

        ApplyMarkerState();
        var operation = retain && retainValue == true ? "批量标记必须保留" : retain && retainValue == false ? "批量取消必须保留"
            : clearTags ? "批量清除标签" : "批量添加标签";
        AppendHistory(new(before, CaptureEditor(), $"{operation}（{names.Length} 个模组）",
            $"撤销{operation}（{names.Length} 个模组）"));
        Status = $"{operation}：{names.Length} 个模组";
    }

    private void RestoreInMemoryMarkers(EditorSnapshot snapshot)
    {
        retainedIdentifiers = snapshot.RetainedIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        markerTags = snapshot.MarkerTags.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        ApplyMarkerState();
    }

    private string MarkerKey(LoadOrderRow row) => row.Match?.UniqueInstallation?.Directory ?? row.PackageId;
    private string MarkerKey(InstalledModRow row) => row.Installation.Directory;

    private void ApplyMarkerState()
    {
        foreach (var row in Rows)
        {
            row.SetColor(markerColors.GetValueOrDefault(row.PackageId));
            row.SetTags(markerTags.TryGetValue(MarkerKey(row), out var tags) ? tags : []);
            SetActivity(row, row.Match?.UniqueInstallation?.Directory);
        }
        foreach (var row in AvailableMods)
        {
            row.SetColor(markerColors.GetValueOrDefault(row.PackageId));
            row.SetTags(markerTags.TryGetValue(MarkerKey(row), out var tags) ? tags : []);
            SetActivity(row, row.Installation.Directory);
        }
        // Activity keys are filled after catalogue rows are replaced; refresh the time sort once.
        ApplyRetainedState();
        ApplyAvailableSort();
        OnPropertyChanged(nameof(SelectedMarkerCountLabel));
        BatchMarkCommand?.Refresh();
    }

    private void SetActivity(LoadOrderRow row, string? directory)
    {
        var record = directory is not null && activity.TryGetValue(directory, out var found) ? found : null;
        row.SetActivity(IsRecent(record?.AddedUtc), IsUpdated(record), record?.AddedUtc, UpdateTime(record));
    }

    private void SetActivity(InstalledModRow row, string directory)
    {
        var record = activity.TryGetValue(directory, out var found) ? found : null;
        row.SetActivity(IsRecent(record?.AddedUtc), IsUpdated(record), record?.AddedUtc, UpdateTime(record));
    }

    private static DateTimeOffset? UpdateTime(ModActivityRecord? record) => record is not null
        && record.LastUpdatedUtc > DateTimeOffset.MinValue ? record.LastUpdatedUtc : null;

    private static bool IsUpdated(ModActivityRecord? record) => IsRecent(UpdateTime(record))
        && (record?.AddedUtc is null || record.LastUpdatedUtc > record.AddedUtc);

    private static bool IsRecent(DateTimeOffset? value) => value is { } timestamp
        && timestamp >= DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(30)) && timestamp <= DateTimeOffset.UtcNow;
}
