#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class ThemeSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, PathSettingsWindow settings, string output)
    {
        var application = (App)Application.Current;
        var themes = application.Themes;
        var original = themes.Preference;
        var order = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        try
        {
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                themes.Select(preference, save: false);
                await Idle(window);
                if (!order.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)))
                    throw new InvalidOperationException("Theme switch modified the loaded order.");
                if (settings.Background != window.Background)
                    throw new InvalidOperationException("Open settings window did not change theme with the main window.");
                var name = preference == ThemePreference.Light ? "日间" : "夜间";
                if (window.OrderGrid.ActualHeight < 180 || window.OrderStatusPanel.ActualHeight < 24 || window.AvailableGrid.ActualWidth <= 0)
                    throw new InvalidOperationException("Live status hid the mod lists at minimum size.");
                var names = Descendants<TextBlock>(window.OrderGrid).ToArray();
                foreach (var (text, resource) in new[] { ("示例框架", "WarningTextBrush"), ("example.stories", "ErrorTextBrush") })
                    if (names.Single(item => item.Text == text).Foreground is not SolidColorBrush actual
                        || actual.Color != ((SolidColorBrush)window.FindResource(resource)).Color)
                        throw new InvalidOperationException("Row severity color did not follow the live theme.");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, name + "主界面.png"));
                PrototypeSmokeCheck.SavePreview(settings, Path.Combine(output, name + "路径设置.png"));

                // Render the actual menu popup template. This verifies its surface and text,
                // without depending on foreground mouse capture in a background check.
                window.FileMenu.ApplyTemplate();
                if (window.FileMenu.Template.FindName("PART_Popup", window.FileMenu) is not Popup popup)
                    throw new InvalidOperationException("Menu popup template missing.");
                popup.IsOpen = true;
                await Idle(window);
                if (popup.Child is not FrameworkElement content || content.ActualWidth <= 0)
                    throw new InvalidOperationException("Menu popup not rendered.");
                PrototypeSmokeCheck.SavePreview(content, Path.Combine(output, name + "文件菜单.png"));
                popup.IsOpen = false;
            }

            // Simulate both OS application theme settings without modifying the user's registry.
            var simulatedDark = false;
            using (var simulated = new ThemeService(application, new AppearanceSettingsStore(output), () => simulatedDark))
            {
                simulated.Select(ThemePreference.System, save: false);
                if (simulated.IsDark) throw new InvalidOperationException("System light setting not applied.");
                simulatedDark = true;
                simulated.RefreshSystemTheme();
                if (!simulated.IsDark) throw new InvalidOperationException("System change was not applied live.");
                simulated.Select(ThemePreference.Light, save: false);
                simulated.RefreshSystemTheme();
                if (simulated.IsDark) throw new InvalidOperationException("System theme overrode manual selection.");
            }
        }
        finally { themes.Select(original, save: false); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        // Fluent expand/collapse animations outlive a dispatcher-idle tick.
        await Task.Delay(300);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
#endif
