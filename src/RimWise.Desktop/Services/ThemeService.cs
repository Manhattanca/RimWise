using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Services;

/// <summary>Keeps Fluent controls and our palette in sync without recreating windows.</summary>
internal sealed class ThemeService : IDisposable
{
    private readonly Application application;
    private readonly ResourceDictionary resources;
    private readonly bool scopedPreview;
    private readonly AppearanceSettingsStore store;
    private readonly Func<bool> systemIsDark;
    private ResourceDictionary? palette;
    private readonly string profile;
    internal AppearanceSettings Current { get; private set; } = new();
    internal AppearanceImage? Image { get; private set; }
    internal BitmapSource? BackgroundBitmap { get; private set; }
    private AppearanceImage? renderedImage;
    private AppearanceImage? requestedImage;
    private double requestedBlur = -1;
    private readonly System.Windows.Threading.DispatcherTimer blurTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private CancellationTokenSource? backgroundCancellation;
    private bool rendering, disposed;
    private System.Windows.Threading.DispatcherOperation? surfaceRefresh;
    private int backgroundRevision, renderedRevision;
    private readonly Dictionary<bool, ResourceDictionary> basePalettes = [];
    private (AppearancePreset, string?, string?, string?, string?, string?, bool, bool)? paletteKey;
    internal int PaletteRebuilds { get; private set; }
    internal int BackgroundRenders { get; private set; }
    internal AppearanceSettingsStore SettingsStore => store;
    public ThemePreference Preference => Current.Preference;
    public bool IsDark { get; private set; }
    public string? LoadWarning { get; private set; }
    public event EventHandler? Changed;

