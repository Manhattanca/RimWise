#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Formats;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class ModWheelMotionSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            var checks = new List<string>(); var failures = new List<string>();
            try { await RunAsync(window, output, checks, failures); }
            catch (Exception ex) { failures.Add(ex.ToString()); }
            finally { InterfaceMotion.ReduceMotionForCheck = false; }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
            window.ViewModel.MarkExported();
            Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
        };
    }

    private sealed record Sample(double Milliseconds, double Offset, bool Running);
    private static async Task RunAsync(MainWindow window, string output, List<string> checks, List<string> failures)
    {
        void Check(bool value, string label) { if (value) checks.Add(label); else failures.Add(label); }
        Task Idle() => window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle).Task;
        var mods = Enumerable.Range(0, 1000).Select(i => new ModInstallation(Path.Combine(output, "合成模组", i.ToString()), i.ToString(),
            ModSource.Local, "", new("滚动测试 " + i, "合成作者", "wheel." + i, ["1.7"], "", ModRequirements.Empty, false), [])).ToArray();
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes("<ModsConfigData><version>1.7</version><activeMods>"
            + string.Concat(mods.Take(500).Select(mod => "<li>" + mod.PackageId + "</li>")) + "</activeMods></ModsConfigData>"));
        window.ViewModel.Load(new ModsConfigXmlFormat().Read(xml), Path.Combine(output, "ModsConfig.xml"));
        window.ViewModel.ApplyScan(new(new(GameVersion.Parse("1.7"), "1.7", ""), GameVersion.Parse("1.7"), new(mods), []));
        await Idle();
        Check(InterfaceMotion.Enabled && SystemParameters.WheelScrollLines != 0, "Animation-capable test environment without changing OS preferences");
        var original = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        var traces = new Dictionary<string, Sample[]>();

        bool Wheel(DataGrid grid, ScrollViewer scroll, int delta)
        {
            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            grid.RaiseEvent(preview);
            var captured = preview.Handled;
            if (!captured) scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = Mouse.MouseWheelEvent });
            return captured;
        }
        async Task<Sample[]> Trace(DataGrid grid, ScrollViewer scroll, Action action)
        {
            var watch = Stopwatch.StartNew(); var samples = new List<Sample>();
            var last = TimeSpan.MinValue;
            void Frame(object? sender, EventArgs e)
            {
                if (e is RenderingEventArgs frame && frame.RenderingTime == last) return;
                if (e is RenderingEventArgs f) last = f.RenderingTime;
                samples.Add(new(watch.Elapsed.TotalMilliseconds, scroll.VerticalOffset, ModWheelMotion.IsRunningForCheck(grid)));
            }
            CompositionTarget.Rendering += Frame;
            try { action(); await Task.Delay(350); await Idle(); }
            finally { CompositionTarget.Rendering -= Frame; }
            return samples.ToArray();
        }

        foreach (var available in new[] { true, false })
        {
            var grid = available ? window.AvailableGrid : window.OrderGrid;
            var name = available ? "Available" : "Enabled";
            var scroll = window.ScrollForCheck(available)!;
            // The product deliberately makes the selected group exclusive to one
            // list. Compare a real group before/after scrolling, not two groups
            // that cannot coexist or selection changes from subsequent clicks.
            if (available) window.ViewModel.SelectAvailableRows(window.ViewModel.AvailableMods.Take(3).ToArray());
            else window.ViewModel.SelectOrderRows(window.ViewModel.Rows.Take(3).ToArray());
            await Idle();
            var selection = grid.SelectedItems.Cast<object>().ToHashSet();
            Check(ModWheelMotion.GetEnabled(grid) && VirtualizingPanel.GetIsVirtualizing(grid)
                && VirtualizingPanel.GetVirtualizationMode(grid) == VirtualizationMode.Recycling
                && VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Pixel && scroll.CanContentScroll,
                name + " retains recycling and pixel virtualization");
            var distance = SystemParameters.WheelScrollLines < 0 ? scroll.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
            scroll.ScrollToVerticalOffset(1000); await Idle();
            var before = scroll.VerticalOffset;
            ModWheelMotion.SetEnabled(grid, false);
            traces[name + "Native"] = await Trace(grid, scroll, () => Wheel(grid, scroll, -120));
            Check(!ModWheelMotion.IsRunningForCheck(grid) && scroll.VerticalOffset > before, name + " native wheel baseline scrolls without attached animation");
            Check(Math.Abs(scroll.VerticalOffset - before - distance) < 1, name + " interpolation distance matches the measured native pixel wheel baseline");
            ModWheelMotion.SetEnabled(grid, true);
            scroll.ScrollToVerticalOffset(1000); await Idle(); before = scroll.VerticalOffset;
            var target = Math.Min(scroll.ScrollableHeight, before + distance);
            traces[name + "Smooth"] = await Trace(grid, scroll, () =>
            {
                Check(Wheel(grid, scroll, -120) && ModWheelMotion.IsRunningForCheck(grid)
                    && Math.Abs(scroll.VerticalOffset - before) < 0.5, name + " wheel starts a short transition rather than jumping to its destination");
            });
            var smooth = traces[name + "Smooth"];
            Check(smooth.Select(s => Math.Round(s.Offset, 1)).Distinct().Count(o => o > before + 0.5 && o < target - 0.5) >= 2,
                name + " real scroll offsets include multiple intermediate positions");
            Check(smooth.Zip(smooth.Skip(1)).All(pair => pair.Second.Offset >= pair.First.Offset - 0.5)
                && smooth.All(s => s.Offset <= target + 0.5), name + " moves monotonically without overshoot");
            Check(Math.Abs(scroll.VerticalOffset - target) < 1 && !ModWheelMotion.IsRunningForCheck(grid), name + " settles at the native wheel distance and releases its Rendering handler");

            scroll.ScrollToVerticalOffset(1000); await Idle();
            Wheel(grid, scroll, -120); await Task.Delay(40); await Idle();
            var reversal = scroll.VerticalOffset;
            Check(ModWheelMotion.IsRunningForCheck(grid) && reversal > 1000, name + " reversal fixture interrupts an in-progress wheel gesture");
            Wheel(grid, scroll, 120); await Task.Delay(240); await Idle();
            Check(Math.Abs(scroll.VerticalOffset - Math.Max(0, reversal - distance)) < 1 && !ModWheelMotion.IsRunningForCheck(grid), name + " reverse wheel replaces the old destination instead of finishing forward motion first");

            scroll.ScrollToVerticalOffset(500); await Idle();
            var burstTarget = Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + distance * 80);
            for (var i = 0; i < 40; i++) { Wheel(grid, scroll, -240); await Task.Delay(5); }
            await Task.Delay(280); await Idle();
            Check(Math.Abs(scroll.VerticalOffset - burstTarget) < 1 && !ModWheelMotion.IsRunningForCheck(grid), name + " rapid wheel burst spanning over 100 rows accumulates distance without a long animation queue");
            Check(Descendants(grid).OfType<DataGridRow>().Count() < 80, name + " wheel motion does not realize the entire MOD list");
            Check(selection.Count == 3 && selection.SetEquals(grid.SelectedItems.Cast<object>()), name + " selected group survives single, reverse and rapid wheel scrolling");

            Wheel(grid, scroll, -120);
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " pointer input interrupts easing without waiting for it");
            Wheel(grid, scroll, -120);
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " keyboard input interrupts easing without becoming delayed");
            Wheel(grid, scroll, -120); await Task.Delay(30);
            scroll.ScrollToVerticalOffset(6000); await Idle(); await Task.Delay(220); await Idle();
            Check(Math.Abs(scroll.VerticalOffset - 6000) < 1 && !ModWheelMotion.IsRunningForCheck(grid), name + " external diagnostic/native navigation is not pulled back to a stale wheel destination");
            Wheel(grid, scroll, -120); await Task.Delay(30);
            grid.ScrollIntoView(grid.Items[350]); await Idle();
            var navigated = scroll.VerticalOffset;
            await Task.Delay(220); await Idle();
            Check(navigated > 6000 && Math.Abs(scroll.VerticalOffset - navigated) < 1 && !ModWheelMotion.IsRunningForCheck(grid),
                name + " real ScrollIntoView wins over pending wheel interpolation");

            scroll.ScrollToVerticalOffset(3000); await Idle();
            Wheel(grid, scroll, -120); await Task.Delay(30); await Idle();
            var fineBefore = scroll.VerticalOffset;
            Wheel(grid, scroll, -30); await Idle();
            Check(!ModWheelMotion.IsRunningForCheck(grid) && Math.Abs(scroll.VerticalOffset - fineBefore - distance) < 1,
                $"{name} native high-resolution fallback is not undone by a cancelled animation request: before={fineBefore}, after={scroll.VerticalOffset}, step={distance}");
            Wheel(grid, scroll, -120); await Task.Delay(30); await Idle();
            var reducedBefore = scroll.VerticalOffset;
            InterfaceMotion.ReduceMotionForCheck = true;
            Wheel(grid, scroll, -120); await Idle();
            Check(!ModWheelMotion.IsRunningForCheck(grid) && Math.Abs(scroll.VerticalOffset - reducedBefore - distance) < 1,
                name + " switching to reduced motion mid-gesture preserves the next native wheel action");
            InterfaceMotion.ReduceMotionForCheck = false;

            Wheel(grid, scroll, -120);
            grid.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " hiding a page stops rendering subscriptions");
            grid.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible); await Idle();
            Wheel(grid, scroll, -120);
            grid.SetCurrentValue(DataGrid.RowHeightProperty, 42d); await Idle();
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " row-height changes cancel obsolete interpolation geometry");
            grid.ClearValue(DataGrid.RowHeightProperty); await Idle();

            InterfaceMotion.ReduceMotionForCheck = true;
            before = scroll.VerticalOffset;
            var captured = Wheel(grid, scroll, -120); await Idle();
            Check(!captured && !ModWheelMotion.IsRunningForCheck(grid) && scroll.VerticalOffset > before, name + " reduced motion keeps native immediate wheel input");
            InterfaceMotion.ReduceMotionForCheck = false;
            Check(!Wheel(grid, scroll, -30) && !ModWheelMotion.IsRunningForCheck(grid), name + " high-resolution wheel input is not given a second easing layer");
            await Idle();
            scroll.ScrollToTop(); await Idle(); Wheel(grid, scroll, 120); await Task.Delay(200); await Idle();
            Check(scroll.VerticalOffset == 0 && !ModWheelMotion.IsRunningForCheck(grid), name + " top boundary does not bounce or leave a frame handler running");
            scroll.ScrollToBottom(); await Idle(); Wheel(grid, scroll, -120); await Task.Delay(200); await Idle();
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 && !ModWheelMotion.IsRunningForCheck(grid), name + " bottom boundary does not overscroll");
            scroll.ScrollToVerticalOffset(1000); await Idle(); Wheel(grid, scroll, -120);
            ModWheelMotion.SetEnabled(grid, false);
            Check(!ModWheelMotion.IsRunningForCheck(grid), name + " disabling behavior releases its frame handler and event state");
            ModWheelMotion.SetEnabled(grid, true);
        }
        foreach (var available in new[] { true, false }) { window.ScrollForCheck(available)!.ScrollToVerticalOffset(1000); }
        await Idle();
        Wheel(window.AvailableGrid, window.ScrollForCheck(true)!, -120);
        Wheel(window.OrderGrid, window.ScrollForCheck(false)!, -120);
        Check(ModWheelMotion.IsRunningForCheck(window.AvailableGrid) && ModWheelMotion.IsRunningForCheck(window.OrderGrid), "Both lists own independent in-progress wheel state");
        await Task.Delay(250); await Idle();
        Check(!ModWheelMotion.IsRunningForCheck(window.AvailableGrid) && !ModWheelMotion.IsRunningForCheck(window.OrderGrid), "Both lists release frame subscriptions after simultaneous scrolling");
        Check(original.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)), "Scrolling, interruption and reverse input never change actual load order");
        File.WriteAllText(Path.Combine(output, "wheel-traces.json"), JsonSerializer.Serialize(new
        {
            Traces = traces, WheelScrollLines = SystemParameters.WheelScrollLines,
            Note = "Synthetic catalogue; real WPF pixel offsets at Rendering callbacks, not GPU-presented FPS or real hardware wheel latency. Preview plus native fallback reproduces the input routing without injecting OS input."
        }, new JsonSerializerOptions { WriteIndented = true }));
        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "wheel-motion.png"));
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
