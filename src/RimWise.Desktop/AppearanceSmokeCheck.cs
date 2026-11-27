#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class AppearanceSmokeCheck
{
    internal static void AttachDirect(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            var checks = new List<string>();
            try
            {
                main.LoadSample(); main.ViewModel.MarkExported();
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] {45,90,140,255,80,45,160,255,60,130,100,255,140,90,45,255}, 8);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, "frame-synthetic-background.png"))) encoder.Save(file);
                await AppearanceDirectEditSmokeChecks.Run(main, output, (value, message) =>
                { if (!value) throw new InvalidOperationException(message); checks.Add(message); });
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }));
                Application.Current.Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); main.ViewModel.MarkExported(); Application.Current.Shutdown(1); }
        };
    }
    internal static void Attach(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try { await Run(main, output); Application.Current.Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); main.ViewModel.MarkExported(); Application.Current.Shutdown(1); }
        };
    }
    private static async Task Run(MainWindow main, string output)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks.Add(message);
            File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(checks));
        }
        var themes = ((App)Application.Current).Themes;
        var original = themes.Current; var originalImage = themes.Image;
        main.LoadSample(); main.ViewModel.MarkExported();
        var order = main.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        var store = new AppearanceSettingsStore(UserStorage.DirectoryPath);
        await AppearanceSurfaceSmokeChecks.Run(main, output, Check);
        await AppearanceWindowFrameSmokeChecks.Run(main, output, Check);
        await ReleaseNotesSmokeChecks.Run(main, output, Check);
        await TopBarOpacitySmokeChecks.Run(main, output, Check);
        Check(main.AppearanceMenu.Header.ToString() == "外观" && main.AppearanceMenu.Items.Count == 0, "appearance menu name retained and opens a unified editor without legacy submenu");
        // An intentionally bright synthetic image, never a user's photograph.
        var path = Path.Combine(output, "合成 背景.png");
        var pixels = new byte[800 * 450 * 4];
        for (var y = 0; y < 450; y++) for (var x = 0; x < 800; x++)
        { var p = (y * 800 + x) * 4; pixels[p] = (byte)(180 + x % 70); pixels[p + 1] = (byte)(110 + y % 140); pixels[p + 2] = (byte)(90 + x % 160); pixels[p + 3] = 255; }
        var bitmap = BitmapSource.Create(800, 450, 96, 96, PixelFormats.Bgra32, null, pixels, 800 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        var image = AppearanceImage.Read(path);
        Check(image.Bitmap.IsFrozen && image.Name.Length == 68 && AppearanceSettings.IsColor(image.Accent), "image decoded into frozen cached bitmap and proposed color");
        var jpegPath = Path.Combine(output, "JPEG.jpg"); var jpeg = new JpegBitmapEncoder(); jpeg.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(jpegPath)) jpeg.Save(file);
        Check(AppearanceImage.Read(jpegPath).Bitmap.IsFrozen, "JPEG static background accepted");
        var oversized = Path.Combine(output, "oversized.png");
        using (var file = File.Create(oversized)) file.SetLength(AppearanceImage.MaximumBytes + 1L);
        try { AppearanceImage.Read(oversized); throw new Exception("oversized input accepted"); } catch (InvalidDataException) { checks.Add("oversized image rejected before decoding"); }
        var broken = Path.Combine(output, "broken.png"); File.WriteAllText(broken, "<script>not an image</script>");
        using (var editor = new WindowScope(new AppearanceWindow { Owner = main }))
        {
            editor.Value.Show(); await Idle();
            Check(editor.Value.Width >= 820 && editor.Value.PresetBox.Items.Count == 7, "seven integrated presets include system day and night");
            foreach (var preset in Enum.GetValues<AppearancePreset>())
            {
                editor.Value.PresetBox.SelectedIndex = (int)preset; await Idle();
                Check(themes.Current.Preset == preset, "live preset " + preset);
            }
            editor.Value.PresetBox.SelectedIndex = (int)AppearancePreset.Ocean; await Idle();
            await editor.Value.ImportAsync(path); await Idle();
            Check(themes.Image is not null && !File.Exists(store.SettingsPath)
                && !Directory.Exists(AppearanceImage.CacheDirectory(UserStorage.DirectoryPath)), "preview imports neither config nor image into persistent profile");
            Check(editor.Value.PreviewArea.ActualWidth >= 300 && editor.Value.SaveButton.ActualHeight >= 28, "preview and save remain visible at initial size");
            Check(editor.Value.AccentColor.ActualHeight is >= 32 and <= 40, "collapsed visual color selector retains a compact row height");
            var importedBitmap = themes.Image!.Bitmap;
            try { editor.Value.AccentColor.SelectedColor = "not a color"; throw new Exception("invalid color accepted"); }
            catch (InvalidDataException) { checks.Add("visual color control rejects invalid programmatic colors without manual input"); }
            editor.Value.AccentColor.SelectedColor = "#000000"; await Idle();
            var accent = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color;
            var page = ((SolidColorBrush)Application.Current.FindResource("PageBrush")).Color;
            var foreground = ((SolidColorBrush)Application.Current.FindResource("AccentTextBrush")).Color;
            var navigation = ((SolidColorBrush)Application.Current.FindResource("NavigationAccentBrush")).Color;
            Check(accent == Colors.Black && ThemeService.Contrast(navigation, page) >= 4.5 && ThemeService.Contrast(foreground, accent) >= 4.5,
                "button fills keep the exact selected color while navigation and button text remain readable");
            editor.Value.PageColor.SelectedColor = "#FFFFFF"; editor.Value.PanelColor.SelectedColor = "#000000"; editor.Value.TextColor.SelectedColor = "#777777"; await Idle();
            var text = ((SolidColorBrush)Application.Current.FindResource("TextBrush")).Color;
            page = ((SolidColorBrush)Application.Current.FindResource("PageBrush")).Color;
            var panel = ((SolidColorBrush)Application.Current.FindResource("PanelBrush")).Color;
            Check(text == Color.FromRgb(0x77, 0x77, 0x77) && panel == Colors.Black,
                "explicit text and panel colors are not silently replaced by contrast correction");
            Check(!editor.Value.PageColor.IsEnabled && editor.Value.PageColorNotice.Visibility == Visibility.Visible && editor.Value.FindName("BorderColor") is null,
                "background image disables window base color with a notice and border editor is removed");
            editor.Value.PresetBox.SelectedIndex = (int)AppearancePreset.Violet; await Idle();
            Check(editor.Value.AccentColor.SelectedColor is null && editor.Value.PageColor.SelectedColor is null
                && themes.Image is null && themes.Current.Background is null && themes.Current.PanelOpacity == new AppearanceSettings().PanelOpacity,
                "preset selection restores its complete colors opacity and absent background rather than retaining prior customizations");
            await editor.Value.ImportAsync(path); await Idle(); importedBitmap = themes.Image!.Bitmap;
            editor.Value.BlurSlider.Value = 5; editor.Value.ShadeSlider.Value = 28; editor.Value.OpacitySlider.Value = 88;
            editor.Value.PositionXSlider.Value = 72; editor.Value.PositionYSlider.Value = 34; await Idle();
            await themes.WaitForBackgroundAsync();
            Check(themes.Current.Blur == 5 && themes.Current.PositionX == .72 && themes.Current.PositionY == .34, "background blur opacity shade and positioning preview live");
            Check(ReferenceEquals(importedBitmap, themes.Image!.Bitmap), "slider changes reuse the newly selected decoded bitmap instead of re-reading the file");
            var blurredBitmap = themes.BackgroundBitmap;
            Check(blurredBitmap is { IsFrozen: true } && !ReferenceEquals(blurredBitmap, importedBitmap), "background blur is flattened into a frozen bitmap rather than a live native effect");
            editor.Value.ShadeSlider.Value = 35; editor.Value.PositionXSlider.Value = 75; await Idle();
            Check(ReferenceEquals(blurredBitmap, themes.BackgroundBitmap), "position and shade changes reuse the flattened blur without rerendering");
            var globalBeforeDrag = themes.Current;
            var globalImageBeforeDrag = themes.BackgroundBitmap;
            var globalBrush = (SolidColorBrush)main.FindResource("SurfaceBrush");
            var globalBrushColor = globalBrush.Color;
            var dragPublications = 0;
            void DragPublication(object? sender, EventArgs args) => dragPublications++;
            themes.Changed += DragPublication;
            editor.Value.BeginInteraction();
            editor.Value.OpacitySlider.Value = 0; editor.Value.ShadeSlider.Value = 64;
            editor.Value.PositionXSlider.Value = 11; editor.Value.BlurSlider.Value = 8;
            await Idle(); await editor.Value.LocalPreview.WaitForBackgroundAsync(); await Idle();
            Check(themes.Current == globalBeforeDrag && dragPublications == 0 && globalBrush.Color == globalBrushColor
                && ReferenceEquals(themes.BackgroundBitmap, globalImageBeforeDrag),
                "held drag including a long pause changes neither global settings brushes image nor notifications");
            Check(editor.Value.LocalPreview.Current.PanelOpacity == 0 && editor.Value.LocalPreview.Current.PositionX == .11
                && ((SolidColorBrush)editor.Value.PreviewArea.FindResource("SurfaceBrush")).Color.A == 0,
                "only the local preview updates during drag and reaches fully transparent panels");
            Check(!ReferenceEquals(editor.Value.PreviewArea.FindResource("SurfaceBrush"), main.FindResource("SurfaceBrush")),
                "preview owns independent resources rather than mutating a shared global brush");
            var completedPreviewBitmap = editor.Value.LocalPreview.BackgroundBitmap;
            editor.Value.EndInteraction(); await Idle();
            Check(themes.Current.PanelOpacity == 0 && themes.Current.Blur == 8 && dragPublications == 1
                && ReferenceEquals(themes.BackgroundBitmap, completedPreviewBitmap),
                "release publishes the final state once and reuses the preview blur without recalculating");
            themes.Changed -= DragPublication;
            editor.Value.BeginInteraction(); editor.Value.TextColor.SelectedColor = "#123456"; await Idle();
            Check(((SolidColorBrush)editor.Value.PreviewArea.FindResource("TextBrush")).Color == Color.FromRgb(0x12, 0x34, 0x56)
                && ((SolidColorBrush)main.FindResource("TextBrush")).Color != Color.FromRgb(0x12, 0x34, 0x56),
                "color dragging is also local and retains the exact custom text color");
            editor.Value.EndInteraction(); await Idle();
            Check(((SolidColorBrush)main.FindResource("TextBrush")).Color == Color.FromRgb(0x12, 0x34, 0x56),
                "custom text color becomes effective globally after release");
            var previewLabel = Descendants(editor.Value.PreviewArea).OfType<TextBlock>().First(block => block.Text == "主界面外观预览");
            Check(((SolidColorBrush)previewLabel.Foreground).Color == Color.FromRgb(0x12, 0x34, 0x56),
                "visible preview label actually uses the exact selected text color");
            var track = (Track)editor.Value.OpacitySlider.Template.FindName("PART_Track", editor.Value.OpacitySlider);
            var beforeRoutedDrag = themes.Current;
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            editor.Value.OpacitySlider.Value = 24; await Idle();
            Check(themes.Current == beforeRoutedDrag && editor.Value.LocalPreview.Current.PanelOpacity == .24,
                "real slider thumb routed drag-start keeps its updates local");
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent }); await Idle();
            Check(themes.Current.PanelOpacity == .24, "real slider thumb routed drag-completion synchronizes final value");
            var source = PresentationSource.FromVisual(editor.Value);
            editor.Value.OpacitySlider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            editor.Value.OpacitySlider.Value = 31; await Idle();
            Check(themes.Current.PanelOpacity == .24 && editor.Value.LocalPreview.Current.PanelOpacity == .31,
                "held keyboard adjustment also leaves the other windows unchanged");
            editor.Value.OpacitySlider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyUpEvent }); await Idle();
            Check(themes.Current.PanelOpacity == .31, "keyboard key-up synchronizes the final preview");
            editor.Value.BeginInteraction(); editor.Value.OpacitySlider.Value = 45;
            editor.Value.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent }); await Idle();
            Check(themes.Current.PanelOpacity == .45, "lost pointer capture safely finishes the local preview interaction");
            editor.Value.TextColor.SelectedColor = null; editor.Value.OpacitySlider.Value = 88; await Idle();
            var rebuilds = themes.PaletteRebuilds; var renders = themes.BackgroundRenders;
            var resourceCount = Application.Current.Resources.MergedDictionaries.Count;
            var pageBrush = Application.Current.FindResource("PageBrush");
            var publications = 0;
            void CountPublication(object? sender, EventArgs args) => publications++;
            themes.Changed += CountPublication;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) themes.Preview(themes.Current with { PositionX = i / 100d, Shade = i % 80 / 100d, PanelOpacity = .85 + i % 15 / 100d }, themes.Image);
            watch.Stop();
            Check(themes.PaletteRebuilds == rebuilds && themes.BackgroundRenders == renders
                && ReferenceEquals(pageBrush, Application.Current.FindResource("PageBrush"))
                && Application.Current.Resources.MergedDictionaries.Count == resourceCount,
                "100 background drag updates reuse the palette dictionary native theme and frozen image");
            Check(watch.Elapsed < TimeSpan.FromMilliseconds(500), "100 direct scalar preview updates finish in under 500ms: " + watch.Elapsed.TotalMilliseconds.ToString("F1"));
            await Idle(); themes.Changed -= CountPublication;
            Check(publications == 1, "100 pointer updates publish only the latest scene once at the next render pass");
            var renderBefore = themes.BackgroundRenders;
            for (var i = 0; i < 50; i++) editor.Value.BlurSlider.Value = 1 + i % 24;
            await Idle(); await themes.WaitForBackgroundAsync();
            Check(themes.Current.Blur == editor.Value.BlurSlider.Value && themes.BackgroundRenders - renderBefore <= 1,
                "rapid blur changes coalesce to one background worker result and retain the final value");
            var largePixels = new byte[2560 * 1440 * 4];
            for (var i = 0; i < largePixels.Length; i += 4)
            { largePixels[i] = (byte)(i % 255); largePixels[i + 1] = 140; largePixels[i + 2] = 90; largePixels[i + 3] = 255; }
            var largeBitmap = BitmapSource.Create(2560, 1440, 96, 96, PixelFormats.Bgra32, null, largePixels, 2560 * 4); largeBitmap.Freeze();
            var largeImage = image with { Bitmap = largeBitmap };
            var heartbeats = 0; var longestGap = TimeSpan.Zero; var heartbeatClock = System.Diagnostics.Stopwatch.StartNew();
            var lastBeat = heartbeatClock.Elapsed;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
            heartbeat.Tick += (_, _) => { var now = heartbeatClock.Elapsed; if (now - lastBeat > longestGap) longestGap = now - lastBeat; lastBeat = now; heartbeats++; };
            heartbeat.Start();
            themes.Preview(themes.Current with { Blur = 24 }, largeImage); await themes.WaitForBackgroundAsync(); heartbeat.Stop();
            Check(heartbeats >= 2 && longestGap < TimeSpan.FromMilliseconds(250) && themes.BackgroundBitmap is { IsFrozen: true, PixelWidth: 1280 },
                "2560-pixel background blur keeps the UI heartbeat alive; longest gap ms " + longestGap.TotalMilliseconds.ToString("F1"));
            themes.Preview(themes.Current with { Blur = 23 }, largeImage); await Task.Delay(125);
            themes.Preview(themes.Current with { Blur = 6 }, image); await themes.WaitForBackgroundAsync();
            Check(themes.Current.Blur == 6 && themes.BackgroundBitmap?.PixelWidth == image.Bitmap.PixelWidth,
                "superseded background jobs cannot overwrite the latest image or blur setting");
            editor.Value.ShadeSlider.Value = 35; editor.Value.OpacitySlider.Value = 88; await Idle();
            editor.Value.PlacementBox.SelectedIndex = 1; await Idle();
            Check(themes.Current.Placement == BackgroundPlacement.Fit, "fit mode preserves the whole image");
            editor.Value.PlacementBox.SelectedIndex = 0; await Idle();
            var warning = ((SolidColorBrush)Application.Current.FindResource("WarningTextBrush")).Color;
            var error = ((SolidColorBrush)Application.Current.FindResource("ErrorTextBrush")).Color;
            Check(warning != error && error != ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color, "warning error and accent remain distinct");
            await editor.Value.ImportAsync(broken); await Idle();
            Check(themes.Image is not null && !editor.Value.Feedback.Text.Contains("正在处理"), "malformed image leaves previous valid preview intact");
            editor.Value.Width = 820; editor.Value.Height = 600; await Idle();
            Check(editor.Value.PreviewArea.ActualWidth > 220 && editor.Value.SaveButton.IsVisible, "minimum-size appearance editor retains usable preview and footer");
            PrototypeSmokeCheck.SavePreview(editor.Value, Path.Combine(output, "外观设置-最小尺寸.png"));
            editor.Value.Width = 980; editor.Value.Height = 730; await Idle();
            PrototypeSmokeCheck.SavePreview(editor.Value, Path.Combine(output, "外观设置-紫罗兰.png"));
            PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, "主界面-背景预览.png"));
            editor.Value.AccentColor.IsExpanded = true; await Idle();
            Check(Application.Current.Windows.Count == 2 && editor.Value.AccentColor.IsExpanded, "color selection opens inline rather than a clipped secondary modal");
            editor.Value.AccentColor.SelectedColor = "#112233"; await Idle();
            Check(editor.Value.SaveButton.IsEnabled && editor.Value.AccentColor.SelectedColor == "#112233", "selecting a color leaves the main save action enabled");
            PrototypeSmokeCheck.SavePreview(editor.Value, Path.Combine(output, "内联选色器.png"));
            editor.Value.PanelColor.IsExpanded = true; await Idle();
            Check(!editor.Value.AccentColor.IsExpanded && editor.Value.PanelColor.IsExpanded, "advanced surfaces use the same inline picker and only one selector expands");
            editor.Value.PanelColor.IsExpanded = false;
            editor.Value.RemoveBackgroundButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Check(themes.Image is null && !editor.Value.ImageAccentButton.IsEnabled, "remove background clears preview without touching the original file");
            Check(editor.Value.PageColor.IsEnabled && editor.Value.PageColorNotice.Visibility == Visibility.Collapsed,
                "removing background enables the window color editor again");
            editor.Value.PageColor.SelectedColor = "#123456"; editor.Value.TextColor.SelectedColor = "#FFFFFF";
            editor.Value.OpacitySlider.Value = 0; await Idle();
            Check(((SolidColorBrush)main.FindResource("PageBrush")).Color == Color.FromRgb(0x12, 0x34, 0x56)
                && ((SolidColorBrush)main.FindResource("SurfaceBrush")).Color.A == 0,
                "solid background color and zero panel opacity work without an imported image");
            editor.Value.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Check(themes.Current == new AppearanceSettings() && !File.Exists(store.SettingsPath), "restore defaults is preview-only until explicitly saved");
        }
        await Idle();
        Check(themes.Current == original && ReferenceEquals(themes.Image, originalImage) && !File.Exists(store.SettingsPath), "cancel and close roll back theme and image without saving");
        using (var editor = new WindowScope(new AppearanceWindow { Owner = main }))
        {
            editor.Value.Show(); editor.Value.PresetBox.SelectedIndex = (int)AppearancePreset.Ocean; await Idle();
            await editor.Value.ImportAsync(path); await Idle();
            editor.Value.AccentColor.SelectedColor = "#4398DB"; editor.Value.PageColor.SelectedColor = "#15242F";
            editor.Value.PanelColor.SelectedColor = "#203440"; editor.Value.TextColor.SelectedColor = "#E7EEEE";
            await Idle();
            Check(editor.Value.SaveAppearance(close: false, "合成主题一"), "explicit named save commits appearance and all four visual color choices");
            Check(editor.Value.PresetBox.Items.Count == 8 && ((ComboBoxItem)editor.Value.PresetBox.SelectedItem).Content.ToString() == "合成主题一",
                "saving adds and selects the named theme in the presets list");
        }
        var saved = new AppearanceSettingsStore(UserStorage.DirectoryPath).LoadAppearance();
        Check(saved.Preset == AppearancePreset.Ocean && saved.Background == image.Name, "saved settings retain preset and content-addressed background reference");
        Check(saved.Accent == "#4398DB" && saved.Page == "#15242F" && saved.Panel == "#203440"
            && saved.Text == "#E7EEEE" && saved.Border is null, "four selected colors survive reload and obsolete border override is cleared");
        var themeStore = new AppearanceSettingsStore(UserStorage.DirectoryPath);
        Check(themeStore.ActiveThemeName == "合成主题一" && themeStore.LoadThemes().Single().Settings == saved,
            "named preset and active theme persist together in the user data configuration");
        Check(!File.ReadAllText(store.SettingsPath).Contains(output, StringComparison.OrdinalIgnoreCase), "appearance settings contain no original image path or uploaded image data");
        File.Delete(path);
        Check(AppearanceImage.Load(UserStorage.DirectoryPath, image.Name).Bitmap.IsFrozen, "saved background survives deletion of original source");
        using (var restored = new ThemeService(Application.Current, new AppearanceSettingsStore(UserStorage.DirectoryPath)))
            Check(restored.Current == saved && restored.Image is not null && restored.LoadWarning is null, "restart restores custom theme and image without external file");
        themes.Preview(saved, themes.Image);
        var before = File.ReadAllBytes(store.SettingsPath);
        var cancelNaming = new ThemeNameWindow(main, null);
        cancelNaming.Loaded += (_, _) =>
        {
            cancelNaming.NameBox.Text = " "; cancelNaming.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(cancelNaming.IsVisible && cancelNaming.ThemeName is null, "blank theme name is rejected without closing the prompt");
            cancelNaming.DialogResult = false;
        };
        Check(cancelNaming.ShowDialog() == false && File.ReadAllBytes(store.SettingsPath).SequenceEqual(before),
            "canceling the theme-name prompt leaves user data unchanged");
        using (var editor = new WindowScope(new AppearanceWindow { Owner = main }))
        {
            editor.Value.Show(); editor.Value.AccentColor.SelectedColor = "#F0B040"; await Idle();
        }
        Check(File.ReadAllBytes(store.SettingsPath).SequenceEqual(before) && themes.Current == saved, "cancel over saved appearance leaves bytes and appearance unchanged");
        var fakeName = new string('f', 64) + ".png";
        themes.SettingsStore.SaveAppearance(saved with { Background = fakeName });
        using (var missing = new ThemeService(Application.Current, new AppearanceSettingsStore(UserStorage.DirectoryPath)))
            Check(missing.Image is null && missing.LoadWarning is not null, "missing cached background falls back to themed solid surface without startup failure");
        var damagedPath = Path.Combine(AppearanceImage.CacheDirectory(UserStorage.DirectoryPath), fakeName);
        File.WriteAllText(damagedPath, "corrupted");
        using (var damaged = new ThemeService(Application.Current, new AppearanceSettingsStore(UserStorage.DirectoryPath)))
            Check(damaged.Image is null && damaged.LoadWarning is not null, "damaged cached image does not crash startup or replace the valid image");
        themes.Preview(saved, AppearanceImage.Load(UserStorage.DirectoryPath, image.Name));
        themes.SettingsStore.SaveNamedTheme("合成主题一", saved);
        Exception? modalFailure = null;
        var modal = new AppearanceWindow { Owner = main, Width = 820, Height = 600 };
        modal.Loaded += async (_, _) =>
        {
            try
            {
                modal.AccentColor.IsExpanded = true;
                var swatches = ((StackPanel)modal.AccentColor.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                swatches[5].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                modal.PageColor.SelectedColor = "#27221B"; modal.PanelColor.SelectedColor = "#342D23";
                modal.TextColor.SelectedColor = "#FFFFFF"; modal.OpacitySlider.Value = 0;
                await Idle();
                var top = modal.SaveButton.TranslatePoint(new Point(), (UIElement)modal.Content);
                Check(top.Y >= 0 && top.Y + modal.SaveButton.ActualHeight <= ((FrameworkElement)modal.Content).ActualHeight,
                    "main save button remains fully in bounds at minimum size with inline selection expanded");
                var namingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                namingTimer.Tick += (_, _) =>
                {
                    if (Application.Current.Windows.OfType<ThemeNameWindow>().SingleOrDefault() is not { } naming) return;
                    namingTimer.Stop();
                    Check(File.ReadAllBytes(store.SettingsPath).SequenceEqual(before), "opening save-name prompt does not persist until confirmed");
                    naming.NameBox.Text = "合成主题二";
                    PrototypeSmokeCheck.SavePreview(naming, Path.Combine(output, "保存主题-命名.png"));
                    naming.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                before = File.ReadAllBytes(store.SettingsPath);
                namingTimer.Start(); modal.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); namingTimer.Stop();
            }
            catch (Exception ex) { modalFailure = ex; AppearanceSurfaceSmokeChecks.CloseDiscard(modal); }
        };
        Check(modal.ShowDialog() == true && modalFailure is null, "real modal save button commits selected colors and closes successfully");
        var afterModal = new AppearanceSettingsStore(UserStorage.DirectoryPath).LoadAppearance();
        Check(afterModal.Accent == "#DDA044" && afterModal.Page == "#27221B" && afterModal.Panel == "#342D23"
            && afterModal.Text == "#FFFFFF" && afterModal.Border is null && afterModal.PanelOpacity == 0,
            "real modal named save persists custom text fully transparent panels and no border override");
        using (var editor = new WindowScope(new AppearanceWindow { Owner = main }))
        {
            editor.Value.Show(); await Idle();
            Check(editor.Value.PresetBox.Items.Count == 9 && ((ComboBoxItem)editor.Value.PresetBox.SelectedItem).Content.ToString() == "合成主题二",
                "reopening selects the persisted custom theme and lists both named presets");
            editor.Value.PresetBox.SelectedIndex = 7; await Idle(); await themes.WaitForBackgroundAsync();
            Check(themes.Current == saved && themes.Image is not null,
                "selecting an older named preset restores its colors and archived image after the original was deleted");
            editor.Value.BeginInteraction(); editor.Value.BlurSlider.Value = 19; editor.Value.OpacitySlider.Value = 12; await Idle();
        }
        await Idle(); await themes.WaitForBackgroundAsync();
        Check(themes.Current == afterModal, "closing during a held drag restores the saved global state and cancels scoped jobs");
        Check(order.SequenceEqual(main.ViewModel.Rows.Select(row => row.PackageId)), "all appearance operations preserve current mod order");
        await AppearanceDirectEditSmokeChecks.Run(main, output, Check);
        await AppearanceSurfaceSmokeChecks.CheckClosingSave(main, Check);
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }));
        main.ViewModel.MarkExported();
    }
    private static async Task Idle()
    {
        await Task.Delay(260);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class WindowScope(AppearanceWindow value) : IDisposable
    {
        internal AppearanceWindow Value => value;
        public void Dispose() => AppearanceSurfaceSmokeChecks.CloseDiscard(value);
    }
}
#endif