    internal ThemeService(Application application, AppearanceSettingsStore store, Func<bool>? systemIsDark = null)
    {
        this.application = application;
        resources = application.Resources;
        this.store = store;
        profile = Path.GetDirectoryName(Path.GetDirectoryName(store.SettingsPath))!;
        this.systemIsDark = systemIsDark ?? ReadSystemIsDark;
        blurTimer.Tick += RenderBackground;
        try { Current = store.LoadAppearance(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { LoadWarning = "无法读取外观配置，已跟随系统"; }
        if (Current.Background is { } background)
        {
            try { Image = AppearanceImage.Load(profile, background); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            { LoadWarning = "背景图片无法读取，已使用主题底色。可在外观设置中重新选择。"; }
        }
        Apply();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    private ThemeService(ThemeService source, FrameworkElement scope)
    {
        application = source.application; resources = scope.Resources; scopedPreview = true;
        store = source.store; profile = source.profile; systemIsDark = source.systemIsDark;
        Current = source.Current; Image = source.Image; BackgroundBitmap = source.BackgroundBitmap;
        renderedImage = source.renderedImage;
        if (source.renderedRevision == source.backgroundRevision)
        { requestedImage = Image; requestedBlur = Current.Blur; }
        blurTimer.Tick += RenderBackground;
        Apply();
    }

    // The preview owns its brushes and notifications, not the application's palette.
    internal ThemeService CreatePreview(FrameworkElement scope) => new(this, scope);

    internal void PreviewFrom(ThemeService source)
    {
        if (source.renderedRevision != source.backgroundRevision)
        { Preview(source.Current, source.Image); return; }
        if (Current == source.Current && ReferenceEquals(Image, source.Image)
            && ReferenceEquals(BackgroundBitmap, source.BackgroundBitmap)) return;
        source.Current.Validate();
        Current = source.Current; Image = source.Image;
        blurTimer.Stop(); backgroundCancellation?.Cancel(); backgroundRevision++;
        requestedImage = Image; requestedBlur = Current.Blur; renderedImage = Image;
        BackgroundBitmap = source.BackgroundBitmap; renderedRevision = backgroundRevision;
        Apply();
    }

    public void Select(ThemePreference preference, bool save = true)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        // A failed save leaves the selection and active theme unchanged.
        var settings = new AppearanceSettings { Preset = (AppearancePreset)preference };
        if (save) Save(settings, null);
        else Preview(settings, null);
    }

    internal void Preview(AppearanceSettings settings, AppearanceImage? image)
    {
        settings.Validate();
        if (settings == Current && ReferenceEquals(image, Image)) return;
        Current = settings; Image = image; Apply();
    }

    internal void Save(AppearanceSettings settings, AppearanceImage? image, string? name = null)
    {
        if (scopedPreview) throw new InvalidOperationException("局部预览不能保存外观设置。");
        settings.Validate();
        if (settings.Background is not null)
        {
            if (image is null || image.Name != settings.Background) throw new InvalidDataException("请重新选择有效的背景图片，或先移除背景。");
            image.Save(profile);
        }
        if (name is null) store.SaveAppearance(settings); else store.SaveNamedTheme(name, settings);
        Preview(settings, image);
    }

    internal void RefreshSystemTheme()
    {
        if (Preference == ThemePreference.System) Apply();
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!application.Dispatcher.HasShutdownStarted)
            application.Dispatcher.BeginInvoke(RefreshSystemTheme);
    }

    private void Apply()
    {
        application.Dispatcher.VerifyAccess();
        RequestBackground();
        IsDark = Preference == ThemePreference.Dark || Preference == ThemePreference.System && systemIsDark();
        var signature = (Current.Preset, Current.Accent, Current.Page, Current.Panel, Current.Text, Current.Border, IsDark, Image is not null);
        if (paletteKey == signature)
        {
            // Pointer events can arrive faster than the compositor. Only publish
            // the latest scalar state once in the next render pass.
            surfaceRefresh ??= application.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            {
                surfaceRefresh = null;
                if (disposed) return;
                ApplySurfaceBrushes(); Changed?.Invoke(this, EventArgs.Empty);
            }));
            return;
        }
        surfaceRefresh?.Abort(); surfaceRefresh = null;
        var modeChanged = paletteKey is null || paletteKey.Value.Item7 != IsDark;
        if (!basePalettes.TryGetValue(IsDark, out var defaults))
        {
            defaults = new ResourceDictionary { Source = new Uri($"/RimWise;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
            basePalettes.Add(IsDark, defaults);
        }
        var replacement = new ResourceDictionary { ["TopButtonSurfaceBrush"] = Brush(Colors.Transparent) };
        foreach (var resourceKey in defaults.Keys) replacement[resourceKey] = defaults[resourceKey];
        PaletteRebuilds++;
        var accent = Current.Accent ?? Current.Preset switch
        {
            AppearancePreset.Ocean => "#4398DB", AppearancePreset.Amber => "#DDA044",
            AppearancePreset.Violet => "#A385DA", _ => null
        };
        var page = (Image is null ? Current.Page : null) ?? Current.Preset switch
        {
            AppearancePreset.Ocean => "#15242F", AppearancePreset.Amber => "#27221B",
            AppearancePreset.Violet => "#24202E", AppearancePreset.Black => "#101214", _ => null
        };
        var panel = Current.Panel ?? Current.Preset switch
        {
            AppearancePreset.Ocean => "#203440", AppearancePreset.Amber => "#342D23",
            AppearancePreset.Violet => "#302A3B", AppearancePreset.Black => "#1A1D20", _ => null
        };
        void ColorOverride(string key, string? hex) { if (hex is not null) replacement[key] = Brush((Color)ColorConverter.ConvertFromString(hex)); }
        ColorOverride("PageBrush", page); ColorOverride("PanelBrush", panel);
        ColorOverride("TextBrush", Current.Text);
        var basePage = ((SolidColorBrush)replacement["PageBrush"]).Color;
        var basePanel = ((SolidColorBrush)replacement["PanelBrush"]).Color;
        var text = ((SolidColorBrush)replacement["TextBrush"]).Color;
        // Guard advanced colors against unreadable combinations. Errors and mod marker colors are not overridden.
        if (Current.Text is null && (Contrast(text, basePage) < 4.5 || Contrast(text, basePanel) < 4.5))
            text = Contrast(Colors.White, basePage) + Contrast(Colors.White, basePanel)
                >= Contrast(Colors.Black, basePage) + Contrast(Colors.Black, basePanel) ? Colors.White : Colors.Black;
        // Opposite custom surfaces cannot share legible text. Bring only incompatible surfaces
        // toward the selected text's opposite extreme rather than leave an unreadable panel.
        if (Current.Text is null)
        {
            var opposite = Luminance(text) > .5 ? Colors.Black : Colors.White;
            for (var i = 0; i < 32 && Contrast(text, basePage) < 5; i++) basePage = Mix(basePage, opposite, .12);
            for (var i = 0; i < 32 && Contrast(text, basePanel) < 5; i++) basePanel = Mix(basePanel, opposite, .12);
        }
        replacement["PageBrush"] = Brush(basePage); replacement["PanelBrush"] = Brush(basePanel);
        replacement["TextBrush"] = Brush(text);
        if (Current.Text is not null || page is not null || panel is not null)
        {
            replacement["MutedBrush"] = Brush(Mix(basePanel, text, .78));
            replacement["DisabledTextBrush"] = Brush(Mix(basePanel, text, .55));
            replacement["HeaderBrush"] = Brush(Mix(basePanel, text, .08));
            replacement["InputBrush"] = Brush(basePanel);
            replacement["AlternateBrush"] = Brush(Mix(basePanel, text, .025));
        }
        replacement["NavigationAccentBrush"] = replacement["AccentBrush"];
        if (accent is not null)
        {
            var color = (Color)ColorConverter.ConvertFromString(accent);
            // Keep button fills exactly as selected; only navigation/link text needs
            // a separately contrast-corrected variant against the page background.
            var readable = color;
            var extreme = Contrast(Colors.White, basePage) > Contrast(Colors.Black, basePage) ? Colors.White : Colors.Black;
            for (var i = 0; i < 24 && Contrast(readable, basePage) < 4.5; i++) readable = Mix(readable, extreme, .1);
            replacement["AccentBrush"] = Brush(color);
            replacement["NavigationAccentBrush"] = Brush(readable);
            replacement["LinkBrush"] = Brush(readable);
            replacement["AccentTextBrush"] = Brush(Contrast(Colors.White, color) >= Contrast(Colors.Black, color) ? Colors.White : Colors.Black);
            replacement["SelectionBrush"] = Brush(Mix(basePanel, color, .25));
            replacement["HoverBrush"] = Brush(Mix(basePanel, color, .08));
        }
        foreach (var key in new[] { "WarningTextBrush", "ErrorTextBrush" })
        {
            var color = ((SolidColorBrush)replacement[key]).Color;
            var target = Luminance(basePanel) < .5 ? Colors.White : Colors.Black;
            for (var i = 0; i < 24 && Contrast(color, basePanel) < 4.5; i++) color = Mix(color, target, .1);
            replacement[key] = Brush(color);
        }
        // .NET 10 still marks the documented runtime switch as experimental.
        // Keep its use here so the rest of the UI has no dependency on that API.
#pragma warning disable WPF0001
        if (modeChanged && !scopedPreview) application.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        if (palette is null)
        {
            palette = new ResourceDictionary();
            foreach (var resourceKey in replacement.Keys) palette[resourceKey] = ((SolidColorBrush)replacement[resourceKey]).Clone();
            resources.MergedDictionaries.Add(palette);
        }
        else foreach (var resourceKey in replacement.Keys) SetBrush((string)resourceKey, (SolidColorBrush)replacement[resourceKey]);
        paletteKey = signature; ApplySurfaceBrushes();
        if (!scopedPreview) foreach (Window window in application.Windows) WindowFrameTheme.Apply(window);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetBrush(string key, SolidColorBrush brush)
    {
        if (palette![key] is SolidColorBrush previous)
        {
            if (previous.Color == brush.Color) return;
            // Mutate a stable resource instead of invalidating every DynamicResource
            // in all mod rows and control templates on each slider tick.
            if (!previous.IsFrozen) { previous.Color = brush.Color; return; }
        }
        palette[key] = brush.Clone();
    }
    private void ApplySurfaceBrushes()
    {
        void Surface(string name, string basis, AppearanceSurface surface)
        {
            var color = ((SolidColorBrush)palette![basis]).Color;
            color.A = (byte)Math.Round((SystemParameters.HighContrast ? 1 : Current.SurfaceOpacity(surface)) * 255);
            SetBrush(name, Brush(color));
        }
        Surface("SurfaceBrush", "PanelBrush", AppearanceSurface.Panels);
        Surface("SurfaceAlternateBrush", "AlternateBrush", AppearanceSurface.Panels);
        Surface("ChromeSurfaceBrush", "PanelBrush", AppearanceSurface.Bars);
        foreach (var (prefix, surface) in new[] { ("List", AppearanceSurface.Lists), ("Input", AppearanceSurface.Inputs), ("Selector", AppearanceSurface.Selectors),
            ("Button", AppearanceSurface.Buttons), ("Content", AppearanceSurface.Content) })
        {
            Surface(prefix + "SurfaceBrush", prefix is "Input" or "Selector" ? "InputBrush" : "PanelBrush", surface);
            Surface(prefix + "HeaderBrush", "HeaderBrush", surface);
            Surface(prefix + "AlternateBrush", "AlternateBrush", surface);
            Surface(prefix + "HoverBrush", "HoverBrush", surface);
            Surface(prefix + "SelectionBrush", "SelectionBrush", surface);
            Surface(prefix + "AccentBrush", "AccentBrush", surface);
            Surface(prefix + "BorderBrush", "BorderBrush", surface);
        }
    }
    private void RequestBackground()
    {
        if (ReferenceEquals(requestedImage, Image) && requestedBlur == Current.Blur) return;
        var imageChanged = !ReferenceEquals(requestedImage, Image);
        requestedImage = Image; requestedBlur = Current.Blur; backgroundRevision++;
        if (!scopedPreview || imageChanged || Current.Blur == 0) { blurTimer.Stop(); backgroundCancellation?.Cancel(); }
        if (Image is null || Current.Blur == 0)
        {
            BackgroundBitmap = Image?.Bitmap; renderedImage = Image;
            renderedRevision = backgroundRevision; return;
        }
        if (palette is null && !scopedPreview)
        {
            BackgroundBitmap = Image.RenderBackground(Current.Blur); BackgroundRenders++;
            renderedImage = Image; renderedRevision = backgroundRevision; return;
        }
        if (!ReferenceEquals(renderedImage, Image)) BackgroundBitmap = Image.Bitmap;
        if (!blurTimer.IsEnabled) blurTimer.Start();
    }
    private async void RenderBackground(object? sender, EventArgs args)
    {
        blurTimer.Stop();
        if (rendering || disposed || requestedImage is null || requestedBlur == 0 || renderedRevision == backgroundRevision) return;
        var image = requestedImage; var blur = requestedBlur; var revision = backgroundRevision;
        using var cancellation = new CancellationTokenSource(); backgroundCancellation = cancellation; rendering = true;
        try
        {
            var result = await Task.Run(() => image.RenderBackground(blur, cancellation.Token), cancellation.Token);
            BackgroundRenders++;
            if (!disposed && (revision == backgroundRevision || scopedPreview && ReferenceEquals(image, Image) && Current.Blur > 0))
            {
                BackgroundBitmap = result; renderedImage = image; renderedRevision = revision;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            if (!disposed && revision == backgroundRevision)
            {
                BackgroundBitmap = image.Bitmap; renderedImage = image; renderedRevision = revision;
                LoadWarning = "背景模糊暂时不可用，已保留原图。"; Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            backgroundCancellation = null; rendering = false;
            if (!disposed && renderedRevision != backgroundRevision) blurTimer.Start();
        }
    }
    internal async Task WaitForBackgroundAsync()
    {
        while (!disposed && (rendering || blurTimer.IsEnabled)) await Task.Delay(15);
    }

    internal static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    internal static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount), (byte)Math.Round(a.B + (b.B - a.B) * amount));
    private static double Luminance(Color c)
    {
        static double Channel(byte b) { var v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
    }
    internal static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);

    private static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return false; }
    }

    public void Dispose()
    {
        disposed = true; blurTimer.Stop(); backgroundCancellation?.Cancel();
        surfaceRefresh?.Abort(); surfaceRefresh = null;
        if (!scopedPreview) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        if (palette is not null) resources.MergedDictionaries.Remove(palette);
    }
}
