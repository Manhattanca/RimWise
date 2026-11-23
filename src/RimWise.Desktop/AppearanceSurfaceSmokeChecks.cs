#if DEBUG
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class AppearanceSurfaceSmokeChecks
{
    internal static async Task Run(MainWindow main, string output, Action<bool, string> check)
    {
        var themes = ((App)Application.Current).Themes;
        var editor = new AppearanceWindow { Owner = main }; editor.Show(); await Idle();
        try
        {
            check(!editor.HasUnsavedChanges, "opening visual opacity editor is not itself an edit");
            check(editor.AdvancedOpacityButton.Content.ToString() == "透明度高级设置"
                && editor.FindName("OpacityMap") is null, "advanced opacity is a real-UI entry rather than the old diagram");
            editor.OpacitySlider.Value = 37; editor.OtherOpacitySlider.Value = 0; await Idle();
            check(Alpha("SurfaceBrush") == 94 && Alpha("ChromeSurfaceBrush") == 94,
                "panel and menu/footer follow panel opacity independently of other controls");
            check(Alpha("ListSurfaceBrush") == 94 && Alpha("ButtonSurfaceBrush") == 255,
                "lists follow panel opacity while default buttons ignore other UI opacity");
            foreach (var key in new[] { "InputSurfaceBrush", "SelectorSurfaceBrush", "ContentSurfaceBrush" })
                check(Alpha(key) == 0, "other-UI unified zero reaches " + key);
            editor.SetSurfaceOpacity(AppearanceSurface.Inputs, new() { Mode = SurfaceOpacityMode.Custom }); await Idle();
            editor.BeginInteraction(); editor.SetSurfaceOpacity(AppearanceSurface.Inputs, new() { Mode = SurfaceOpacityMode.Custom, Opacity = .25 }); await Idle();
            check(Alpha("InputSurfaceBrush") == 255 && Alpha("InputSurfaceBrush", editor.PreviewArea) == 64,
                "independent-region drag only updates the scoped preview while held");
            editor.EndInteraction(); await Idle();
            check(Alpha("InputSurfaceBrush") == 64 && Alpha("SelectorSurfaceBrush") == 0,
                "input override commits on release without changing selectors or lists");
            editor.SetSurfaceOpacity(AppearanceSurface.Selectors, new() { Mode = SurfaceOpacityMode.Opaque }); await Idle();
            check(Alpha("SelectorSurfaceBrush") == 255 && Alpha("InputSurfaceBrush") == 64,
                "selector opaque mode is independent from custom input opacity");
            foreach (var surface in Enum.GetValues<AppearanceSurface>())
            {
                editor.SetSurfaceOpacity(surface, new() { Mode = SurfaceOpacityMode.Custom, Opacity = 0 }); await Idle();
                check(themes.Current.SurfaceOpacity(surface) == 0, "legacy category settings retain custom zero: " + surface);
            }
            check(main.MainMenu.Background is SolidColorBrush { Color.A: 0 }
                && main.ModEditorFooter.Background is SolidColorBrush { Color.A: 0 }, "real top menu and bottom footer follow the bars rule");
            check(main.SearchBox.Background is SolidColorBrush { Color.A: 0 }, "real mod search uses transparent input surface");
            var rowBrush = (SolidColorBrush)new MarkerBackgroundConverter().Convert(
                ["#EF705F", 1, main.OrderGrid.RowBackground], typeof(Brush), "", CultureInfo.InvariantCulture);
            check(rowBrush.Color.A == 0, "marker and alternating-row tints preserve transparent alpha instead of forcing opaque rows");
            check(main.OrderGrid.Background is SolidColorBrush { Color.A: 0 } && main.OrderGrid.RowBackground is SolidColorBrush { Color.A: 0 },
                "list scaffold and rows do not put an opaque floor under the input controls");
            check(main.OrderGrid.Opacity == 1 && main.SearchBox.Opacity == 1, "transparent backgrounds never fade text or thumbnails with control opacity");
            foreach (var slider in Descendants(editor).OfType<Slider>())
            {
                slider.ApplyTemplate(); var track = slider.Template.FindName("PART_Track", slider) as Track;
                check(!slider.IsMoveToPointEnabled && track is not null && track.IncreaseRepeatButton.Command is null
                    && track.DecreaseRepeatButton.Command is null && !track.IncreaseRepeatButton.IsHitTestVisible,
                    "appearance track has no click/hold page action: " + System.Windows.Automation.AutomationProperties.GetName(slider));
                var before = slider.Value; track!.IncreaseRepeatButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                check(slider.Value == before, "track clicks cannot alter slider value");
            }
            editor.OtherOpacitySlider.Value = 0; editor.OtherOpacitySlider.Focus();
            Slider.IncreaseSmall.Execute(null, editor.OtherOpacitySlider);
            check(editor.OtherOpacitySlider.Value == editor.OtherOpacitySlider.SmallChange, "keyboard small-step remains available for opacity");
            editor.AccentColor.IsExpanded = true; await Idle();
            var hue = Descendants(editor.AccentColor).OfType<Slider>().Single();
            check(hue.Background is LinearGradientBrush { GradientStops.Count: 7 }, "hue slider visibly renders the seven-stop spectrum");
            editor.AccentColor.IsExpanded = false; await Idle();
            PrototypeSmokeCheck.SavePreview(editor, Path.Combine(output, "visual-opacity-editor.png"));
            check(editor.HasUnsavedChanges, "independent transparency adjustments are unsaved edits");
            using (Answer(editor, MessageBoxResult.Cancel)) editor.Close();
            check(editor.IsVisible && editor.HasUnsavedChanges, "cancel close confirmation keeps unsaved editor open");
            using (Answer(editor, MessageBoxResult.Yes, cancelNaming: true)) editor.Close();
            check(editor.IsVisible && editor.HasUnsavedChanges, "cancelling theme naming also cancels the editor close");
        }
        finally { CloseDiscard(editor); }
        static byte Alpha(string key, FrameworkElement? scope = null) => ((SolidColorBrush)(scope?.FindResource(key) ?? Application.Current.FindResource(key))).Color.A;
    }
    internal static void CloseDiscard(AppearanceWindow window) { using (Answer(window, MessageBoxResult.No)) window.Close(); }
    internal static async Task CheckClosingSave(MainWindow main, Action<bool, string> check)
    {
        var themes = ((App)Application.Current).Themes;
        var editor = new AppearanceWindow { Owner = main }; editor.Show(); await Idle();
        editor.SetSurfaceOpacity(AppearanceSurface.Selectors, new() { Mode = SurfaceOpacityMode.Custom, Opacity = .29 }); await Idle();
        using (Answer(editor, MessageBoxResult.Yes, name: "关闭保存独立配置")) editor.Close();
        var store = new AppearanceSettingsStore(UserStorage.DirectoryPath);
        check(!editor.IsVisible && store.ActiveThemeName == "关闭保存独立配置"
            && store.LoadAppearance().SurfaceOpacity(AppearanceSurface.Selectors) == .29,
            "save on close uses the naming transaction and persists independent region opacity");
        var failed = new AppearanceWindow { Owner = main }; failed.Show(); await Idle();
        failed.OtherOpacitySlider.Value = 13;
        // A competing writer is a real expected save failure, not a bypass in the editor.
        store.SaveNamedTheme("并发写入测试", store.LoadAppearance());
        using (Answer(failed, MessageBoxResult.Yes, name: "保存失败不得关闭")) failed.Close();
        check(failed.IsVisible && failed.HasUnsavedChanges && failed.Feedback.Text.StartsWith("无法保存外观设置：", StringComparison.Ordinal),
            "failed atomic save on close preserves the open editor and all unsaved changes");
        CloseDiscard(failed);
        check(themes.Current.OtherUiOpacity != .13, "discard restores the last successfully saved theme after a failed save");
    }
    private static IDisposable Answer(AppearanceWindow owner, MessageBoxResult choice, bool cancelNaming = false, string? name = null)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<MessageDialogWindow>().Where(window => window.Owner == owner).ToArray())
                Descendants(dialog).OfType<Button>().Single(button => Equals(button.Tag, choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (cancelNaming)
                foreach (var naming in Application.Current.Windows.OfType<ThemeNameWindow>().Where(window => window.Owner == owner).ToArray()) naming.DialogResult = false;
            else if (name is not null)
                foreach (var naming in Application.Current.Windows.OfType<ThemeNameWindow>().Where(window => window.Owner == owner).ToArray())
                { naming.NameBox.Text = name; naming.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        };
        timer.Start(); return new TimerScope(timer);
    }
    private sealed class TimerScope(DispatcherTimer timer) : IDisposable { public void Dispose() => timer.Stop(); }
    private static async Task Idle() { await Task.Delay(260); await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
#endif
