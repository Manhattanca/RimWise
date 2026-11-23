#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>Only synthetic windows and isolated themes; never captures the desktop or user windows.</summary>
internal static class AppearanceWindowFrameSmokeChecks
{
    internal static async Task Run(MainWindow main, string output, Action<bool, string> check)
    {
        var themes = ((App)Application.Current).Themes;
        var baseline = themes.Current; var baselineImage = themes.Image;
        var imagePath = Path.Combine(output, "frame-synthetic-background.png");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 45, 90, 140, 255, 80, 45, 160, 255, 60, 130, 100, 255, 140, 90, 45, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(imagePath)) encoder.Save(file);
        var image = AppearanceImage.Read(imagePath);
        // A saturated owner makes accidental backdrop/owner bleed distinguishable from the page.
        var owner = new Window { Owner = main, Width = 1050, Height = 800,
            Background = Brushes.Magenta, Content = new Border { Background = Brushes.Magenta,
                Child = new TextBlock { Text = "合成后窗 · 不应穿透", FontSize = 64, Foreground = Brushes.Lime } } };
        owner.Show(); await Idle();
        try
        {
            foreach (var (preset, picture) in new[] { (AppearancePreset.Violet, false), (AppearancePreset.Light, false), (AppearancePreset.Violet, true) })
            {
                var scenario = preset + (picture ? "-image" : "-solid");
                themes.Preview(new AppearanceSettings { Preset = preset, PanelOpacity = 0, OtherUiOpacity = 0,
                    Background = picture ? image.Name : null }, picture ? image : null);
                var editor = new AppearanceWindow { Owner = owner };
                try
                {
                    editor.Show();
                    var presentation = SecondaryWindowPresentation.GetPresentation(editor);
                    check(presentation is not null && await presentation.Ready, "appearance secondary window uses the normal first-frame reveal");
                    await Idle();
                    CaptureEdges(editor, output, scenario + "-initial", check);
                    // Full-bleed backing is a layout invariant, not just an initial HWND clear color.
                    var root = (FrameworkElement)editor.Content;
                    check(root.Margin == new Thickness(0) && root is Border { Background: SolidColorBrush { Color.A: 255 } },
                        "appearance backing covers the complete client area outside content padding");
                    foreach (var state in new[] { "minimum", "maximized", "restored" })
                    {
                        if (state == "minimum") { editor.Width = editor.MinWidth; editor.Height = editor.MinHeight; }
                        editor.WindowState = state == "maximized" ? WindowState.Maximized : WindowState.Normal;
                        await Idle();
                        CaptureEdges(editor, output, scenario + "-" + state, check);
                        var saveRect = new Rect(editor.SaveButton.TranslatePoint(new Point(), root), editor.SaveButton.RenderSize);
                        check(new Rect(root.RenderSize).Contains(saveRect), "save button remains fully in client bounds: " + state);
                        var settingsScroll = Descendants(editor).OfType<ScrollViewer>().Single(scroll => IsAncestor(scroll, editor.PresetBox));
                        editor.AccentColor.IsExpanded = true; await Idle();
                        settingsScroll.ScrollToBottom(); await Idle();
                        var position = editor.PositionYSlider.TranslatePoint(new Point(), settingsScroll);
                        check(settingsScroll.ScrollableHeight > 0 && Math.Abs(settingsScroll.VerticalOffset - settingsScroll.ScrollableHeight) < 1
                            && position.Y >= 0 && position.Y + editor.PositionYSlider.ActualHeight <= settingsScroll.ActualHeight,
                            "bottom settings stay reachable with expanded color controls: " + state);
                        settingsScroll.ScrollToTop(); editor.AccentColor.IsExpanded = false; await Idle();
                        check(settingsScroll.VerticalOffset == 0 && editor.PresetBox.ActualHeight > 0,
                            "settings return to the top without moving the fixed footer: " + state);
                    }
                    // Resource updates must repaint all four margins, not expose old owner pixels.
                    editor.PresetBox.SelectedIndex = (int)AppearancePreset.Ocean;
                    // Programmatic selection has no mouse-up. Model the completed gesture
                    // before fencing the globally committed theme (not the local-only preview).
                    editor.EndInteraction();
                    check(themes.Current.Preset == AppearancePreset.Ocean, "completed theme selection synchronizes the global palette before native capture");
                    await Idle();
                    CaptureEdges(editor, output, scenario + "-live-theme", check);
                }
                finally { AppearanceSurfaceSmokeChecks.CloseDiscard(editor); }
            }
        }
        finally { owner.Close(); themes.Preview(baseline, baselineImage); }
    }

    private static void CaptureEdges(AppearanceWindow editor, string output, string name, Action<bool, string> check)
    {
        var dpi = VisualTreeHelper.GetDpi(editor);
        var pixels = NativeStartupFrameCapture.CaptureEdges(editor, Path.Combine(output, "appearance-native-" + name + ".png"));
        var page = ((SolidColorBrush)editor.FindResource("PageBrush")).Color;
        var actualBrush = ((Border)editor.Content).Background as SolidColorBrush;
        File.WriteAllText(Path.Combine(output, "edge-resources-" + name + ".json"), System.Text.Json.JsonSerializer.Serialize(new {
            Page = page.ToString(), Root = actualBrush?.Color.ToString(), Window = (editor.Background as SolidColorBrush)?.Color.ToString(),
            Same = ReferenceEquals(actualBrush, editor.FindResource("PageBrush")), Global = ((App)Application.Current).Themes.Current.Preset.ToString(),
            Local = editor.LocalPreview.Current.Preset.ToString(), Frozen = actualBrush?.IsFrozen }));
        check(pixels.All(pixel => pixel == page), "native appearance edges are opaque page pixels, not owner contents: " + name
            + " (" + string.Join(",", pixels) + "; expected " + page + "; DPI " + dpi.DpiScaleX + ")");
        check(editor.Opacity == 1 && !editor.AllowsTransparency, "window opacity and native composition remain unchanged: " + name);
    }

    private static bool IsAncestor(DependencyObject ancestor, DependencyObject target)
    { for (var node = target; node is not null; node = VisualTreeHelper.GetParent(node)) if (node == ancestor) return true; return false; }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static async Task Idle()
    {
        await Task.Delay(260);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        // Dispatcher idle alone is not a native present fence after a Fluent light/dark change.
        // Rendering is raised BEFORE that frame is submitted. A second frame fences
        // the previous WPF submission; DwmFlush alone only waits for DWM's existing queue.
        for (var frame = 0; frame < 2; frame++)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            handler = (_, _) => { CompositionTarget.Rendering -= handler; rendered.TrySetResult(); };
            CompositionTarget.Rendering += handler;
            try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { CompositionTarget.Rendering -= handler; }
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        NativeStartupFrameCapture.FlushComposition();
    }
}
#endif
