namespace RimWise.Integration.Configuration;

public enum ThemePreference { System, Light, Dark }
public enum AppearancePreset { System, Light, Dark, Ocean, Amber, Violet, Black }
public enum BackgroundPlacement { Fill, Fit }
public enum AppearanceSurface { Bars, Panels, Lists, Inputs, Selectors, Buttons, Content }
public enum SurfaceOpacityMode { Follow, Custom, Opaque }

public sealed record SurfaceOpacitySetting
{
    public SurfaceOpacityMode Mode { get; init; }
    public double Opacity { get; init; } = 1;
    internal void Validate()
    {
        if (!Enum.IsDefined(Mode) || !double.IsFinite(Opacity) || Opacity is < 0 or > 1)
            throw new InvalidDataException("界面独立透明度设置无效。");
    }
}

/// <summary>Value records keep preview comparisons and named-theme round trips deterministic.</summary>
public sealed record AppearanceTransparency
{
    public SurfaceOpacitySetting Bars { get; init; } = new();
    public SurfaceOpacitySetting Panels { get; init; } = new();
    public SurfaceOpacitySetting Lists { get; init; } = new();
    public SurfaceOpacitySetting Inputs { get; init; } = new();
    public SurfaceOpacitySetting Selectors { get; init; } = new();
    public SurfaceOpacitySetting Buttons { get; init; } = new();
    public SurfaceOpacitySetting Content { get; init; } = new();
    public SurfaceOpacitySetting Get(AppearanceSurface surface) => surface switch
    {
        AppearanceSurface.Bars => Bars, AppearanceSurface.Panels => Panels, AppearanceSurface.Lists => Lists,
        AppearanceSurface.Inputs => Inputs, AppearanceSurface.Selectors => Selectors,
        AppearanceSurface.Buttons => Buttons, AppearanceSurface.Content => Content,
        _ => throw new ArgumentOutOfRangeException(nameof(surface))
    };
    public AppearanceTransparency With(AppearanceSurface surface, SurfaceOpacitySetting value) => surface switch
    {
        AppearanceSurface.Bars => this with { Bars = value }, AppearanceSurface.Panels => this with { Panels = value },
        AppearanceSurface.Lists => this with { Lists = value }, AppearanceSurface.Inputs => this with { Inputs = value },
        AppearanceSurface.Selectors => this with { Selectors = value },
        AppearanceSurface.Buttons => this with { Buttons = value }, AppearanceSurface.Content => this with { Content = value },
        _ => throw new ArgumentOutOfRangeException(nameof(surface))
    };
    internal void Validate()
    {
        foreach (var surface in Enum.GetValues<AppearanceSurface>())
        {
            var rule = Get(surface) ?? throw new InvalidDataException("界面透明度设置缺失。");
            rule.Validate();
        }
    }
}

/// <summary>Only data, never executable theme markup or an external image address.</summary>
public sealed record AppearanceSettings
{
    public AppearancePreset Preset { get; init; } = AppearancePreset.System;
    public string? Accent { get; init; }
    public string? Page { get; init; }
    public string? Panel { get; init; }
    public string? Text { get; init; }
    public string? Border { get; init; }
    public string? Background { get; init; }
    public BackgroundPlacement Placement { get; init; }
    public double PositionX { get; init; } = .5;
    public double PositionY { get; init; } = .5;
    public double Shade { get; init; } = .40;
    public double Blur { get; init; } = 0;
    public double PanelOpacity { get; init; } = .94;
    public double OtherUiOpacity { get; init; } = .94;
    public AppearanceTransparency Transparency { get; init; } = new();
    public AppearanceControlOverrides Controls { get; init; } = new();
    public double ControlOpacity(string key, AppearanceSurface surface) => Controls.Get(key) switch
    {
        { Mode: SurfaceOpacityMode.Custom } rule => rule.Opacity,
        { Mode: SurfaceOpacityMode.Opaque } => 1,
        _ => SurfaceOpacity(surface)
    };
    public double SurfaceOpacity(AppearanceSurface surface)
    {
        var rule = Transparency.Get(surface);
        return rule.Mode switch
        {
            SurfaceOpacityMode.Opaque => 1,
            SurfaceOpacityMode.Custom => rule.Opacity,
            _ => surface switch
            {
                AppearanceSurface.Bars or AppearanceSurface.Panels or AppearanceSurface.Lists => PanelOpacity,
                AppearanceSurface.Buttons => 1,
                _ => OtherUiOpacity
            }
        };
    }
    public ThemePreference Preference => Preset == AppearancePreset.System ? ThemePreference.System
        : Preset == AppearancePreset.Light ? ThemePreference.Light : ThemePreference.Dark;

