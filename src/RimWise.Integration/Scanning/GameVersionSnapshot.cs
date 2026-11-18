using System.Text.RegularExpressions;
using RimWise.Core;

namespace RimWise.Integration.Scanning;

/// <summary>Keep runtime evidence separate from an imported configuration's version marker.</summary>
public sealed record GameVersionSnapshot(GameVersion EffectiveVersion, GameVersionDetection Installed,
    GameVersion? SelectedVersion, GameVersion? ConfigurationVersion)
{
    public string Source => Installed.Version is not null ? "安装版本" : SelectedVersion is not null ? "手动目标" : "配置标记";
    public string Description => $"目标={EffectiveVersion}；来源={Source}；安装={Installed.Version?.ToString() ?? "未识别"}"
        + (Revision(Installed) is { } revision ? $".{revision}" : "")
        + $"；手动={SelectedVersion?.ToString() ?? "未指定"}；配置={ConfigurationVersion?.ToString() ?? "未识别"}"
        + $"；版本文件={Installed.EvidenceFile}";

    public static GameVersionSnapshot Capture(string gameDirectory, string? selected, string? configuration)
    {
        var installed = GameVersionDetector.Detect(gameDirectory);
        var selectedVersion = GameVersion.Parse(selected);
        var configVersion = GameVersion.Parse(configuration);
        var effective = installed.Version ?? selectedVersion ?? configVersion
            ?? throw new InvalidDataException("游戏及配置版本均未识别，请在路径设置中指定目标版本。");
        GameVersionCompatibility.EnsureCompatibleIdentifiers(configVersion, effective);
        return new(effective, installed, selectedVersion, configVersion);
    }

    public void EnsureUnchanged(GameVersionSnapshot current)
    {
        if (Installed.Version is not null && current.Installed.Version is null)
            throw new IOException("保存前无法再次识别游戏安装版本，请检查游戏目录后重试。原排序未覆盖。\n"
                + $"测试前：{Description}\n保存前：{current.Description}");
        // A missing build number is unknown, not a different build. Moving from 1.7 to
        // an observed 1.7.5011 must not be reported as an actual game update.
        var before = EffectiveVersion;
        var after = current.EffectiveVersion;
        var revisionBefore = Revision(Installed);
        var revisionAfter = Revision(current.Installed);
        if (before.Build is not null && after.Build is null || revisionBefore is not null && revisionAfter is null)
            throw new IOException("保存前的游戏构建信息不完整，无法核对测试时的版本。请检查游戏目录后重试，原排序未覆盖。\n"
                + $"测试前：{Description}\n保存前：{current.Description}");
        if (before.Major != after.Major || before.Minor != after.Minor
            || before.Build is { } buildBefore && after.Build is { } buildAfter && buildBefore != buildAfter
            || revisionBefore is { } oldRevision && revisionAfter is { } newRevision && oldRevision != newRevision)
            throw new IOException("游戏版本在测试期间发生变化，请重新测试后保存。\n"
                + $"测试前：{Description}\n保存前：{current.Description}");
    }

    private static int? Revision(GameVersionDetection detection)
    {
        if (detection.Version is null) return null;
        var match = Regex.Match(detection.RawText, @"^(?:[vV]|Version\s*[:=]?\s*)?\d{1,3}\.\d{1,3}\.\d{1,9}\.(\d{1,9})(?:\s|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
