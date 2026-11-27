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
    internal static void Attach(MainWindow window, string output, bool focused = false)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                await RunAsync(window, output, focused);
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

    private static async Task RunAsync(MainWindow window, string output, bool focused)
    {
        // Experimental developer-only cache size. No environment switch is read by
        // normal product startup; compare a bounded overscan against the same binary.
        var cacheExperiment = focused ? Environment.GetEnvironmentVariable("RIMWISE_PERF_CACHE_PAGES") : null;
        if (double.TryParse(cacheExperiment, System.Globalization.CultureInfo.InvariantCulture, out var cachePages)
            && cachePages >= 0 && cachePages <= 1)
            foreach (var grid in new[] { window.AvailableGrid, window.OrderGrid })
            {
                System.Windows.Controls.VirtualizingPanel.SetCacheLengthUnit(grid, System.Windows.Controls.VirtualizationCacheLengthUnit.Page);
                System.Windows.Controls.VirtualizingPanel.SetCacheLength(grid, new(cachePages));
            }
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
        var originalOrder = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        var scrollFrames = new List<double>();
        var maximumRealizedRows = 0;
        var rapidScrollFrames = new List<double>();
        var samples = new List<object>();
        var renderReports = new List<object>();
        var allocation = GC.GetTotalAllocatedBytes();
        var gen0 = GC.CollectionCount(0);
        var maximumVisuals = 0;
        var phaseAllocations = new List<object>();
        foreach (var available in new[] { true, false })
        {
            var scroll = window.ScrollForCheck(available) ?? throw new InvalidOperationException("Missing list scroll viewer.");
            var grid = available ? window.AvailableGrid : window.OrderGrid;
            var phaseAllocation = GC.GetTotalAllocatedBytes();
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
                samples.Add(new { Available = available, Kind = "one-row", Index = i,
                    Ms = scrollFrames[^1], Offset = scroll.VerticalOffset });
            }
            phaseAllocations.Add(new { Available = available, Kind = "one-row", Bytes = GC.GetTotalAllocatedBytes() - phaseAllocation });
            phaseAllocation = GC.GetTotalAllocatedBytes();
            // Far-apart offsets approximate fast scrollbar scrubbing, not just a gentle wheel.
            for (var i = 0; i < 30; i++)
            {
                watch.Restart();
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * ((i * 17) % 29) / 28);
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                rapidScrollFrames.Add(watch.Elapsed.TotalMilliseconds);
                maximumRealizedRows = Math.Max(maximumRealizedRows, CountRows(grid));
                samples.Add(new { Available = available, Kind = "jump", Index = i,
                    Ms = rapidScrollFrames[^1], Offset = scroll.VerticalOffset });
            }
            phaseAllocations.Add(new { Available = available, Kind = "jump", Bytes = GC.GetTotalAllocatedBytes() - phaseAllocation });
            maximumVisuals = Math.Max(maximumVisuals, CountVisuals(grid));
            scroll.ScrollToTop();
            if (focused) renderReports.Add(await MeasureContinuousScrollAsync(window, scroll));
        }
        if (maximumRealizedRows >= 200) throw new InvalidOperationException("Scrolling materialized too many rows.");
        if (!originalOrder.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)))
            throw new InvalidOperationException("Scrolling changed the actual load order.");
        scrollFrames.Sort();
        rapidScrollFrames.Sort();
        var report = new { Passed = true, Installed = installed, Enabled = enabled, LoadMs = loadMs, ApplyMs = applyMs,
            LayoutMs = layoutMs, ReapplyMs = reapplyMs, ScrollMedianMs = scrollFrames[scrollFrames.Count / 2],
            ScrollP95Ms = scrollFrames[(int)(scrollFrames.Count * .95)], MaximumRealizedRows = maximumRealizedRows,
            RapidScrollMedianMs = rapidScrollFrames[rapidScrollFrames.Count / 2],
            RapidScrollP95Ms = rapidScrollFrames[(int)(rapidScrollFrames.Count * .95)],
            AllocatedBytes = GC.GetTotalAllocatedBytes() - allocation, Gen0Collections = GC.CollectionCount(0) - gen0,
            MaximumVisuals = maximumVisuals, ContinuousScroll = renderReports,
            CacheExperimentPages = cacheExperiment, PhaseAllocations = phaseAllocations,
            Note = "Synthetic 5000/1500 catalogue. Layout latency includes dispatcher work, not presented GPU frames or physical input. Rendering callback intervals are not FPS. Allocation is process-wide. No real game, AI or Steam." };
        File.WriteAllText(Path.Combine(output, "performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples));
        window.ViewModel.MarkExported();
        if (!focused)
        {
            await FastStartupSmokeCheck.RunAsync(window, output);
            await StartupCacheSmokeCheck.RunAsync(window, output);
        }
    }

    private static async Task<object> MeasureContinuousScrollAsync(MainWindow window, System.Windows.Controls.ScrollViewer scroll)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        var watch = Stopwatch.StartNew();
        var intervals = new List<double>();
        double last = 0;
        TimeSpan lastRendering = TimeSpan.MinValue;
        var ticks = 0;
        EventHandler rendering = (_, args) =>
        {
            if (args is not System.Windows.Media.RenderingEventArgs frame || frame.RenderingTime == lastRendering) return;
            lastRendering = frame.RenderingTime;
            var now = watch.Elapsed.TotalMilliseconds;
            if (last > 0) intervals.Add(now - last);
            last = now;
        };
        var timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) => { ticks++; scroll.ScrollToVerticalOffset(Math.Min(scroll.ScrollableHeight, watch.Elapsed.TotalMilliseconds * 4)); };
        System.Windows.Media.CompositionTarget.Rendering += rendering;
        try { timer.Start(); await Task.Delay(1200); }
        finally { timer.Stop(); System.Windows.Media.CompositionTarget.Rendering -= rendering; }
        intervals.Sort();
        return new { InputTicks = ticks, RenderCallbacks = intervals.Count + 1,
            CallbackMedianMs = intervals.Count == 0 ? 0 : intervals[intervals.Count / 2],
            CallbackP95Ms = intervals.Count == 0 ? 0 : intervals[(int)(intervals.Count * .95)],
            GapsOver34Ms = intervals.Count(ms => ms > 34), FinalOffset = scroll.VerticalOffset };
    }

    private static int CountVisuals(DependencyObject node)
    {
        var count = 1;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            count += CountVisuals(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        return count;
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
