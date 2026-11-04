using RimWise.Core;

namespace RimWise.Desktop.Presentation;

internal static class ModDisplay
{
    internal static string SourceIcon(ModSource? source) => source switch
        {
            ModSource.Game => "M 3,3 L 17,3 L 17,17 L 3,17 Z M 6,7 L 14,7 M 6,11 L 14,11",
            ModSource.Local => "M 1,5 L 8,5 L 10,8 L 19,8 L 19,17 L 1,17 Z",
            // Steam's offset joints and left-hand lever remain distinct at 18 px.
            ModSource.Workshop => "M 0.8,10.7 L 6.3,13.4 M 7.2,11.2 L 10.3,6.2 M 8.6,14.1 L 14,9.4 M 13.5,2.7 A 3.5,3.5 0 1 1 13.5,9.7 A 3.5,3.5 0 1 1 13.5,2.7 Z M 6.3,10.9 A 2.5,2.5 0 1 1 6.3,15.9 A 2.5,2.5 0 1 1 6.3,10.9 Z",
            _ => "M 5,5 C 5,0 16,0 15,6 C 15,9 10,9 10,12 M 10,15 L 10,17"
        };
    internal static string CodeLabel(ModCodeKind kind) => kind switch
        { ModCodeKind.CSharp => "C#MOD", ModCodeKind.Xml => "XMLMOD", _ => "未识别" };
    internal static string CodeIcon(ModCodeKind kind) => kind switch
        { ModCodeKind.CSharp => "C#", ModCodeKind.Xml => "XML", _ => "?" };
    internal static string ContentIcon(ModCodeKind code, ModContentKinds kinds)
    {
        if (code is ModCodeKind.CSharp or ModCodeKind.Unknown) return CodeIcon(code);
        if ((kinds & (ModContentKinds.Definitions | ModContentKinds.Patches)) != 0) return "XML";
        var resources = (kinds & (ModContentKinds.Textures | ModContentKinds.Audio | ModContentKinds.AssetBundles)) != 0;
        if ((kinds & ModContentKinds.Translation) != 0) return resources ? "混合" : "翻译";
        return resources ? "资源" : "XML";
    }
    internal static string ContentLabel(ModCodeKind code, ModContentKinds kinds) => ContentIcon(code, kinds) switch
        { "C#" => "C#MOD", "XML" => "XMLMOD", "资源" => "资源MOD", "翻译" => "翻译MOD", "混合" => "混合MOD", _ => "未识别" };
    internal static string ContentDescription(ModContentKinds kinds) => string.Join("、", new[] {
        (ModContentKinds.ManagedCode, "C#／托管代码"), (ModContentKinds.Definitions, "XML 定义"),
        (ModContentKinds.Patches, "XML 补丁"), (ModContentKinds.Translation, "翻译"),
        (ModContentKinds.Textures, "贴图"), (ModContentKinds.Audio, "音效"), (ModContentKinds.AssetBundles, "资源包")
    }.Where(item => (kinds & item.Item1) != 0).Select(item => item.Item2));
    internal static string ContentTooltip(ModCodeKind code, ModContentKinds kinds) =>
        ContentDescription(kinds) is { Length: > 0 } description
            ? ContentLabel(code, kinds) + "\n包括：" + description + "\n按安装目录识别，可能包含多个游戏版本的内容。"
                + (code == ModCodeKind.Unknown ? "\n尚未完整识别，请等待扫描或刷新。" : "")
            : CodeTooltip(code);
    internal static string CodeTooltip(ModCodeKind kind) => kind switch
        { ModCodeKind.CSharp => "C#MOD：安装目录包含托管 DLL（含各版本目录），可能同时包含 XML。", ModCodeKind.Xml => "XMLMOD：未发现托管 DLL，包含 XML 或资源文件。", _ => "类型未识别：尚未扫描、读取不完整或安装来源不唯一。" };
    internal static string Source(ModSource source) => source switch
        { ModSource.Game => "游戏内容", ModSource.Local => "本地", _ => "创意工坊" };
    internal static string SourceTooltip(ModSource? source) => source switch
        { ModSource.Game => "游戏内容：环世界本体或 DLC。", ModSource.Local => "本地模组：来自本地 Mods 目录。",
          ModSource.Workshop => "Steam 创意工坊模组：来自创意工坊安装目录。", _ => "来源未确定：未找到安装或存在多份安装。" };
    internal static string Versions(ModMetadata? metadata) => metadata is null ? "—"
        : metadata.SupportedVersions.Count > 0 ? string.Join("、", metadata.SupportedVersions)
        : metadata.LegacyTargetVersion.Length > 0 ? metadata.LegacyTargetVersion : "未注明";
}

