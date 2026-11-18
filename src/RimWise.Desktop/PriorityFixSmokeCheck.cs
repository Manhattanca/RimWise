#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Game;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class PriorityFixSmokeCheck
{
    internal static void Attach(MainWindow owner, string output)
    {
        owner.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                await VersionCompatibilitySmokeCheck.RunAsync(owner, output);
                owner.LoadSample();
                var window = new AiSortingWindow(owner.ViewModel, UserStorage.DirectoryPath) { Owner = owner };
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                window.Show();
                try
                {
                    window.ShowDependencyReasons(Enumerable.Range(1, 20).Select(i => $"前置模组 {i}：由当前启用模组要求，等待 Steam 完成下载。").ToArray());
                    window.StatusText.Text = GameTestStatus.Describe(new(39560, 1190, 0) { Phase = "loading" });
                    foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                    {
                        ((App)Application.Current).Themes.Select(theme, save: false);
                        await owner.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                        window.StatusScroll.ScrollToBottom();
                        await owner.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                        var visual = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        visual.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(visual));
                        using (var file = File.Create(Path.Combine(output, $"progress-{theme}.png"))) encoder.Save(file);
                        var status = Bounds(window.StatusText, window);
                        var details = Bounds(window.StatusScroll, window);
                        Assert(status.Height >= 40 && status.Bottom <= details.Top,
                            "Live status is clipped or inside prerequisite scrolling.");
                        Assert(window.AcceptedList.ActualHeight >= 36, "Minimum-size result list collapsed.");
                        foreach (var control in new FrameworkElement[] { window.StatusText, window.AutomaticButton, window.CancelButton, window.ApplyButton, window.CloseButton })
                        {
                            var bounds = Bounds(control, window);
                            Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight,
                                $"Minimum-size controls extend outside the window: {control.Name}; bounds={bounds}; window={window.ActualWidth}x{window.ActualHeight}");
                        }
                    }
                }
                finally { window.Close(); }
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    passed = true,
                    checks = new[] { "deferred real-save version guard", "precision enrichment", "actual build and revision rejection", "byte-exact undo/redo",
                        "fixed status independent of prerequisite scrolling", "minimum-size controls in light and dark themes" }
                }));
                owner.ViewModel.MarkExported(); Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                owner.ViewModel.MarkExported(); Application.Current.Shutdown(1);
            }
        };
    }

    private static Rect Bounds(FrameworkElement element, Window window) => new(element.TranslatePoint(new Point(), window), element.RenderSize);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