    public void Validate()
    {
        if (!Enum.IsDefined(Preset) || !Enum.IsDefined(Placement)) throw new InvalidDataException("主题或背景显示方式无效。");
        foreach (var value in new[] { Accent, Page, Panel, Text, Border })
            if (value is not null && !IsColor(value)) throw new InvalidDataException("颜色请填写 #RRGGBB 格式，例如 #337F73。");
        if (Background is not null && (Background.Length != 68 || !Background.EndsWith(".png", StringComparison.Ordinal)
            || !Background.AsSpan(0, 64).ToString().All(Uri.IsHexDigit)))
            throw new InvalidDataException("背景图片缓存标识无效。");
        if (!InRange(PositionX, 0, 1) || !InRange(PositionY, 0, 1) || !InRange(Shade, 0, .90)
            || !InRange(Blur, 0, 24) || !InRange(PanelOpacity, 0, 1) || !InRange(OtherUiOpacity, 0, 1))
            throw new InvalidDataException("背景参数超出允许范围。");
        if (Transparency is null) throw new InvalidDataException("界面透明度设置缺失。");
        Transparency.Validate();
        if (Controls is null) throw new InvalidDataException("单独透明度配置缺失。");
        Controls.Validate();
    }
    public static bool IsColor(string value) => value.Length == 7 && value[0] == '#' && value.AsSpan(1).ToString().All(Uri.IsHexDigit);
    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

public sealed record NamedAppearanceTheme(string Name, AppearanceSettings Settings);

public sealed class AppearanceSettingsStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredSettings> file = new(applicationDirectory, "appearance", maximumBytes: 1_000_000);
    private StoredSettings? stored;
    private bool loaded;
    public string SettingsPath => file.SettingsPath;
    private StoredSettings? Read()
    {
        if (!loaded)
        {
            var value = file.Load();
            if (value is not null && (value.SchemaVersion is not (1 or 2) || !Enum.IsDefined(value.Theme)))
                throw new InvalidDataException("外观配置版本或主题无效。");
            stored = value; loaded = true;
        }
        return stored;
    }
    public string? ActiveThemeName => Read()?.ActiveTheme;
    public IReadOnlyList<NamedAppearanceTheme> LoadThemes()
    {
        var themes = Read()?.Themes ?? [];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in themes)
        {
            if (theme is null || theme.Settings is null) throw new InvalidDataException("自定义主题内容缺失。");
            ValidateName(theme.Name); theme.Settings.Validate();
            if (!names.Add(theme.Name)) throw new InvalidDataException("自定义主题名称重复。");
        }
        return themes.ToArray();
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Any(char.IsControl) || name != name.Trim())
            throw new InvalidDataException("主题名称请输入 1～60 个字符，不支持控制字符或首尾空格。");
    }

    public ThemePreference Load() => LoadAppearance().Preference;

    public AppearanceSettings LoadAppearance()
    {
        var settings = Read();
        if (settings is null) return new();
        if (settings.SchemaVersion is not (1 or 2) || !Enum.IsDefined(settings.Theme))
            throw new InvalidDataException("外观配置版本或主题无效。");
        var result = settings.SchemaVersion == 1 ? new AppearanceSettings { Preset = (AppearancePreset)settings.Theme }
            : settings.Options ?? throw new InvalidDataException("外观配置内容缺失。");
        result.Validate();
        _ = LoadThemes();
        if (result.Preference != settings.Theme) throw new InvalidDataException("外观配置主题不一致。");
        return result;
    }

    public void Save(ThemePreference preference)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        SaveAppearance(new() { Preset = (AppearancePreset)preference });
    }

    public void SaveAppearance(AppearanceSettings settings)
    {
        settings.Validate();
        Write(new StoredSettings(2, settings.Preference, settings, null, LoadThemes().ToArray()));
    }

    public void SaveNamedTheme(string name, AppearanceSettings settings)
    {
        ValidateName(name); settings.Validate();
        var themes = LoadThemes().ToList();
        var index = themes.FindIndex(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase));
        var entry = new NamedAppearanceTheme(name, settings);
        if (index < 0) themes.Add(entry); else themes[index] = entry;
        Write(new StoredSettings(2, settings.Preference, settings, name, themes.ToArray()));
    }

    /// <summary>Persist the active selection, never create or overwrite a theme definition.</summary>
    public void ActivateTheme(AppearanceSettings settings, string? name = null)
    {
        settings.Validate();
        var themes = LoadThemes().ToArray();
        if (name is not null)
        {
            ValidateName(name);
            var selected = themes.SingleOrDefault(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase));
            if (selected is null || selected.Settings != settings)
                throw new InvalidDataException("所选主题已改变，请重新选择后使用。");
            name = selected.Name;
        }
        Write(new StoredSettings(2, settings.Preference, settings, name, themes));
    }
    private void Write(StoredSettings value) { file.Save(value); stored = value; loaded = true; }

    private sealed record StoredSettings(int SchemaVersion, ThemePreference Theme, AppearanceSettings? Options = null,
        string? ActiveTheme = null, NamedAppearanceTheme[]? Themes = null);
}
