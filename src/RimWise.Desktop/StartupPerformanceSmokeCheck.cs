#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Formats;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

/// <summary>Controlled large catalogue; never discovers the user's game or contacts AI.</summary>
internal static class StartupPerformanceSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                await RunAsync(window, output);
                Application.Current.Shutdown(0);
            }
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
        const int installed = 5000;
        const int enabled = 1500;
        var watch = Stopwatch.StartNew();
        var mods = Enumerable.Range(0, installed).Select(index => new ModInstallation(Path.Combine(output, "虚拟模组", index.ToString()), index.ToString(),
            ModSource.Local, "", new("中文模组 " + index, "测试作者", "perf." + index, ["1.7"], "", ModRequirements.Empty, false), [])
            { LastUpdatedUtc = DateTimeOffset.UnixEpoch, CodeKind = ModCodeKind.Xml }).ToArray();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("<ModsConfigData><version>1.7</version><activeMods>"
            + string.Concat(mods.Take(enabled).Select(mod => "<li>" + mod.PackageId + "</li>")) + "</activeMods></ModsConfigData>"));
        var config = new ModsConfigXmlFormat().Read(input);
        var scan = new ModScanResult(new(GameVersion.Parse("1.7"), "1.7", ""), GameVersion.Parse("1.7"), new(mods), []);
        watch.Restart();
        window.ViewModel.Load(config, Path.Combine(output, "ModsConfig.xml"));
        var loadMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        window.ViewModel.ApplyScan(scan);
        var applyMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var layoutMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        window.ViewModel.ApplyScan(scan);
        var reapplyMs = watch.Elapsed.TotalMilliseconds;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        if (window.ViewModel.Rows.Count != enabled || window.ViewModel.AvailableMods.Count != installed - enabled)
            throw new InvalidOperationException("Large catalogue lost rows.");
        var scrollFrames = new List<double>();
        var maximumRealizedRows = 0;
        foreach (var available in new[] { true, false })
        {
            var scroll = window.ScrollForCheck(available) ?? throw new InvalidOperationException("Missing list scroll viewer.");
            var grid = available ? window.AvailableGrid : window.OrderGrid;
            for (var i = 0; i < 60; i++)
            {
                watch.Restart();
                // Keep each sample at one 38px row regardless of the configured scroll unit.
                var distance = System.Windows.Controls.VirtualizingPanel.GetScrollUnit(grid)
                    == System.Windows.Controls.ScrollUnit.Pixel ? 38d : 1d;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + (i < 30 ? distance : -distance));
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                scrollFrames.Add(watch.Elapsed.TotalMilliseconds);
                maximumRealizedRows = Math.Max(maximumRealizedRows, CountRows(grid));
            }
        }
        if (maximumRealizedRows >= 200) throw new InvalidOperationException("Scrolling materialized too many rows.");
        scrollFrames.Sort();
        var report = new { Passed = true, Installed = installed, Enabled = enabled, LoadMs = loadMs, ApplyMs = applyMs,
            LayoutMs = layoutMs, ReapplyMs = reapplyMs, ScrollMedianMs = scrollFrames[scrollFrames.Count / 2],
            ScrollP95Ms = scrollFrames[(int)(scrollFrames.Count * .95)], MaximumRealizedRows = maximumRealizedRows,
            Note = "Synthetic in-memory mods; one-row (38px) scroll layout latency; excludes EXE startup, disk enumeration and physical wheel input." };
        File.WriteAllText(Path.Combine(output, "performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        window.ViewModel.MarkExported();
        await FastStartupSmokeCheck.RunAsync(window, output);
        await StartupCacheSmokeCheck.RunAsync(window, output);
    }

    private static int CountRows(DependencyObject node)
    {
        var count = node is System.Windows.Controls.DataGridRow ? 1 : 0;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            count += CountRows(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        return count;
    }
}
#endif
