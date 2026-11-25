#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>In-process synthetic input only. No game, AI, network, real profile or OS preference changes.</summary>
internal static class MotionPolishSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                await Run(window, output);
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task Run(MainWindow window, string output)
    {
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        var timings = new List<object>();
        window.LoadSample();
        var themes = ((App)Application.Current).Themes;
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        foreach (var reduced in new[] { false, true })
        {
            InterfaceMotion.ReduceMotionForCheck = reduced;
            themes.Select(theme, save: false);
            await Idle(window);
            var label = $"{theme}/reduced={reduced}";
            var originalOrder = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
            var probe = new Border { Width = 180, Height = 65, Background = Brushes.Teal, Opacity = .63 };
            window.ModInteractionOverlay.Children.Add(probe);
            await Idle(window);
            InterfaceMotion.SlidePage(probe, true);
            var offset = probe.RenderTransform as TranslateTransform;
            Check(offset is not null, "Default presentation owns only a translation " + label);
            Check(probe.Opacity == .63 && probe.Width == 180 && probe.Height == 65,
                "Page motion preserves alpha and layout bounds " + label);
            Check(!InterfaceMotion.Enabled || offset!.X is >= 0 and <= 12 && offset.HasAnimatedProperties,
                "Page entrance is limited to twelve DIP " + label);
            await Task.Delay(35);
            var running = offset!.HasAnimatedProperties;
            var before = offset.X;
            InterfaceMotion.SlidePage(probe, false);
            Check(!InterfaceMotion.Enabled || !running || Math.Abs(before - offset.X) < .01,
                "Interrupted page resumes from current presentation without jumping " + label);
            for (var i = 0; i < 60; i++) InterfaceMotion.SlidePage(probe, i % 2 == 0);
            await Task.Delay(240);
            Check(offset.X == 0 && !offset.HasAnimatedProperties && probe.Opacity == .63,
                "Sixty replacements settle and release clocks " + label);
            var callerTransform = new ScaleTransform(1.1, 1.1);
            probe.RenderTransform = callerTransform;
            InterfaceMotion.SlidePage(probe, true);
            Check(probe.RenderTransform is TransformGroup { Children.Count: 2 } group
                && ReferenceEquals(group.Children[0], callerTransform), "Caller transform survives page motion " + label);
            probe.Visibility = Visibility.Collapsed;
            Check(probe.RenderTransform.Value == callerTransform.Value && !callerTransform.HasAnimatedProperties,
                "Hidden page drops its presentation offset immediately " + label);
            probe.Visibility = Visibility.Visible;
            InterfaceMotion.RevealNotification(probe);
            await Task.Delay(35);
            var alpha = probe.Opacity;
            InterfaceMotion.RevealNotification(probe);
            Check(probe.Opacity >= alpha - .001, "Repeated reveal never restarts at zero opacity " + label);
            probe.Visibility = Visibility.Collapsed;
            Check(probe.Opacity == .63 && !probe.HasAnimatedProperties, "Dismissed notice releases fade clock " + label);
            window.ModInteractionOverlay.Children.Remove(probe);

            var button = new ProbeButton { Content = "轻量动效验证", Width = 170, Height = 38,
                Style = (Style)window.FindResource(typeof(Button)) };
            button.Resources["ButtonAccentBrush"] = new SolidColorBrush(Color.FromArgb(0, 80, 180, 180));
            // The production drag overlay deliberately rejects input. Enable it only for this
            // isolated probe so hit-testing measures the button template, not its parent veto.
            var overlayHitTest = window.ModInteractionOverlay.IsHitTestVisible;
            window.ModInteractionOverlay.IsHitTestVisible = true;
            window.ModInteractionOverlay.Children.Add(button);
            await Idle(window);
            var chrome = (Border)button.Template.FindName("Chrome", button);
            var hover = (Border)button.Template.FindName("HoverFace", button);
            Check(ButtonFeedbackMotion.TintOpacity(false, false) == 0
                && ButtonFeedbackMotion.TintOpacity(false, true) == .22
                && ButtonFeedbackMotion.TintOpacity(true, true) == 1
                && ButtonFeedbackMotion.TintOpacity(true, false) == 1,
                "Button tint state matrix is idle / shallow hover / deeper press " + label);
            Check(chrome.RenderTransform is ScaleTransform && button.RenderTransform.Value.IsIdentity,
                "Only button presentation face scales, not outer hit area " + label);
            var scale = (ScaleTransform)chrome.RenderTransform;
            var clicks = 0; button.Click += (_, _) => clicks++;
            button.Focus();
            var beforeScale = scale.ScaleX; var beforeTint = hover.Opacity;
            button.Press(true);
            Check(!InterfaceMotion.Enabled || Math.Abs(scale.ScaleX - beforeScale) < .001 && Math.Abs(hover.Opacity - beforeTint) < .001,
                "New button clocks do not expose their endpoint before the first render tick " + label);
            button.ClickNow();
            Check(clicks == 1 && button.IsKeyboardFocused && button.ActualWidth == 170 && button.ActualHeight == 38,
                "Command and focus stay immediate during press feedback " + label);
            await Task.Delay(140);
            Check(Math.Abs(scale.ScaleX - .985) < .001 && chrome.Opacity == 1 && button.Opacity == 1,
                "Press is subtle and leaves text opacity unchanged " + label);
            Check(hover.Opacity == 1, "Actual button press reaches the deeper tint endpoint " + label);
            Check(button.InputHitTest(new Point(.5, .5)) is not null,
                "Outer button corner remains a hit target while pressed " + label);
            Check(hover.BorderBrush is SolidColorBrush { Color.A: 0 } && hover.Child is Border
                { Background: SolidColorBrush { Color.A: 0 }, Opacity: .16 },
                "Hover stroke and subtle fill both respect zero-alpha appearance " + label);
            for (var i = 0; i < 40; i++) button.Press(i % 2 == 0);
            button.Press(false);
            await Task.Delay(230);
            Check(scale.ScaleX == 1 && scale.ScaleY == 1 && !scale.HasAnimatedProperties && !hover.HasAnimatedProperties && clicks == 1,
                "Rapid press replacements clean up and never synthesize clicks " + label);
            button.Press(true); button.IsEnabled = false;
            Check(scale.ScaleX == 1 && !scale.HasAnimatedProperties, "Disabling button stops press feedback immediately " + label);
            window.ModInteractionOverlay.Children.Remove(button);
            window.ModInteractionOverlay.IsHitTestVisible = overlayHitTest;
            Check(chrome.RenderTransform.Value.IsIdentity && !scale.HasAnimatedProperties,
                "Unloaded button restores presentation transform " + label);

            window.UpdateNotice.Visibility = Visibility.Collapsed;
            window.ShowUpdateNotification("动效测试", "不连接更新源", false);
            await Task.Delay(35);
            var noticeAlpha = window.UpdateNotice.Opacity;
            window.ShowUpdateNotification("状态更新", "不应重新入场", false);
            Check(window.UpdateNotice.Opacity >= noticeAlpha - .001,
                "Visible update notice changes text without replaying entrance " + label);
            window.UpdateNotice.Visibility = Visibility.Collapsed;
            Check(window.UpdateNotice.Opacity == 1 && !window.UpdateNotice.HasAnimatedProperties,
                "Actual notification dismisses without retained clocks " + label);

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 30; i++)
            {
                window.LogsPageTab.IsChecked = true;
                await Idle(window);
                window.ModsPageTab.IsChecked = true;
                await Idle(window);
                Check(!window.MainPageTabs.IndicatorAnimatesLayout, "Tab width never animates layout " + label + "/" + i);
            }
            timings.Add(new { Theme = theme.ToString(), Reduced = reduced, SixtyPageSelectionsMs = watch.Elapsed.TotalMilliseconds });
            await Task.Delay(240);
            Check(Math.Abs(window.MainPageTabs.IndicatorX - window.ModsPageTab.TranslatePoint(new Point(), window.MainPageTabs).X) < 1
                && Math.Abs(window.MainPageTabs.IndicatorWidth - window.ModsPageTab.ActualWidth) < 1,
                "Traveling indicator settles on the final selected tab " + label);
            Check(originalOrder.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)),
                "Navigation and visual feedback never change actual mod order " + label);
            Check(window.ModEditorBody.RenderTransform.Value.IsIdentity && window.GameLogHost.RenderTransform.Value.IsIdentity,
                "Visible and hidden pages retain no offsets " + label);
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"motion-{theme}-{reduced}.png"));
        }
        InterfaceMotion.ReduceMotionForCheck = false;
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, Timings = timings,
            Note = "Synthetic commands and isolated profile; dispatch/layout timings are not display FPS or a real-device latency guarantee." }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Idle(FrameworkElement element) =>
        await element.Dispatcher.InvokeAsync(element.UpdateLayout, DispatcherPriority.ApplicationIdle);

    private sealed class ProbeButton : Button
    {
        internal void Press(bool value) => IsPressed = value;
        internal void ClickNow() => OnClick();
    }
}
#endif
