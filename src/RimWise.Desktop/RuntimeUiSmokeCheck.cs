#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

/// <summary>Synthetic rows and local estimates only; no game files, secret store, or network requests.</summary>
internal static class RuntimeUiSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try { await RunAsync(window, output); Application.Current.Shutdown(0); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task RunAsync(MainWindow window, string output)
    {
        window.LoadSample();
        var vm = window.ViewModel;
        var ids = vm.Rows.Select(row => row.PackageId).Concat(["available.workshop", "available.local"]).ToArray();
        var mods = ids.Select((id, i) => new ModInstallation(Path.Combine(output, "样例", id), id,
            i == 0 ? ModSource.Game : i % 2 == 0 ? ModSource.Local : ModSource.Workshop, "",
            new(id, "合成作者", id, ["1.7"], "", ModRequirements.Empty, false), [])
            { CodeKind = i % 2 == 0 ? ModCodeKind.Xml : ModCodeKind.CSharp }).ToArray();
        vm.ApplyScan(new ModScanResult(new(null, "", ""), GameVersion.Parse("1.7"), new(mods), []));
        window.SetBadgeDisplay(ModBadgeDisplay.Icons);
        foreach (var row in vm.Rows) row.SetActivity(true, true);
        foreach (var row in vm.AvailableMods) row.SetActivity(true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var revision = vm.ContentRevision;
        var order = vm.Rows.Select(row => row.PackageId).ToArray();
        var estimate = new AiTokenEstimator(output).Estimate(new(AiSettings.Default with { Model = "synthetic-model" },
            null, "1.7", order, ids, [], []));
        var checks = new List<string>();
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            await Idle(window);
            foreach (var grid in new[] { window.AvailableGrid, window.OrderGrid })
            {
                foreach (var name in new[] { "SourceIconHitArea", "CodeKindHitArea", "AddedIconHitArea", "UpdatedIconHitArea" })
                {
                    var areas = Descendants(grid).OfType<Border>().Where(b => b.Name == name && b.IsVisible).ToArray();
                    Assert(areas.Length > 0, "Missing icon: " + name);
                    foreach (var area in areas)
                    {
                        var expected = Meaning(area);
                        Assert(area.ToolTip is string text && text == expected && text.Length > 0, "Wrong icon meaning: " + name);
                        // Probe both blank corners and glyph centers through the complete grid hierarchy.
                        foreach (var point in new[] { new Point(1, 1), new Point(area.ActualWidth / 2, area.ActualHeight / 2),
                            new Point(area.ActualWidth - 1, area.ActualHeight - 1) })
                        {
                            var hit = grid.InputHitTest(area.TranslatePoint(point, grid)) as DependencyObject;
                            while (hit is not null && (hit is not FrameworkElement element || element.ToolTip is null))
                                hit = VisualTreeHelper.GetParent(hit);
                            Assert(ReferenceEquals(hit, area), "Row tooltip covers icon hit area: " + name);
                        }
                        Assert(ToolTipService.GetInitialShowDelay(area) <= 200 && ToolTipService.GetShowDuration(area) >= 60000,
                            "Icon tooltip timing missing.");
                    }
                    var first = areas[0];
                    var tooltip = new ToolTip { Content = first.ToolTip, PlacementTarget = first, IsOpen = true };
                    try
                    {
                        await Idle(window);
                        Assert(tooltip.ActualWidth > 0 && tooltip.ActualHeight > 0, "Tooltip did not render.");
                        PrototypeSmokeCheck.SavePreview(tooltip, Path.Combine(output, $"{theme}-{grid.Name}-{name}.png"));
                    }
                    finally { tooltip.IsOpen = false; }
                }
                checks.Add(theme + " " + grid.Name + " four icon meanings and full hit targets");
            }
            var diagnosticRow = vm.Rows.First();
            foreach (var severity in new[] { DiagnosticSeverity.Warning, DiagnosticSeverity.Error })
            {
                diagnosticRow.SetDiagnostics([new("fixture", severity, "合成警告原因：缺少必要前置。")], true);
                await Idle(window);
                var area = Descendants(window.OrderGrid).OfType<Border>()
                    .Single(b => b.Name == "DiagnosticHitArea" && b.DataContext == diagnosticRow);
                Assert(area.InputHitTest(new Point(area.ActualWidth / 2, area.ActualHeight / 2)) == area,
                    "Warning hit target missing.");
                var tooltip = (ToolTip)area.ToolTip;
                tooltip.PlacementTarget = area;
                tooltip.IsOpen = true;
                try
                {
                    await Idle(window);
                    Assert(tooltip.Content is TextBlock text && text.Text == diagnosticRow.DiagnosticTooltip, "Warning reason lost.");
                    PrototypeSmokeCheck.SavePreview(tooltip, Path.Combine(output, $"{theme}-{severity}-原因.png"));
                }
                finally { tooltip.IsOpen = false; }
            }
            checks.Add(theme + " warning/error reasons preserved");
            var usage = new AiUsageWindow(estimate) { Owner = window, Width = 440, Height = 320 };
            usage.Show();
            try
            {
                await Idle(usage);
                Assert(usage.EstimateText.Text.Contains("样本不足") && usage.BreakdownText.Text.Contains("输入"), "Estimate uncertainty missing.");
                Assert(usage.EstimateText.ActualWidth > 200 && usage.EstimateText.ActualHeight > 0, "Usage layout collapsed.");
                PrototypeSmokeCheck.SavePreview(usage, Path.Combine(output, theme + "-用量预估最小尺寸.png"));
                var nextTheme = theme == ThemePreference.Light ? ThemePreference.Dark : ThemePreference.Light;
                ((App)Application.Current).Themes.Select(nextTheme, save: false);
                await Idle(usage);
                Assert(usage.Background is SolidColorBrush actual && Application.Current.FindResource("PageBrush") is SolidColorBrush expected
                    && actual.Color == expected.Color, "Open usage window failed hot theme switch.");
            }
            finally { usage.Close(); }
            checks.Add(theme + " local usage preview narrow layout and hot theme switch");
        }
        Assert(vm.ContentRevision == revision && vm.Rows.Select(row => row.PackageId).SequenceEqual(order), "Tooltips changed order/history.");
        await ImportAndThumbnailSmokeCheck.RunAsync(window, output, checks);
        await MissingModDownloadSmokeCheck.RunAsync(window, output, checks);
        vm.MarkExported();
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Meaning(Border area) => (area.DataContext, area.Name) switch
    {
        (InstalledModRow row, "SourceIconHitArea") => row.SourceTooltip,
        (InstalledModRow row, "CodeKindHitArea") => row.CodeKindTooltip,
        (InstalledModRow row, "AddedIconHitArea") => row.AddedTooltip,
        (InstalledModRow row, "UpdatedIconHitArea") => row.UpdatedTooltip,
        (LoadOrderRow row, "SourceIconHitArea") => row.SourceTooltip,
        (LoadOrderRow row, "CodeKindHitArea") => row.CodeKindTooltip,
        (LoadOrderRow row, "AddedIconHitArea") => row.AddedTooltip,
        (LoadOrderRow row, "UpdatedIconHitArea") => row.UpdatedTooltip,
        _ => throw new InvalidOperationException("Unexpected icon row")
    };
    private static async Task Idle(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
#endif
