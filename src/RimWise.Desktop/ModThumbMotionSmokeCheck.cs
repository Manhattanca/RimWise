#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Formats;
using RimWise.Integration.Cache;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

/// <summary>Native ScrollBar drag routing with real recycled cells; optional read-only personal catalogue.</summary>
internal static class ModThumbMotionSmokeCheck
{
    private sealed record Input(GamePaths Paths, string ConfigPath, string Profile);
    private sealed record Sample(double Ms, double Offset, double Thumb, bool Running, int Rows);
    internal static void Attach(MainWindow window, string output, bool real)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            var checks = new List<string>(); var failures = new List<string>();
            try { await RunAsync(window, output, real, checks, failures); }
            catch (Exception ex) { failures.Add(ex.ToString()); }
            finally { InterfaceMotion.ReduceMotionForCheck = false; }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
            window.ViewModel.MarkExported();
            Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
        };
    }

    private static async Task RunAsync(MainWindow window, string output, bool real, List<string> checks, List<string> failures)
    {
        void Check(bool value, string label) { if (value) checks.Add(label); else failures.Add(label); }
        Task Idle() => window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle).Task;
        ModsConfigDocument config;
        ModScanResult scan;
        Input? input = null;
        string? configHash = null;
        var watch = Stopwatch.StartNew();
        if (real)
        {
            input = JsonSerializer.Deserialize<Input>(File.ReadAllText(Path.Combine(output, "input.json")))!;
            configHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input.ConfigPath)));
            config = await Task.Run(() => { using var stream = File.OpenRead(input.ConfigPath); return new ModsConfigXmlFormat().Read(stream); });
            scan = await Task.Run(() => new ModScanner().Scan(input.Paths, inspectContent: false));
            // Reuse the user's existing classification display values, not credentials
            // or task data. Actual installation membership and metadata come from this scan.
            var snapshot = new StartupCacheStore(input.Profile).TryLoad(input.Paths);
            if (snapshot is not null)
            {
                var cached = snapshot.Scan.Catalogue.Installations.ToDictionary(m => m.Directory, StringComparer.OrdinalIgnoreCase);
                scan = scan with { Catalogue = new(scan.Catalogue.Installations.Select(m => cached.TryGetValue(m.Directory, out var old)
                    ? m with { CodeKind = old.CodeKind, ContentKinds = old.ContentKinds, LastUpdatedUtc = old.LastUpdatedUtc } : m).ToArray()) };
            }
            Check(scan.Catalogue.Installations.Count(m => m.Metadata is not null) > 100, "Real scan has over 100 installations with valid MOD metadata");
            Check(!scan.Diagnostics.Any(d => d.Code is "ScanLimit" or "ScanRootUnreadable"), "Real configured roots were completely read without changing installations");
        }
        else
        {
            var mods = Enumerable.Range(0, 2000).Select(i => new ModInstallation(Path.Combine(output, "合成模组", i.ToString()), i.ToString(),
                ModSource.Local, "", new("拖动测试 " + i, "合成作者", "thumbmotion." + i, ["1.7"], "", ModRequirements.Empty, false), [])).ToArray();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<ModsConfigData><version>1.7</version><activeMods>"
                + string.Concat(mods.Take(1000).Select(m => "<li>" + m.PackageId + "</li>")) + "</activeMods></ModsConfigData>"));
            config = new ModsConfigXmlFormat().Read(stream);
            scan = new(new(GameVersion.Parse("1.7"), "1.7", ""), GameVersion.Parse("1.7"), new(mods), []);
        }
        var scanMs = watch.Elapsed.TotalMilliseconds;
        window.ViewModel.Load(config, Path.Combine(output, "ReadOnly-Preview.xml"));
        window.ViewModel.ApplyScan(scan);
        await Idle();
        var original = window.ViewModel.Rows.Select(r => r.PackageId).ToArray();
        var traces = new Dictionary<string, Sample[]>();
        void Start(Thumb thumb) => thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        void End(Thumb thumb, bool cancel = false) => thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, cancel) { RoutedEvent = Thumb.DragCompletedEvent });
        void Move(Thumb thumb, double fraction) => thumb.RaiseEvent(new DragDeltaEventArgs(0, fraction) { RoutedEvent = Thumb.DragDeltaEvent });
        foreach (var available in new[] { true, false })
        {
            var name = available ? "Available" : "Enabled";
            var grid = available ? window.AvailableGrid : window.OrderGrid;
            var scroll = window.ScrollForCheck(available)!;
            var bar = Descendants(scroll).OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical && ReferenceEquals(b.TemplatedParent, scroll));
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            var thumb = track.Thumb;
            var visibleThumbs = Descendants(grid).OfType<ModThumbnail>().ToArray();
            await Task.WhenAll(visibleThumbs.Select(t => t.ImageLoading));
            if (available) window.ViewModel.SelectAvailableRows(window.ViewModel.AvailableMods.Take(3).ToArray());
            else window.ViewModel.SelectOrderRows(window.ViewModel.Rows.Take(3).ToArray());
            await Idle();
            var selection = grid.SelectedItems.Cast<object>().ToHashSet();
            Check(grid.Items.Count > 100 && scroll.ScrollableHeight > 5 * scroll.ViewportHeight, name + " actual list spans more than 100 MODs and five viewports");
            async Task<Sample[]> Trace(Action action)
            {
                var timer = Stopwatch.StartNew(); var samples = new List<Sample>(); var last = TimeSpan.MinValue;
                void Frame(object? sender, EventArgs e)
                {
                    if (e is RenderingEventArgs frame && frame.RenderingTime == last) return;
                    if (e is RenderingEventArgs f) last = f.RenderingTime;
                    samples.Add(new(timer.Elapsed.TotalMilliseconds, scroll.VerticalOffset, bar.Value,
                        ModWheelMotion.IsRunningForCheck(grid), Descendants(grid).OfType<DataGridRow>().Count()));
                }
                CompositionTarget.Rendering += Frame;
                try { action(); await Task.Delay(300); await Idle(); }
                finally { CompositionTarget.Rendering -= Frame; }
                return samples.ToArray();
            }
            scroll.ScrollToTop(); await Idle();
            ModWheelMotion.SetEnabled(grid, false);
            traces[name + "NativeBottom"] = await Trace(() => { Start(thumb); Move(thumb, 100000); End(thumb); });
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, name + " native thumb route reaches the actual bottom");
            ModWheelMotion.SetEnabled(grid, true);
            scroll.ScrollToTop(); await Idle();
            traces[name + "SmoothBottom"] = await Trace(() =>
            {
                Start(thumb); Move(thumb, 100000);
                Check(ModWheelMotion.IsRunningForCheck(grid), name + " native DragDelta starts a short transition before native ScrollViewer can jump");
                Check(Math.Abs(bar.Value - bar.Maximum) < 1, name + " native thumb immediately follows pointer to bottom while content eases independently");
                End(thumb);
            });
            var bottom = traces[name + "SmoothBottom"];
            Check(bottom.Select(s => Math.Round(s.Offset, 1)).Distinct().Count() >= 3, name + " top-to-bottom native thumb drag retains intermediate visible positions");
            Check(bottom.Zip(bottom.Skip(1)).All(p => p.Second.Offset >= p.First.Offset - 1), name + " bottom arrival is monotonic without bounce");
            Check(bottom.Any(s => s.Running) && bottom.All(s => Math.Abs(s.Thumb - s.Offset) <= Math.Min(240, scroll.ViewportHeight * 0.5) + 1),
                name + " rendered drag tail stays within half a viewport");
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 && !ModWheelMotion.IsRunningForCheck(grid), name + " bottom release lands precisely and unsubscribes rendering");
            Check(Math.Abs(bar.Value - scroll.VerticalOffset) < 1, name + " native thumb and actual content reunite after release");
            await Task.WhenAll(Descendants(grid).OfType<ModThumbnail>().Select(t => t.ImageLoading));
            traces[name + "SmoothTopWarm"] = await Trace(() => { Start(thumb); Move(thumb, -100000); End(thumb); });
            Check(traces[name + "SmoothTopWarm"].Select(s => Math.Round(s.Offset, 1)).Distinct().Count() >= 3
                && scroll.VerticalOffset < 1 && !ModWheelMotion.IsRunningForCheck(grid), name + " quick return to top retains motion and settles without a stale destination");

            scroll.ScrollToVerticalOffset(1000); await Idle();
            Start(thumb); Move(thumb, 3); await Task.Delay(25); await Idle();
            var current = scroll.VerticalOffset;
            Move(thumb, -2); var reversalTarget = bar.Value; End(thumb); await Task.Delay(220); await Idle();
            Check(Math.Abs(scroll.VerticalOffset - reversalTarget) < 1 && scroll.VerticalOffset < current,
                name + " reversing a drag replaces forward motion immediately");

            Start(thumb);
            for (var i = 0; i < 30; i++) Move(thumb, i % 2 == 0 ? 400 : -400);
            var destination = bar.Value; End(thumb); await Task.Delay(220); await Idle();
            Check(Math.Abs(scroll.VerticalOffset - destination) < 1 && !ModWheelMotion.IsRunningForCheck(grid), name + " bursty extreme drag input coalesces to the last destination without an animation queue");
            Check(selection.SetEquals(grid.SelectedItems.Cast<object>()), name + " thumb scrolling preserves the actual selected group");
            Check(bottom.All(s => s.Rows < 80), name + " full-range drag never realizes the whole MOD list");

            Start(thumb); Move(thumb, 100000); await Task.Delay(240); await Idle();
            Check(!ModWheelMotion.IsRunningForCheck(grid) && Math.Abs(bar.Value - scroll.ScrollableHeight) < 1,
                name + " holding a stationary native thumb releases rendering after arriving");
            Move(thumb, -3); var continued = bar.Value; End(thumb); await Task.Delay(220); await Idle();
            Check(Math.Abs(scroll.VerticalOffset - continued) < 1 && continued < scroll.ScrollableHeight,
                name + " the same held drag can resume after its prior tail completed");
            Start(thumb); Move(thumb, -20); InterfaceMotion.ReduceMotionForCheck = true;
            Move(thumb, -3); End(thumb); await Idle();
            Check(!ModWheelMotion.IsRunningForCheck(grid) && scroll.VerticalOffset < continued,
                name + " switching to reduced motion mid-drag preserves the next native input");
            InterfaceMotion.ReduceMotionForCheck = false;
            scroll.ScrollToTop(); await Idle();
            var page = Descendants(track).OfType<RepeatButton>().First(b => b.Command == ScrollBar.PageDownCommand);
            ScrollBar.PageDownCommand.Execute(page.CommandParameter, scroll); await Idle();
            Check(!ModWheelMotion.IsRunningForCheck(grid) && scroll.VerticalOffset > 0,
                name + " native scrollbar page command remains immediate without thumb interpolation");
            Check(Math.Abs(bar.Value - scroll.VerticalOffset) < 1,
                name + " template offset binding continues to follow native navigation after repeated thumb gestures");

            scroll.ScrollToVerticalOffset(1000); await Idle();
            Start(thumb); Move(thumb, 20); End(thumb, cancel: true);
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " cancelled native drag promptly releases motion");
            Start(thumb); Move(thumb, 20);
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " keyboard cancels thumb easing immediately");
            End(thumb);
            Start(thumb); Move(thumb, 20); scroll.ScrollToVerticalOffset(1500); await Idle(); await Task.Delay(180); await Idle(); End(thumb);
            Check(!ModWheelMotion.IsRunningForCheck(grid) && Math.Abs(scroll.VerticalOffset - 1500) < 1, name + " external navigation cancels obsolete thumb destination");
            Start(thumb); Move(thumb, 20); grid.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " hidden list removes thumb rendering subscription");
            End(thumb); grid.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible); await Idle();
            Start(thumb); Move(thumb, 20); grid.SetCurrentValue(DataGrid.RowHeightProperty, 42d); await Idle();
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " row-height change cancels thumb interpolation with obsolete geometry");
            End(thumb); grid.ClearValue(DataGrid.RowHeightProperty); await Idle();

            scroll.ScrollToTop(); await Idle(); InterfaceMotion.ReduceMotionForCheck = true;
            Start(thumb); Move(thumb, 100000); End(thumb); await Idle();
            Check(!ModWheelMotion.IsRunningForCheck(grid) && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                name + " reduced motion retains native thumb input and the real endpoint");
            InterfaceMotion.ReduceMotionForCheck = false;
            Start(thumb); Move(thumb, -100000); ModWheelMotion.SetEnabled(grid, false); End(thumb);
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " disabling behavior cleans pending thumb and command handlers");
            ModWheelMotion.SetEnabled(grid, true);
            Check(VirtualizingPanel.GetVirtualizationMode(grid) == VirtualizationMode.Recycling
                && VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Pixel, name + " recycling and native pixel virtualization remain enabled");
        }
        Check(original.SequenceEqual(window.ViewModel.Rows.Select(r => r.PackageId)), "All thumb gestures preserve actual MOD load order");
        if (input is not null)
            Check(configHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input.ConfigPath))), "Actual user's game configuration stayed byte-identical throughout read-only acceptance");
        File.WriteAllText(Path.Combine(output, "thumb-traces.json"), JsonSerializer.Serialize(new
        {
            RealCatalogue = real, Installed = scan.Catalogue.Installations.Count, ValidMetadata = scan.Catalogue.Installations.Count(m => m.Metadata is not null),
            Enabled = window.ViewModel.Rows.Count, Available = window.ViewModel.AvailableMods.Count, ScanMs = scanMs, Traces = traces,
            Note = "Native WPF Thumb/ScrollBar drag event and command routes, real recycled cells and real thumbnails when requested. Rendering offset observations are not GPU FPS or physical input latency. No game, subscription, AI call or config save."
        }, new JsonSerializerOptions { WriteIndented = true }));
        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "thumb-motion.png"));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
#endif
