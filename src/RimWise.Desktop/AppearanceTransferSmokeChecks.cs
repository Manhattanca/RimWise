#if DEBUG
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>Synthetic theme containers and isolated profiles only; no personal pictures, game or network.</summary>
internal static class AppearanceTransferSmokeChecks
{
    internal static void Attach(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            var checks = new List<string>();
            void Check(bool value, string message)
            {
                if (!value) throw new InvalidOperationException(message);
                checks.Add(message);
                File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(checks));
            }
            try
            {
                await Run(main, output, Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
                main.ViewModel.MarkExported(); Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                main.ViewModel.MarkExported(); Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task Run(MainWindow main, string output, Action<bool, string> check)
    {
        main.LoadSample(); main.ViewModel.MarkExported();
        var order = main.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        var themes = ((App)Application.Current).Themes;
        var baseline = themes.Current; var baselineImage = themes.Image;
        await Idle();
        check(!main.AllowsTransparency && main.Opacity == 1, "native HWND stays opaque; user alpha is not desktop alpha");
        check(WindowChrome.GetWindowChrome(main)?.CornerRadius == new CornerRadius(8), "custom chrome uses native eight-DIP rounded region");
        CheckCorners(main, "initial", check);
        themes.Preview(new AppearanceSettings { PanelOpacity = 0, OtherUiOpacity = 0 }, null);
        await Idle(); CheckCorners(main, "all surfaces transparent", check);
        main.Width = Math.Max(main.MinWidth, main.Width - 100); await Idle(); CheckCorners(main, "resized", check);
        main.WindowState = WindowState.Maximized; await Idle();
        check(main.WindowState == WindowState.Maximized && main.MainClient.Margin == SystemParameters.WindowResizeBorderThickness,
            "maximized work-area margins and native resize behavior preserved");
        main.WindowState = WindowState.Normal; await Idle(); CheckCorners(main, "restored from maximized", check);
        main.WindowState = WindowState.Minimized; await Idle();
        main.WindowState = WindowState.Normal; await Idle(); CheckCorners(main, "restored from minimized", check);
        main.WindowState = WindowState.Maximized; await Idle();
        main.WindowState = WindowState.Minimized; await Idle();
        main.WindowState = WindowState.Normal; await Idle();
        var nativeMaximized = IsZoomed(new WindowInteropHelper(main).Handle);
        File.WriteAllText(Path.Combine(output, "maximize-minimize-restore.json"), JsonSerializer.Serialize(new
            { WpfState = main.WindowState.ToString(), NativeMaximized = nativeMaximized, ClientMargin = main.MainClient.Margin.ToString() }));
        // Windows can restore a minimized, previously-maximized window back to maximized.
        // Require genuinely normal native bounds before checking corner clipping.
        if (nativeMaximized)
        {
            SystemCommands.RestoreWindow(main); await Idle();
            check(!IsZoomed(new WindowInteropHelper(main).Handle), "native restore returns previously-maximized minimized window to normal bounds");
        }
        CheckCorners(main, "maximized-minimized-native-restore", check);
        themes.Preview(baseline, baselineImage);

        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 45, 90, 140, 255, 80, 45, 160, 255, 60, 130, 100, 255, 140, 90, 45, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var originalFile = Path.Combine(output, "synthetic-original.png");
        using (var file = File.Create(originalFile)) encoder.Save(file);
        var image = AppearanceImage.Read(originalFile);
        var settings = new AppearanceSettings { Preset = AppearancePreset.Violet, Accent = "#964AC8", Page = "#242035",
            Panel = "#191827", Text = "#F1F2F3", Border = "#123456", Background = image.Name, Placement = BackgroundPlacement.Fit,
            PositionX = .13, PositionY = .77, Shade = .31, Blur = 4, PanelOpacity = 0, OtherUiOpacity = .37,
            Transparency = new AppearanceTransparency().With(AppearanceSurface.Buttons, new() { Mode = SurfaceOpacityMode.Custom, Opacity = .43 }),
            Controls = new AppearanceControlOverrides().With("main.ImportOrderButton", new() { Mode = SurfaceOpacityMode.Custom, Opacity = .17 }) };
        var package = Path.Combine(output, "合成紫色主题.rimwise-theme");
        AppearanceThemePackage.Write(package, "合成紫色主题", settings, image);
        var loaded = AppearanceThemePackage.Read(package);
        check(loaded.Settings == settings && loaded.Name == "合成紫色主题", "every setting including advanced opacity round trips exactly");
        check(loaded.Image is not null && loaded.Image.Png.SequenceEqual(image.Png) && loaded.Image.Bitmap.IsFrozen,
            "background PNG bytes are embedded losslessly with a frozen decoded bitmap");
        var good = Entries(package);
        check(good.Keys.Order().SequenceEqual(new[] { "background.png", "theme.json" }), "package contains only manifest and image; no source paths or user account data");
        check(!Encoding.UTF8.GetString(good["theme.json"]).Contains(output, StringComparison.OrdinalIgnoreCase), "manifest never exports an absolute image or user profile path");
        var noImagePath = Path.Combine(output, "无背景.rimwise-theme");
        var noImageSettings = settings with { Background = null };
        AppearanceThemePackage.Write(noImagePath, "无背景", noImageSettings, null);
        check(AppearanceThemePackage.Read(noImagePath) is { Image: null } solid && solid.Settings == noImageSettings, "themes without images are self-contained too");
        File.Delete(originalFile);
        check(AppearanceThemePackage.Read(package).Image is not null, "export remains readable after original background is removed");

        void Reject(string name, IEnumerable<KeyValuePair<string, byte[]>> entries)
        {
            var path = Path.Combine(output, name + ".rimwise-theme"); Make(path, entries);
            try { AppearanceThemePackage.Read(path); throw new Exception("Unsafe package accepted: " + name); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException or ArgumentException or FormatException)
            { check(true, "reject unsafe package: " + name); }
        }
        Dictionary<string, byte[]> Manifest(string json) => new() { ["theme.json"] = Encoding.UTF8.GetBytes(json) };
        var json = Encoding.UTF8.GetString(good["theme.json"]);
        Reject("future-schema", new Dictionary<string, byte[]>(good) { ["theme.json"] = Encoding.UTF8.GetBytes(json.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99")) });
        Reject("missing-image", new Dictionary<string, byte[]> { ["theme.json"] = good["theme.json"] });
        Reject("wrong-hash", new Dictionary<string, byte[]>(good) { ["background.png"] = Encoding.UTF8.GetBytes("not an image") });
        Reject("path-traversal", new Dictionary<string, byte[]>(good) { ["../outside.txt"] = [1] });
        Reject("unknown-file", new Dictionary<string, byte[]> { ["theme.json"] = Entries(noImagePath)["theme.json"], ["run.xaml"] = [1] });
        Reject("duplicate-entry", new[] { new KeyValuePair<string, byte[]>("theme.json", good["theme.json"]), new("theme.json", good["theme.json"]) });
        Reject("missing-schema", Manifest("{\"Name\":\"test\",\"Settings\":{}}"));
        Reject("null-settings", Manifest("{\"SchemaVersion\":1,\"Name\":\"test\",\"Settings\":null}"));
        Reject("duplicate-json-field", Manifest("{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Name\":\"test\",\"Settings\":{}}"));
        Reject("unknown-json-field", Manifest("{\"SchemaVersion\":1,\"Name\":\"test\",\"Settings\":{},\"Execute\":\"bad.exe\"}"));
        Reject("external-background", Manifest("{\"SchemaVersion\":1,\"Name\":\"test\",\"Settings\":{\"Background\":\"https://example.com/a.png\"}}"));
        Reject("absolute-background", Manifest("{\"SchemaVersion\":1,\"Name\":\"test\",\"Settings\":{\"Background\":\"C:/outside.png\"}}"));
        Reject("invalid-opacity", Manifest("{\"SchemaVersion\":1,\"Name\":\"test\",\"Settings\":{\"PanelOpacity\":2}}"));
        Reject("invalid-enum", Manifest("{\"SchemaVersion\":1,\"Name\":\"test\",\"Settings\":{\"Preset\":99}}"));
        Reject("invalid-name", Manifest("{\"SchemaVersion\":1,\"Name\":\" test\",\"Settings\":{}}"));
        Reject("zip-bomb-manifest", new Dictionary<string, byte[]> { ["theme.json"] = new byte[AppearanceThemePackage.MaximumManifestBytes + 1] });
        Reject("unreferenced-image", new Dictionary<string, byte[]> { ["theme.json"] = Entries(noImagePath)["theme.json"], ["background.png"] = image.Png });
        var fake = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0 };
        var fakeName = Convert.ToHexString(SHA256.HashData(fake)).ToLowerInvariant() + ".png";
        Reject("damaged-image-valid-hash", new Dictionary<string, byte[]> { ["theme.json"] = Encoding.UTF8.GetBytes(json.Replace(image.Name, fakeName)), ["background.png"] = fake });
        var excessive = Path.Combine(output, "oversize.rimwise-theme");
        using (var file = File.Create(excessive)) file.SetLength(AppearanceThemePackage.MaximumPackageBytes + 1L);
        try { AppearanceThemePackage.Read(excessive); throw new Exception("Oversize package accepted"); }
        catch (InvalidDataException) { check(true, "oversize archive rejected before parsing"); }
        var priorExport = File.ReadAllBytes(package);
        try { AppearanceThemePackage.Write(package, "invalid", settings, null); throw new Exception("Missing export image accepted"); }
        catch (InvalidDataException) { check(File.ReadAllBytes(package).SequenceEqual(priorExport), "failed export preserves existing destination bytes"); }

        var store = themes.SettingsStore;
        var editor = new AppearanceWindow { Owner = main, Width = 820, Height = 600 };
        try
        {
            editor.Show(); await Idle();
            var root = (FrameworkElement)editor.Content;
            Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), root), element.RenderSize);
            var reset = Bounds(editor.ResetButton); var export = Bounds(editor.ExportThemeButton); var import = Bounds(editor.ImportThemeButton);
            check(reset.Right <= export.Left && export.Right <= import.Left && import.Right < Bounds(editor.SaveButton).Left,
                "reset, export, import and save occupy one non-overlapping footer row");
            foreach (var button in new[] { editor.ResetButton, editor.ExportThemeButton, editor.ImportThemeButton, editor.SaveButton })
                check(new Rect(root.RenderSize).Contains(Bounds(button)) && button.ActualHeight >= 28,
                    "footer action stays visible at minimum size: " + button.Content);
            PrototypeSmokeCheck.SavePreview(editor, Path.Combine(output, "theme-transfer-minimum.png"));
            var pending = editor.ImportThemeAsync(package);
            check(!editor.EditorBody.IsEnabled && !editor.FooterActions.IsEnabled, "async import disables edits and duplicate actions while processing");
            check(await pending, "editor imports valid theme through production handler"); await Idle();
            check(themes.Current == settings && themes.Image?.Name == image.Name && editor.PageColor.IsEnabled == false,
                "import previews exact colors, all opacity settings and embedded background");
            await themes.WaitForBackgroundAsync(); await Idle();
            PrototypeSmokeCheck.SavePreview(editor, Path.Combine(output, "theme-import-preview.png"));
            check(!File.Exists(store.SettingsPath) && !Directory.Exists(AppearanceImage.CacheDirectory(UserStorage.DirectoryPath)),
                "import preview does not save themes or image cache without confirmation");
            check(editor.HasUnsavedChanges, "imported edits participate in existing unsaved-close prompt");
            var current = themes.Current; var currentImage = themes.Image;
            check(!await editor.ImportThemeAsync(excessive) && themes.Current == current && ReferenceEquals(themes.Image, currentImage),
                "invalid import shows feedback and leaves current preview untouched");
            check(editor.Feedback.Text.StartsWith("无法导入主题") && editor.SaveButton.IsEnabled && editor.FooterActions.IsEnabled,
                "failed import restores controls and Chinese feedback without an unhandled exception");
            var throughUi = Path.Combine(output, "导出当前编辑.rimwise-theme");
            check(await editor.ExportThemeAsync(throughUi, "当前编辑") && AppearanceThemePackage.Read(throughUi).Settings == settings,
                "export uses current unsaved editor settings and image, not only persisted preset");
            check(!File.Exists(store.SettingsPath), "export does not silently save local appearance");
            var independent = Path.Combine(output, "independent-user-profile");
            loaded.Image!.Save(independent);
            new AppearanceSettingsStore(independent).SaveNamedTheme(loaded.Name, loaded.Settings);
            File.Delete(package); File.Delete(throughUi);
            check(AppearanceImage.Load(independent, image.Name).Png.SequenceEqual(image.Png)
                && new AppearanceSettingsStore(independent).LoadAppearance() == settings,
                "saved portable theme survives deletion of package and original image source");
            check(editor.SaveAppearance(close: false, "导入主题") && store.LoadThemes().Any(theme => theme.Name == "导入主题"),
                "explicit save commits imported theme to named presets and image cache");
            check(editor.PresetBox.SelectedItem is ComboBoxItem { Content: "导入主题" }, "saved import is selected in preset list");
            editor.AccentColor.SelectedColor = "#112233"; await Idle();
        }
        finally { AppearanceSurfaceSmokeChecks.CloseDiscard(editor); }
        check(themes.Current == settings, "discarding later edits restores last explicitly saved imported theme");
        check(order.SequenceEqual(main.ViewModel.Rows.Select(row => row.PackageId)), "theme transfer never changes actual mod load order");
        check(!Directory.EnumerateFiles(output, ".rimwise-theme-*.tmp").Any(), "no export temporary files remain");
    }

    private static Dictionary<string, byte[]> Entries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(e => e.FullName, e => { using var stream = e.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray(); });
    }
    private static void Make(string path, IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries) { using var stream = archive.CreateEntry(name).Open(); stream.Write(bytes); }
    }
    private static void CheckCorners(MainWindow main, string label, Action<bool, string> check)
    {
        var handle = new WindowInteropHelper(main).Handle;
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            var kind = GetWindowRgn(handle, region);
            GetWindowRect(handle, out var rect); var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            var points = new[] { PtInRegion(region, 0, 0), PtInRegion(region, width - 1, 0), PtInRegion(region, 0, height - 1),
                PtInRegion(region, width - 1, height - 1), PtInRegion(region, width / 2, height / 2) };
            check(kind == 3 && !PtInRegion(region, 0, 0) && !PtInRegion(region, width - 1, 0)
                && !PtInRegion(region, 0, height - 1) && !PtInRegion(region, width - 1, height - 1)
                && PtInRegion(region, width / 2, height / 2), "all four native HWND corners are clipped, center retained: " + label
                    + $" (kind={kind}, size={width}x{height}, state={main.WindowState}, points={string.Join(',', points)})");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                _ = DwmGetWindowAttribute(handle, 33, out var preference, sizeof(int));
                check(preference == 2, "Windows 11 native round preference retained: " + label);
            }
        }
        finally { DeleteObject(region); }
    }
    private static async Task Idle()
    {
        await Task.Delay(280);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RectI rect);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
#endif