public sealed class InstalledModRow(ModInstallation installation, bool duplicate) : ObservableObject
{
    public string? MarkerColor { get; private set; }
    public void SetColor(string? value) { if (MarkerColor == value) return; MarkerColor = value; OnPropertyChanged(nameof(MarkerColor)); }
    private bool isRetained;
    private bool isRecentlyAdded;
    private bool isRecentlyUpdated;
    private string[] tags = [];
    private string? nameSortKey;
    public ModInstallation Installation { get; private set; } = installation;
    internal void UpdateInstallation(ModInstallation updated)
    {
        Installation = updated;
        foreach (var property in new[] { nameof(Installation), nameof(CodeKind), nameof(CodeKindLabel), nameof(CodeKindIcon), nameof(CodeKindTooltip) }) OnPropertyChanged(property);
    }
    public string Name => Installation.Name;
    internal string NameSortKey => nameSortKey ??= PinyinSortKey.Create(Name);
    public string Author => Installation.Metadata?.Author ?? string.Empty;
    public DateTimeOffset? AddedUtc { get; private set; }
    public DateTimeOffset? UpdatedUtc { get; private set; }
    public string AddedTooltip => ModActivityDisplay.AddedTooltip(AddedUtc);
    public string UpdatedTooltip => ModActivityDisplay.UpdatedTooltip(UpdatedUtc);
    public string PackageId => Installation.PackageId.Length > 0 ? Installation.PackageId : Installation.FolderName;
    public string SourceLabel => ModDisplay.Source(Installation.Source);
    public string SourceTooltip => ModDisplay.SourceTooltip(Installation.Source);
    public string SourceIcon => ModDisplay.SourceIcon(Installation.Source);
    public bool IsWorkshopSource => Installation.Source == ModSource.Workshop;
    public ModCodeKind CodeKind => Installation.CodeKind;
    public bool HasCodeKindBadge => Installation.Source != ModSource.Game;
    public string CodeKindLabel => ModDisplay.ContentLabel(CodeKind, Installation.ContentKinds);
    public string CodeKindIcon => ModDisplay.ContentIcon(CodeKind, Installation.ContentKinds);
    public string CodeKindTooltip => ModDisplay.ContentTooltip(CodeKind, Installation.ContentKinds);
    public string VersionLabel => ModDisplay.Versions(Installation.Metadata);
    public string StateLabel => Installation.Metadata is null ? "信息未读取" : duplicate ? "多份安装"
        : Installation.Diagnostics.Count > 0 ? "有提示" : Installation.PackageId.Length == 0 ? "旧格式／标识未定" : "已读取";
    public bool IsRetained => isRetained;
    public string RetainedTooltip => isRetained ? "已标记为必须保留；取消标记后才可移出" : "标记为必须保留";
    public bool IsRecentlyAdded => isRecentlyAdded;
    public bool IsRecentlyUpdated => isRecentlyUpdated;
    public string RecentLabel => IsRecentlyAdded && IsRecentlyUpdated ? "近期新增／更新"
        : IsRecentlyAdded ? "近期新增" : IsRecentlyUpdated ? "近期更新" : "";
    public IReadOnlyList<string> Tags => tags;
    public string TagsLabel => string.Join("、", tags);
    public string TagTooltip => tags.Length == 0 ? "未添加标签" : "标签：" + TagsLabel;
    public void SetRetained(bool value)
    {
        if (isRetained == value) return;
        isRetained = value;
        OnPropertyChanged(nameof(IsRetained));
        OnPropertyChanged(nameof(RetainedTooltip));
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
        OnPropertyChanged(nameof(TagTooltip));
    }
}

public sealed class DiagnosticRow(CatalogueDiagnostic diagnostic, string modName = "")
{
    public CatalogueDiagnostic Diagnostic { get; } = diagnostic;
    public string SeverityLabel => Diagnostic.Severity switch
        { DiagnosticSeverity.Error => "错误", DiagnosticSeverity.Warning => "警告", _ => "信息" };
    public string Message => Diagnostic.Message;
    public string Location => Diagnostic.Location;
    public string Identifier => Diagnostic.Identifier;
    public string ModName { get; } = modName.Length > 0 ? modName
        : diagnostic.Identifier.Length > 0 ? diagnostic.Identifier : "当前排序";
}
