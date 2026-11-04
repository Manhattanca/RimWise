using RimWise.Core;

namespace RimWise.Desktop.Presentation;

public sealed class LoadOrderRow(string packageId, int index) : ObservableObject
{
    private int index = index;
    private RimWise.Core.OrderMatch? match;
    private IReadOnlyList<CatalogueDiagnostic> diagnostics = [];
    private bool hasStatus;
    private bool isRetained;
    private bool isRecentlyAdded;
    private bool isRecentlyUpdated;
    private string[] tags = [];
    public string? MarkerColor { get; private set; }
    public void SetColor(string? value) { if (MarkerColor == value) return; MarkerColor = value; OnPropertyChanged(nameof(MarkerColor)); }
    public DiagnosticSeverity? Severity { get; private set; }
    public bool HasError => Severity == DiagnosticSeverity.Error;
    public bool HasWarning => Severity == DiagnosticSeverity.Warning;
    public string DiagnosticTooltip => string.Join("\n\n", diagnostics.Select(issue => issue.Message).Distinct());
    public string RowTooltip => $"{Name}\n{PackageId}\n{SourceLabel}\n{StateLabel}\n\n" +
        (isRetained ? "必须保留\n\n" : string.Empty) +
        (tags.Length > 0 ? "标签：" + string.Join("、", tags) + "\n\n" : string.Empty) +
        (diagnostics.Count > 0 ? DiagnosticTooltip : hasStatus ? "在已读取的声明和排序规则中未发现问题" : "等待扫描模组资料");
    public bool IsRetained => isRetained;
    public bool IsRecentlyAdded => isRecentlyAdded;
    public bool IsRecentlyUpdated => isRecentlyUpdated;
    public DateTimeOffset? AddedUtc { get; private set; }
    public DateTimeOffset? UpdatedUtc { get; private set; }
    public string AddedTooltip => ModActivityDisplay.AddedTooltip(AddedUtc);
    public string UpdatedTooltip => ModActivityDisplay.UpdatedTooltip(UpdatedUtc);
    public string RecentLabel => IsRecentlyAdded && IsRecentlyUpdated ? "近期新增／更新"
        : IsRecentlyAdded ? "近期新增" : IsRecentlyUpdated ? "近期更新" : "";
    public IReadOnlyList<string> Tags => tags;
    public string TagsLabel => string.Join("、", tags);
    public string RetainedTooltip => isRetained ? "已标记为必须保留；取消标记后才可移出" : "标记为必须保留";
    public void SetRetained(bool value)
    {
        if (isRetained == value) return;
        isRetained = value;
        OnPropertyChanged(nameof(IsRetained));
        OnPropertyChanged(nameof(RetainedTooltip));
        OnPropertyChanged(nameof(RowTooltip));
    }

    public void SetActivity(bool recentlyAdded, bool recentlyUpdated, DateTimeOffset? addedUtc = null, DateTimeOffset? updatedUtc = null)
    {
        AddedUtc = addedUtc;
        UpdatedUtc = updatedUtc;
        foreach (var property in new[] { nameof(AddedUtc), nameof(UpdatedUtc), nameof(AddedTooltip), nameof(UpdatedTooltip) }) OnPropertyChanged(property);
        if (isRecentlyAdded == recentlyAdded && isRecentlyUpdated == recentlyUpdated) return;
        isRecentlyAdded = recentlyAdded;
        isRecentlyUpdated = recentlyUpdated;
        OnPropertyChanged(nameof(IsRecentlyAdded));
        OnPropertyChanged(nameof(IsRecentlyUpdated));
        OnPropertyChanged(nameof(RecentLabel));
    }

    public void SetTags(IEnumerable<string> values)
    {
        var next = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.SequenceEqual(next, StringComparer.OrdinalIgnoreCase)) return;
        tags = next;
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(TagsLabel));
        OnPropertyChanged(nameof(RowTooltip));
    }
    public void SetDiagnostics(IReadOnlyList<CatalogueDiagnostic> issues, bool evaluated)
    {
        diagnostics = issues;
        hasStatus = evaluated;
        Severity = issues.Count == 0 ? null : issues.Max(issue => issue.Severity);
        foreach (var property in new[] { nameof(Severity), nameof(HasError), nameof(HasWarning), nameof(DiagnosticTooltip), nameof(RowTooltip) }) OnPropertyChanged(property);
    }
    public string PackageId { get; } = packageId;
    public string Name => match?.Candidates.FirstOrDefault()?.Name ?? PackageId;
    public string SourceLabel => match?.UniqueInstallation is { } mod ? ModDisplay.Source(mod.Source)
        : match?.Candidates.Count > 1 ? "多个来源" : "—";
    public string SourceIcon => ModDisplay.SourceIcon(match?.UniqueInstallation?.Source);
    public string SourceTooltip => ModDisplay.SourceTooltip(match?.UniqueInstallation?.Source);
    public bool IsWorkshopSource => match?.UniqueInstallation?.Source == ModSource.Workshop;
    public ModCodeKind CodeKind => match?.UniqueInstallation?.CodeKind ?? ModCodeKind.Unknown;
    public bool HasCodeKindBadge => match?.UniqueInstallation?.Source != ModSource.Game;
    public string CodeKindLabel => ModDisplay.ContentLabel(CodeKind, match?.UniqueInstallation?.ContentKinds ?? ModContentKinds.None);
    public string CodeKindIcon => ModDisplay.ContentIcon(CodeKind, match?.UniqueInstallation?.ContentKinds ?? ModContentKinds.None);
    public string CodeKindTooltip => ModDisplay.ContentTooltip(CodeKind, match?.UniqueInstallation?.ContentKinds ?? ModContentKinds.None);
    public string StateLabel => match is null ? "未扫描" : match.Candidates.Count switch
        { 0 => "未找到", 1 => match.UniqueInstallation!.Metadata is null ? "信息未读取" : "已找到", _ => "多份安装" };
    public RimWise.Core.OrderMatch? Match => match;
    public ModInstallation? Installation => match?.UniqueInstallation;
    public void SetMatch(RimWise.Core.OrderMatch? value)
    {
        match = value;
        OnPropertyChanged(nameof(Installation));
        foreach (var property in new[] { nameof(Name), nameof(SourceLabel), nameof(SourceTooltip), nameof(SourceIcon), nameof(IsWorkshopSource), nameof(CodeKind), nameof(HasCodeKindBadge), nameof(CodeKindLabel), nameof(CodeKindIcon), nameof(CodeKindTooltip), nameof(StateLabel), nameof(Match) }) OnPropertyChanged(property);
    }
    public int Index
    {
        get => index;
        set { index = value; OnPropertyChanged(); }
    }
}
