#if DEBUG
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>Real editor/save handlers, isolated profile and synthetic pictures only.</summary>
internal static class AppearancePresetSmokeCheck
{
    internal static void Attach(MainWindow main, string output)
    {
        main.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            var checks = new List<string>();
            void Check(bool value, string label)
            {
                if (!value) throw new InvalidOperationException(label);
                checks.Add(label);
                File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(checks));
            }
            try
            {
                await Run(main, output, Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }));
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
        var store = themes.SettingsStore;
        var source = Path.Combine(output, "合成 临时原图.png");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 30, 90, 140, 255, 80, 45, 160, 255, 60, 130, 100, 255, 140, 90, 45, 255 }, 8);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(source)) png.Save(file);
        var picture = AppearanceImage.Read(source);
        var a = new AppearanceSettings
        {
            Preset = AppearancePreset.Dark, Background = picture.Name,
            Accent = "#337f73", Text = "#eeddbb", Page = "#203040", Panel = "#405060",
            Placement = BackgroundPlacement.Fit, PositionX = .237654321, PositionY = .6987654321,
            Shade = .3123456789, Blur = 3.7, PanelOpacity = .317654321, OtherUiOpacity = .427654321,
            Transparency = new() { Inputs = new() { Mode = SurfaceOpacityMode.Custom, Opacity = .23 } },
            Controls = new AppearanceControlOverrides().With("mods.SearchBox", new() { Mode = SurfaceOpacityMode.Custom, Opacity = .17 })
        };
        var b = new AppearanceSettings
        {
            Preset = AppearancePreset.Violet, PanelOpacity = .62, OtherUiOpacity = .73,
            Transparency = new() { Lists = new() { Mode = SurfaceOpacityMode.Opaque } },
            Controls = new AppearanceControlOverrides().With("mods.OrderGrid", new() { Mode = SurfaceOpacityMode.Custom, Opacity = .61 })
        };
        themes.Save(a, picture, "图片与小数透明度");
        themes.Save(b, null, "无图独立透明度");
        themes.ActivateTheme(a, picture, "图片与小数透明度");
        File.Delete(source);
        var archive = Path.Combine(AppearanceImage.CacheDirectory(UserStorage.DirectoryPath), picture.Name);
        var archiveHash = Hash(archive); var archiveTime = File.GetLastWriteTimeUtc(archive);
        var definitions = store.LoadThemes().ToArray();
        check(File.Exists(archive) && !File.Exists(source), "named picture survives deletion of its synthetic original source");

        var editor = new AppearanceWindow { Owner = main }; editor.Show(); await Idle();
        try
        {
            check(editor.CanActivateSelectedTheme && !editor.HasUnsavedChanges, "fractional values and lowercase colors are not accidental edits on reopen");
            var configHash = Hash(store.SettingsPath);
            foreach (var preset in Enum.GetValues<AppearancePreset>())
            {
                Select(editor, "图片与小数透明度"); await Idle();
                editor.PresetBox.SelectedIndex = (int)preset; await Idle();
                check(themes.Current == new AppearanceSettings { Preset = preset } && themes.Image is null,
                    "builtin restores its complete definition and removes prior image/opacity/overrides: " + preset);
                check(editor.PageColor.IsEnabled && editor.CanActivateSelectedTheme && !editor.HasUnsavedChanges,
                    "builtin is a selection, not an edit; page color is enabled: " + preset);
            }
            Select(editor, "图片与小数透明度"); await Idle();
            check(themes.Current == a && themes.Image?.Name == picture.Name, "named A restores picture, scalar and advanced transparency together");
            Select(editor, "无图独立透明度"); await Idle();
            check(themes.Current == b && themes.Image is null, "named B restores its own no-image definition without A leakage");
            editor.OtherOpacitySlider.Value = 12; await Idle();
            check(editor.HasUnsavedChanges && !editor.CanActivateSelectedTheme, "an actual slider edit requires named saving");
            Select(editor, "图片与小数透明度"); await Idle();
            check(!editor.HasUnsavedChanges && editor.CanActivateSelectedTheme && themes.Current == a,
                "dirty-to-preset replaces all unsaved overrides and returns to selection mode");
            check(Hash(store.SettingsPath) == configHash, "previewing preset choices never writes profile or template definitions");
            Select(editor, "无图独立透明度"); await Idle();
            check(!editor.HasUnsavedChanges && themes.Current == b, "a different preset remains a selection before closing without Save");
            editor.Close();
            check(!editor.IsVisible && themes.Current == a, "closing a pure selection needs no save prompt and restores the baseline");
        }
        finally { if (editor.IsVisible) AppearanceSurfaceSmokeChecks.CloseDiscard(editor); }

        foreach (var preset in Enum.GetValues<AppearancePreset>())
        {
            await SaveExisting(main, e => e.PresetBox.SelectedIndex = (int)preset, check);
            var fresh = new AppearanceSettingsStore(UserStorage.DirectoryPath);
            check(fresh.ActiveThemeName is null && fresh.LoadAppearance() == new AppearanceSettings { Preset = preset },
                "save persists builtin selection across a fresh store: " + preset);
            check(fresh.LoadThemes().SequenceEqual(definitions), "applying builtin does not overwrite any named definition: " + preset);
        }
        foreach (var name in new[] { "图片与小数透明度", "无图独立透明度", "图片与小数透明度" })
        {
            await SaveExisting(main, e => Select(e, name), check);
            var fresh = new AppearanceSettingsStore(UserStorage.DirectoryPath);
            var expected = definitions.Single(t => t.Name == name).Settings;
            check(fresh.ActiveThemeName == name && fresh.LoadAppearance() == expected && themes.Current == expected,
                "save activates existing named theme exactly without duplicating it: " + name);
            check(fresh.LoadThemes().SequenceEqual(definitions), "named templates remain unchanged after activation: " + name);
        }
        check(Hash(archive) == archiveHash && File.GetLastWriteTimeUtc(archive) == archiveTime,
            "activation does not rewrite or recompress the archived background");

        var edited = new AppearanceWindow { Owner = main }; edited.Show(); await Idle();
        edited.PanelOpacitySliderForCheck(check);
        var namingCount = 0;
        using (WatchDialogs(edited, _ => namingCount++)) edited.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(namingCount == 1 && edited.IsVisible && edited.HasUnsavedChanges, "saving a real edit still opens naming, and cancel keeps it unsaved");
        check(edited.SaveAppearance(false, "另存实际修改") && store.LoadThemes().Single(t => t.Name == "图片与小数透明度").Settings == a,
            "save-as creates a separate theme and preserves the selected original template");
        AppearanceSurfaceSmokeChecks.CloseDiscard(edited);

        var transfer = Path.Combine(output, "原版值导入.rimwise-theme");
        AppearanceThemePackage.Write(transfer, "导入主题", new(), null);
        var imported = new AppearanceWindow { Owner = main }; imported.Show(); await Idle();
        check(await imported.ImportThemeAsync(transfer), "synthetic theme imports through the real editor");
        check(!imported.CanActivateSelectedTheme && imported.HasUnsavedChanges, "import is a new theme even when its values equal a builtin");
        namingCount = 0;
        using (WatchDialogs(imported, _ => namingCount++)) imported.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(namingCount == 1 && imported.IsVisible, "imported theme Save uses naming rather than silently applying a builtin");
        AppearanceSurfaceSmokeChecks.CloseDiscard(imported);

        // A concurrent writer must not be overwritten by an editor holding stale definitions.
        var stale = new AppearanceWindow { Owner = main }; stale.Show(); await Idle();
        stale.PresetBox.SelectedIndex = (int)AppearancePreset.Light; await Idle();
        var concurrent = new AppearanceSettingsStore(UserStorage.DirectoryPath);
        concurrent.SaveNamedTheme("并发写入保留", b);
        var concurrentHash = Hash(concurrent.SettingsPath);
        stale.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(stale.IsVisible && stale.Feedback.Text.StartsWith("无法保存外观设置：", StringComparison.Ordinal),
            "failed activation keeps the editor open and reports concurrent-save refusal");
        check(Hash(concurrent.SettingsPath) == concurrentHash, "failed activation does not overwrite the concurrent writer");
        AppearanceSurfaceSmokeChecks.CloseDiscard(stale);
        check(main.ViewModel.Rows.Select(row => row.PackageId).SequenceEqual(order), "theme choices do not change MOD loading order");

        var isolated = new AppearanceSettingsStore(Path.Combine(output, "独立存储校验"));
        isolated.SaveNamedTheme("原定义", a);
        isolated.ActivateTheme(a, "原定义");
        var before = Hash(isolated.SettingsPath);
        foreach (var invalid in new[] { "不存在", "原定义" })
        {
            var refused = false;
            try { isolated.ActivateTheme(a with { PanelOpacity = .22 }, invalid); }
            catch (InvalidDataException) { refused = true; }
            check(refused && Hash(isolated.SettingsPath) == before, "unknown or mismatched named activation is refused without mutation: " + invalid);
        }
        isolated.ActivateTheme(a, "原定义");
        check(isolated.LoadThemes().Count == 1 && isolated.LoadThemes()[0].Settings == a,
            "storage activation preserves named template data exactly");
    }

    private static void PanelOpacitySliderForCheck(this AppearanceWindow editor, Action<bool, string> check)
    {
        editor.OpacitySlider.Value = 54;
        check(editor.HasUnsavedChanges && !editor.CanActivateSelectedTheme, "changing named theme opacity is an actual edit");
    }

    private static async Task SaveExisting(MainWindow main, Action<AppearanceWindow> select, Action<bool, string> check)
    {
        var editor = new AppearanceWindow { Owner = main };
        Exception? error = null; var prompts = 0;
        using var guard = WatchDialogs(editor, _ => prompts++);
        editor.ContentRendered += async (_, _) =>
        {
            try
            {
                select(editor); await Idle();
                check(editor.CanActivateSelectedTheme && !editor.HasUnsavedChanges, "unchanged chosen preset is directly applicable");
                editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (editor.IsVisible) AppearanceSurfaceSmokeChecks.CloseDiscard(editor);
            }
            catch (Exception ex) { error = ex; if (editor.IsVisible) AppearanceSurfaceSmokeChecks.CloseDiscard(editor); }
        };
        var result = editor.ShowDialog();
        if (error is not null) throw new InvalidOperationException("modal save action failed", error);
        check(result == true && prompts == 0, "actual Save button applies and closes without naming or overwrite dialogs");
        await Idle();
    }

    private static void Select(AppearanceWindow editor, string name) => editor.PresetBox.SelectedItem = editor.PresetBox.Items
        .OfType<ComboBoxItem>().Single(item => item.Tag is NamedAppearanceTheme theme && theme.Name == name);
    private static IDisposable WatchDialogs(AppearanceWindow editor, Action<Window> seen)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (var window in Application.Current.Windows.OfType<Window>().Where(w => w.Owner == editor).ToArray())
            {
                seen(window);
                if (window is ThemeNameWindow naming) naming.DialogResult = false;
                else if (window is MessageDialogWindow dialog)
                    Descendants(dialog).OfType<Button>().Single(b => Equals(b.Tag, MessageBoxResult.No)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
        timer.Start(); return new StopTimer(timer);
    }
    private sealed class StopTimer(DispatcherTimer timer) : IDisposable { public void Dispose() => timer.Stop(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task Idle()
    {
        await Task.Delay(280);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
#endif
