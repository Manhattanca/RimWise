#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class GameLogUiSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try { await Run(window, output); Application.Current.Shutdown(0); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                window.ViewModel.MarkExported(); Application.Current.Shutdown(1);
            }
        };
    }
    private static async Task Run(MainWindow window, string output)
    {
        window.LoadSample();
        var revision = window.ViewModel.ContentRevision;
        var order = window.ViewModel.Rows.Select(r => r.PackageId).ToArray();
        var startup = Path.Combine(output, "启动 日志.log");
        var runtime = Path.Combine(output, "游戏中 日志.log");
        File.WriteAllText(startup, "2026-11-05 12:00:00 INFO 游戏启动\n2026-11-05 12:00:01 WARN 可选内容未加载\n2026-11-05 12:00:02 ERROR 合成测试错误\n  at Example.Run()\n");
        File.WriteAllText(runtime, "2026-11-05 12:01:00 INFO 地图已载入\n");
        window.LogsPageTab.IsChecked = true;
        await Idle(window);
        var page = (GameLogPage)window.GameLogHost.Content;
        page.SelectFile(startup);
        await WaitRead(page, 4);
        Assert(window.ModEditorBody.Visibility == Visibility.Collapsed && window.ModEditorFooter.Visibility == Visibility.Collapsed, "Editor must hide");
        page.LinesList.SelectedIndex = 2;
        Assert(page.DetailText.Text.Contains("合成测试错误"), "Detail selection");
        page.SearchText.Text = "合成";
        await Task.Delay(240);
        Assert(page.VisibleCountForCheck == 1, "Search");
        page.SearchText.Clear();
        page.LevelFilter.SelectedIndex = 1;
        await Task.Delay(240);
        Assert(page.VisibleCountForCheck == 2, "Error and stack filter");
        page.LevelFilter.SelectedIndex = 0;
        await Task.Delay(240);
        File.AppendAllText(startup, "2026-11-05 12:00:03 INFO 新增日志\n");
        await WaitRead(page, 5);
        page.RuntimeTab.IsChecked = true;
        page.SelectFile(runtime);
        await WaitRead(page, 1);
        page.StartupTab.IsChecked = true;
        await WaitRead(page, 5);
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            foreach (var size in new[] { (1360d, 820d), (960d, 640d) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                await Idle(window);
                Assert(page.LinesList.ActualWidth > 250 && page.LinesList.ActualHeight > 80, "Log list clipped");
                Assert(page.AnalyzeButton.IsVisible && page.AnalyzeButton.ActualWidth > 100, "Analysis control missing");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"logs-{theme}-{size.Item1}.png"));
            }
        }
        window.ModsPageTab.IsChecked = true;
        await Idle(window);
        Assert(window.ModEditorBody.IsVisible && window.ModEditorFooter.IsVisible && !page.IsVisible, "Page return");
        Assert(revision == window.ViewModel.ContentRevision && order.SequenceEqual(window.ViewModel.Rows.Select(r => r.PackageId)), "Editor state changed");
        window.ViewModel.MarkExported();
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true,
            Checks = new[] { "Live append", "Search", "Severity and stack filter", "Source cache", "Details", "Light/dark narrow/wide layout", "Editor preserved" } }));
    }
    private static async Task WaitRead(GameLogPage page, int count)
    {
        for (var i = 0; i < 30; i++)
        {
            await page.ReadForCheckAsync();
            if (page.VisibleCountForCheck == count) return;
            await Task.Delay(50);
        }
        throw new Exception($"Read expected {count}, got {page.VisibleCountForCheck}");
    }
    private static async Task Idle(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
#endif
