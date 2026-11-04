using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using RimWise.Core;
using RimWise.Integration.Scanning;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private ModScanResult? scan;
    private InstalledModRow? selectedAvailable;
    private bool isScanning;
    private bool isShowingCache;
    private bool isEnrichingContent;
    public bool IsEnrichingContent
    {
        get => isEnrichingContent;
        internal set { isEnrichingContent = value; OnPropertyChanged(); }
    }
    public bool IsShowingCache
    {
        get => isShowingCache;
        set { isShowingCache = value; OnPropertyChanged(); OnPropertyChanged(nameof(GameVersionLabel)); RefreshCommands(); }
    }
    public SnapshotCollection<InstalledModRow> AvailableMods { get; } = [];
    public SnapshotCollection<DiagnosticRow> ScanIssues { get; } = [];
    public ICollectionView VisibleAvailableMods { get; private set; } = null!;
    public bool HasCatalogue => scan is not null;
    public GameVersion? CatalogueVersion => scan?.TargetVersion;
    public GameVersion? CurrentDetectedVersion => scan?.DetectedVersion.Version;
    public GameVersion? EffectiveGameVersion => CatalogueVersion ?? GameVersion.Parse(document?.GameVersion);
    public bool HasScanIssues => ScanIssues.Count > 0;
    public string ScanIssuesLabel => $"读取记录（{ScanIssues.Count}）";
    public string GameVersionLabel => (IsShowingCache ? "缓存 · " : "") + (scan is null ? "游戏版本：未读取" : scan.TargetVersion is null ? "游戏版本：未识别"
        : scan.DetectedVersion.Version == scan.TargetVersion ? $"游戏版本：{scan.DetectedVersion.Version}"
        : $"目标版本：{scan.TargetVersion}（检测：{scan.DetectedVersion.Version?.ToString() ?? "未知"}）");
    public bool IsScanning
    {
        get => isScanning;
        set { isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanActionLabel)); OnPropertyChanged(nameof(OrderStatusTooltip)); RefreshCommands(); }
    }
    public string ScanActionLabel => IsScanning ? "取消刷新" : "刷新";
    public InstalledModRow? SelectedAvailable
    {
        get => selectedAvailable;
        set
        {
            selectedAvailable = value;
            selectedAvailableRows = value is null ? [] : [value];
            if (value is not null)
            {
                selectedRow = null;
                selectedOrderRows = [];
                OnPropertyChanged(nameof(SelectedRow));
                OnPropertyChanged(nameof(SelectedOrderRows));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedAvailableRows));
            RefreshSelection();
            RefreshCommands();
        }
    }
    private ModInstallation? SelectedInstallation => selectedAvailable?.Installation ?? selectedRow?.Match?.UniqueInstallation;
    public string SelectedName => SelectedInstallation?.Name ?? selectedRow?.Name ?? "—";
    public string SelectedDescription => SelectedInstallation?.Metadata?.Description is { Length: > 0 } description
        ? description : selectedRow?.Match is { Candidates.Count: > 1 } ? "存在多份安装，请先确认模组来源。"
        : SelectedInstallation is null ? "选择模组以查看资料与简介" : "该模组未提供简介";
    public string SelectedSource => SelectedInstallation is { } mod ? ModDisplay.Source(mod.Source) : selectedRow?.SourceLabel ?? "—";
    public string SelectedContent => SelectedInstallation is { } mod ? ModDisplay.ContentDescription(mod.ContentKinds) : "";
    public bool HasSelectedContent => SelectedContent.Length > 0;
    public string SelectedVersions => ModDisplay.Versions(SelectedInstallation?.Metadata);
    public string SelectedAuthor => SelectedInstallation?.Metadata?.Author is { Length: > 0 } author ? author : "—";
    public string SelectedDirectory => SelectedInstallation?.Directory ?? (selectedRow?.Match is { Candidates.Count: > 1 } match
        ? string.Join("\n", match.Candidates.Select(mod => mod.Directory)) : "—");
    public string SelectedDependencies => SelectedInstallation?.Metadata is not { } metadata ? "—"
        : metadata.Requirements.Dependencies.Count == 0 ? "未声明" : string.Join("、", metadata.Requirements.Dependencies.Select(dep => dep.DisplayName));
    public string SelectedRules => SelectedInstallation?.Metadata is not { } metadata ? ""
        : string.Join("\n", new[]
        {
            ("在以下模组之前：", metadata.Requirements.LoadBefore),
            ("在以下模组之后：", metadata.Requirements.LoadAfter),
            ("不兼容：", metadata.Requirements.IncompatibleWith)
        }.Where(item => item.Item2.Count > 0).Select(item => item.Item1 + string.Join("、", item.Item2)));
    public bool HasSelectedRules => SelectedRules.Length > 0;
    private bool ContainsSearch(string text) => text.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    private void InitializeCatalogueViews()
    {
        VisibleAvailableMods = CollectionViewSource.GetDefaultView(AvailableMods);
        VisibleAvailableMods.Filter = item => item is InstalledModRow row
            && MatchesSource(row.Installation.Source)
            && MatchesRecent(row)
            && (ContainsSearch(row.Name) || ContainsSearch(row.PackageId) || ContainsSearch(row.Installation.Metadata?.Author ?? "")
                || ContainsSearch(row.Installation.WorkshopId));
        ApplyAvailableSort();
    }
    public void ApplyScan(ModScanResult result, Dictionary<string, ModActivityRecord>? preparedActivity = null, bool observeActivity = true)
    {
        scan = result;
        previewDirectory = null;
        try { activity = preparedActivity ?? (observeActivity ? activityStore.Observe(result.Catalogue.Installations, DateTimeOffset.UtcNow, result.ActivityRoots) : activityStore.Load()); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { Status = "近期模组记录未保存：" + ex.Message; }
        // Read failures stay separate from diagnostics of the current enabled order.
        ScanIssues.ReplaceWith(result.Diagnostics.Where(issue => issue.Code is "ScanRootUnreadable" or "ScanLimit"
            or "VersionReadFailed" or "NoScanPaths" or "MetadataUnreadable").Select(issue => new DiagnosticRow(issue)));
        RefreshCatalogueMatches();
        foreach (var property in new[] { nameof(HasCatalogue), nameof(GameVersionLabel), nameof(HasScanIssues), nameof(ScanIssuesLabel) }) OnPropertyChanged(property);
        Status = $"已读取 {result.Catalogue.Installations.Count} 个模组" + (HasScanIssues ? $"，{ScanIssues.Count} 条读取记录" : "");
        RefreshCommands();
    }

    internal void ApplyContentScan(ModScanResult result, Dictionary<string, ModActivityRecord> records)
    {
        // Content enrichment changes badges/times only. Keep row identity, current selection and editing history.
        scan = result;
        activity = records;
        var installations = result.Catalogue.Installations.ToDictionary(mod => mod.Directory, StringComparer.OrdinalIgnoreCase);
        foreach (var row in AvailableMods)
            if (installations.TryGetValue(row.Installation.Directory, out var mod)) row.UpdateInstallation(mod);
        var version = EffectiveGameVersion;
        foreach (var row in Rows) row.SetMatch(result.Catalogue.Resolve(row.PackageId, version));
        ApplyMarkerState();
        RefreshSelection();
    }
    internal void MergeWorkshopInstallations(IReadOnlyList<ModInstallation> changed,
        Dictionary<string, ModActivityRecord> records)
    {
        if (scan is null || changed.Count == 0) return;
        var updates = changed.ToDictionary(mod => mod.Directory, StringComparer.OrdinalIgnoreCase);
        var merged = scan.Catalogue.Installations.Select(mod => updates.GetValueOrDefault(mod.Directory, mod)).ToList();
        var existing = merged.Select(mod => mod.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        merged.AddRange(changed.Where(mod => !existing.Contains(mod.Directory)));
        scan = scan with { Catalogue = new(merged) };
        activity = records;
        var version = EffectiveGameVersion;
        foreach (var row in Rows)
        {
            var match = scan.Catalogue.Resolve(row.PackageId, version);
            if (row.Match is null || !row.Match.Candidates.SequenceEqual(match.Candidates)) row.SetMatch(match);
        }
        var represented = Rows.Where(row => row.Match is not null).SelectMany(row => row.Match!.Candidates)
            .Select(mod => mod.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Patch just affected rows. Unrelated rows retain identity, selection and presentation state.
        foreach (var row in AvailableMods.ToArray())
        {
            if (represented.Contains(row.Installation.Directory)) AvailableMods.Remove(row);
            else if (updates.TryGetValue(row.Installation.Directory, out var mod)) row.UpdateInstallation(mod);
        }
        var available = AvailableMods.Select(row => row.Installation.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in merged)
            if (!represented.Contains(mod.Directory) && available.Add(mod.Directory)) AvailableMods.Add(new(mod, false));
        foreach (var issue in ScanIssues.Where(issue => updates.ContainsKey(issue.Diagnostic.Location)).ToArray()) ScanIssues.Remove(issue);
        foreach (var issue in changed.SelectMany(mod => mod.Diagnostics).Where(issue => issue.Code == "MetadataUnreadable"))
            ScanIssues.Add(new(issue));
        ApplyMarkerState();
        RefreshOrderDiagnostics();
        RefreshCounts();
        RefreshSelection();
        OnPropertyChanged(nameof(HasScanIssues));
        OnPropertyChanged(nameof(ScanIssuesLabel));
        RefreshCommands();
    }

    private void RefreshCatalogueMatches()
    {
        var previousSelection = CurrentOrderSelection();
        var primary = SelectedRow;
        var configurationVersion = EffectiveGameVersion;
        foreach (var row in Rows) row.SetMatch(scan?.Catalogue.Resolve(row.PackageId, configurationVersion));
        var represented = Rows.Where(row => row.Match is not null).SelectMany(row => row.Match!.Candidates)
            .Select(mod => mod.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SelectedAvailable = null;
        // An ambiguous entry represents every candidate; do not choose one silently.
        AvailableMods.ReplaceWith(scan?.Catalogue.Installations.Where(mod => !represented.Contains(mod.Directory))
            .Select(mod => new InstalledModRow(mod, false)) ?? []);
        ApplyMarkerState();
        RefreshOrderDiagnostics();
        VisibleRows.Refresh();
        if (previousSelection.Length > 0) SelectOrderRows(previousSelection, primary);
        RefreshCounts();
        RefreshSelection();
    }

    private void ApplyRetainedState()
    {
        foreach (var row in Rows) row.SetRetained(IsRetained(row.PackageId));
        foreach (var row in AvailableMods) row.SetRetained(IsRetained(row.PackageId));
        OnPropertyChanged(nameof(RetainedCount));
        OnPropertyChanged(nameof(HasRetainedMods));
        OnPropertyChanged(nameof(RetainedLabel));
    }
    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(AvailableCountLabel));
        OnPropertyChanged(nameof(OrderCountLabel));
    }
    private void RefreshSelection()
    {
        foreach (var property in new[] { nameof(SelectedPackage), nameof(SelectedName), nameof(SelectedSource), nameof(SelectedVersions),
                     nameof(SelectedAuthor), nameof(SelectedDirectory), nameof(SelectedDependencies), nameof(SelectedRules), nameof(HasSelectedRules), nameof(SelectedDescription), nameof(SelectedContent), nameof(HasSelectedContent), nameof(SelectedMarkerCountLabel) }) OnPropertyChanged(property);
        RefreshSelectedPresentation();
    }
}
