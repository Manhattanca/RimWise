#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop;

internal static class AutomaticLogSmokeCheck
{
    internal static async Task Run(MainWindow owner, string output)
    {
        var profile = Path.Combine(output, "自动日志 配置");
        var destination = Path.Combine(output, "自动日志 导出");
        Directory.CreateDirectory(profile);
        var store = new AutomaticSortingLogStore(profile);
        store.SaveSettings(new(false, destination));
        var window = new AiSortingWindow(owner.ViewModel, profile) { Owner = owner };
        string? opened = null;
        window.OpenLogFolder = path => opened = path;
        window.Show();
        try
        {
            Assert(window.AutoSaveLogsToggle.IsChecked == false, "Automatic export must default off");
            window.AutoSaveLogsToggle.IsChecked = true;
            window.AutoSaveLogsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(new AutomaticSortingLogStore(profile).LoadSettings().AutoSave, "Toggle did not persist");
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                window.SettingsScroll.ScrollToBottom();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                foreach (var control in new FrameworkElement[] { window.AutoSaveLogsToggle, window.SaveLastLogsButton, window.OpenLogsButton, window.DeleteAllLogsButton, window.CloseButton })
                {
                    var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                    Assert(bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight && bounds.Right <= window.ActualWidth,
                        "Log controls exceed minimum window bounds");
                }
                Assert(window.LogSettingsPanel.IsVisible, "Log actions must always be visible");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"{theme}-自动排序日志.png"));
            }
            var changedDestination = Path.Combine(output, "新 日志目录");
            var paths = new PathSettingsWindow(new GamePaths(), new PathSettingsStore(profile));
            Assert(paths.LogDirectoryBox.Text == destination, "Path settings did not load export destination");
            paths.Owner = window;
            paths.Show();
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                paths.Width = paths.MinWidth; paths.Height = paths.MinHeight;
                await paths.Dispatcher.InvokeAsync(paths.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert(paths.LogDirectoryBox.IsVisible && paths.LogDirectoryBox.ActualWidth > 200, "Log path field is not readable");
                PrototypeSmokeCheck.SavePreview(paths, Path.Combine(output, $"{theme}-日志路径设置.png"));
            }
            paths.LogDirectoryBox.Text = changedDestination;
            paths.SaveLogDirectory();
            paths.Close();
            Assert(store.LoadSettings().AutoSave, "Changing path lost automatic export preference");
            destination = changedDestination;
            window.OpenLogsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(opened == destination && Directory.Exists(destination), "Open folder ignored newly saved settings");
            await window.SaveLastLogsAsync(true);
            Assert(opened == destination && Directory.GetFiles(destination, "*.zip").Length == 1 && !window.IsWorking,
                "Manual save failed to export/open folder/reset busy state: " + window.LogStatusText.Text);
            opened = null;
            await window.SaveLastLogsAsync(false);
            Assert(opened is null && Directory.GetFiles(destination, "*.zip").Length == 2, "Automatic save opens folder or overwrites prior export");
            await window.DeleteAllLogsAsync();
            Assert(Directory.GetFiles(destination, "*.zip").Length == 0 && !window.IsWorking, "Delete did not clear exported logs");
            var blockedPath = Path.Combine(output, "blocked-export");
            store.SaveSettings(new(true, blockedPath));
            File.WriteAllText(blockedPath, "Synthetic blocker");
            await window.SaveLastLogsAsync(true);
            Assert(opened is null && !window.IsWorking && window.LogStatusText.Text.Contains("排序结果不受影响"),
                "Export failure affected sorting state");
        }
        finally { window.Close(); }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
#endif
