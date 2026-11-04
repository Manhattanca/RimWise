#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class ModListLayoutSmokeCheck
{
    internal static async Task RunAsync(MainWindow main, string output)
    {
        var order = main.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        var selected = main.OrderGrid.SelectedItems.Cast<object>().ToArray();
        Check(System.Text.Json.JsonSerializer.Deserialize<ListDisplaySettings>("{\"AvailableSource\":1}") is { RowHeight: 38, AvailableSource: 1 }, "Old settings lost their row height default.");
        main.RowHeightPopup.IsOpen = true;
        await Layout(main);
        Check(main.RowHeightSlider.FocusVisualStyle is null, "Slider still uses the white Windows focus rectangle.");
        Check(!main.RowHeightSlider.IsSnapToTickEnabled, "Row height slider still snaps in two-pixel jumps.");
        main.RowHeightInput.Text = "35";
        await Layout(main);
        Check(main.OrderGrid.RowHeight == 35 && main.RowHeightSlider.Value == 35, "Typed odd row height not applied.");
        main.RowHeightInput.Text = "100";
        CommitInput();
        Check(main.RowHeightInput.Text == "64" && main.OrderGrid.RowHeight == 64, "Out-of-range typed height not clamped.");
        main.RowHeightInput.Text = "invalid";
        CommitInput();
        Check(main.RowHeightInput.Text == "64", "Invalid typed height did not restore the current value.");
        main.RowHeightInput.Text = "";
        main.RowHeightInput.Text = "29";
        var sliderFrames = new List<double>();
        void RecordSlider(object sender, RoutedPropertyChangedEventArgs<double> e) => sliderFrames.Add(e.NewValue);
        main.RowHeightSlider.ValueChanged += RecordSlider;
        main.AnimateRowHeight(60);
        await SettleMotion(main);
        main.RowHeightSlider.ValueChanged -= RecordSlider;
        if (SystemParameters.ClientAreaAnimation)
            Check(sliderFrames.Any(value => value > 29.01 && value < 59.99),
                "Slider track click did not show eased intermediate movement: " + string.Join(", ", sliderFrames));
        main.AnimateRowHeight(40);
        await Task.Delay(40);
        main.RowHeightInput.Text = "31";
        await SettleMotion(main);
        Check(main.RowHeightSlider.Value == 31 && main.OrderGrid.RowHeight == 31 && main.RowHeightInput.Text == "31",
            $"A previous slider animation overwrote a later typed value: slider={main.RowHeightSlider.Value}, row={main.OrderGrid.RowHeight}, input={main.RowHeightInput.Text}.");
        main.AnimateRowHeight(29);
        await SettleMotion(main);
        Check(main.RowHeightSlider.Value == 29 && main.OrderGrid.RowHeight == 29, "Animated slider stopped before its target.");
        main.AnimateRowHeight(47);
        main.RowHeightPopup.IsOpen = false;
        await Layout(main);
        var persistedHeight = new ListDisplaySettingsStore(UserStorage.DirectoryPath).Load().RowHeight;
        Check(persistedHeight == 47, $"Closing the popup saved an intermediate animation value instead of the selected height: {persistedHeight}; slider={main.RowHeightSlider.Value}, input={main.RowHeightInput.Text}.");

        void CommitInput() => main.RowHeightInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(main.RowHeightInput), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

        foreach (var height in new[] { 28d, 64d, 38d })
        {
            main.SetModRowHeight(height);
            await Layout(main);
            foreach (var grid in new[] { main.AvailableGrid, main.OrderGrid })
            {
                Check(grid.RowHeight == height, "Main list height did not update.");
                Check(Descendants(grid).OfType<DataGridRow>().All(row => Math.Abs(row.ActualHeight - height) < 1), "Rendered row height differs.");
            }
            Check(new ListDisplaySettingsStore(UserStorage.DirectoryPath).Load().RowHeight == height, "Row height not persisted.");
        }
        Check(order.SequenceEqual(main.ViewModel.Rows.Select(row => row.PackageId)), "Row height edited order.");
        Check(selected.SequenceEqual(main.OrderGrid.SelectedItems.Cast<object>()), "Row height changed selection.");
        main.SetModRowHeight(48);
        var restored = new MainWindow(initializePaths: false);
        Check(restored.RowHeightSlider.Value == 48, "Row height not restored in new window.");
        restored.Close();

        // Thousands of items force the native proportional thumb below our minimum.
        var gridLarge = new DataGrid
        {
            Style = (Style)main.FindResource("ModListGrid"), AutoGenerateColumns = false,
            IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true,
            ItemsSource = Enumerable.Range(0, 5000).Select(index => new { Name = $"模组 {index + 1:0000}" }).ToArray()
        };
        gridLarge.Columns.Add(new DataGridTextColumn { Header = "大量模组滚动验证", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        VirtualizingPanel.SetVirtualizationMode(gridLarge, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(gridLarge, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(gridLarge, true);
        var preview = new Window { Title = "模组行高与滚动条验证", Width = 520, Height = 520, Content = gridLarge, Owner = main };
        preview.Show();
        try
        {
            await Layout(preview);
            var bar = Descendants(gridLarge).OfType<ScrollBar>().Single(item => item.Orientation == Orientation.Vertical && item.IsVisible);
            Check(Presentation.ModScrollInteraction.GetEnabled(bar), "Mod scrollbar style not applied.");
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            var scroll = Descendants(gridLarge).OfType<ScrollViewer>().First();
            bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            await SettleMotion(preview);
            var small = track.Thumb.ActualHeight;
            Check(small >= 39 && small < 65, $"Resting thumb is too small: {small}.");
            bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            if (SystemParameters.ClientAreaAnimation)
            {
                await Task.Delay(60);
                await Layout(preview);
                Check(Presentation.ModScrollInteraction.GetGripWidth(bar) > 6 && Presentation.ModScrollInteraction.GetGripWidth(bar) < 14,
                    "Hover expansion did not render an intermediate eased width.");
                Check(track.Thumb.CaptureMouse(), "Moving thumb could not capture mouse.");
                var capturedHeight = track.Thumb.ActualHeight;
                await SettleMotion(preview);
                Check(Math.Abs(track.Thumb.ActualHeight - capturedHeight) < 0.1, "Thumb geometry changed during drag capture.");
                track.Thumb.ReleaseMouseCapture();
                bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            }
            await SettleMotion(preview);
            Check(track.Thumb.ActualHeight >= 63 && track.Thumb.ActualHeight > small, "Hover did not lengthen thumb.");
            Check(Descendants(track.Thumb).OfType<Border>().Single().ActualWidth >= 14, "Hover did not widen thumb.");
            Check(Descendants(gridLarge).OfType<DataGridRow>().Count() < 100, "Row virtualization lost.");
            Check(track.Thumb.CaptureMouse(), "Test thumb could not capture mouse.");
            bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            await Layout(preview);
            Check(track.Thumb.ActualHeight >= 63, "Thumb shrank while dragging outside the hover area.");
            track.Thumb.ReleaseMouseCapture();
            bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            await SettleMotion(preview);
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                await Layout(preview);
                PrototypeSmokeCheck.SavePreview(preview, Path.Combine(output, $"{theme}-滚动滑块.png"));
            }
            // Use the real ScrollBar routed drag handlers and Track distance mapping.
            foreach (var direction in new[] { 1d, -1d })
            {
                track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, direction * track.ActualHeight) { RoutedEvent = Thumb.DragDeltaEvent });
                track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, direction * track.ActualHeight, false) { RoutedEvent = Thumb.DragCompletedEvent });
                await Layout(preview);
                Check(direction > 0 ? Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 : scroll.VerticalOffset < 1, "Thumb drag did not reach list end.");
            }
            ScrollBar.PageDownCommand.Execute(null, bar);
            await Layout(preview);
            Check(scroll.VerticalOffset > 0, "Scroll track page command failed.");
            main.SetModRowHeight(28);
            await Layout(preview);
            Check(gridLarge.RowHeight == 28, "Other open list did not follow row height.");
            preview.Height = 180;
            await Layout(preview);
            Check(track.Thumb.IsVisible && track.Thumb.ActualHeight < track.ActualHeight, "Thumb inaccessible in short viewport.");
        }
        finally { preview.Close(); main.SetModRowHeight(38); }
        main.RowHeightPopup.IsOpen = true;
        await Layout(main);
        Check(main.RowHeightSlider.IsVisible, "Row height popup did not open.");
        PrototypeSmokeCheck.SavePreview(main, Path.Combine(output, "行高入口.png"));
        PrototypeSmokeCheck.SavePreview((FrameworkElement)main.RowHeightPopup.Child, Path.Combine(output, "行高调节.png"));
        main.RowHeightPopup.IsOpen = false;
        File.WriteAllText(Path.Combine(output, "mod-list-layout.json"), "{\"Passed\":true,\"Items\":5000,\"RowHeights\":[28,29,35,38,48,64],\"ThumbMinimum\":40,\"HoverMinimum\":64,\"AnimatedHover\":true,\"TypedHeight\":true,\"StableDragGeometry\":true}");
    }

    private static async Task Layout(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static async Task SettleMotion(Window window) { await Task.Delay(240); await Layout(window); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
