using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private readonly ListDisplaySettingsStore listDisplayStore = new(UserStorage.DirectoryPath);
    private ListDisplaySettings listDisplay = new();
    private bool listDisplayInitialized;
    private bool synchronizingRowHeight;
    private double? rowHeightMotionTarget;
    private long rowHeightMotionRevision;

    private void InitializeListDisplay()
    {
        try { listDisplay = listDisplayStore.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { ViewModel.Status = "列表设置无法读取，已使用默认显示"; }
        DetailsColumn.Width = new(listDisplay.DetailsWidth, GridUnitType.Star);
        AvailableColumn.Width = new(listDisplay.AvailableWidth, GridUnitType.Star);
        OrderColumn.Width = new(listDisplay.OrderWidth, GridUnitType.Star);
        SetModRowHeight(listDisplay.RowHeight, save: false);
        ApplyBadgeDisplay();
        SourceFilterBox.SelectedIndex = listDisplay.AvailableSource;
        OrderSourceFilterBox.SelectedIndex = listDisplay.OrderSource;
        listDisplayInitialized = true;
    }

    private void ApplyBadgeDisplay()
    {
        ViewModel.SetModBadgeIcons(listDisplay.Badges == ModBadgeDisplay.Icons);
        BadgeIconsItem.IsChecked = ViewModel.ShowModBadgeIcons;
        BadgeTextItem.IsChecked = ViewModel.ShowModBadgeText;
    }

    private void BadgeIcons_Click(object sender, RoutedEventArgs e) => SetBadgeDisplay(ModBadgeDisplay.Icons);
    private void RowHeight_Click(object sender, RoutedEventArgs e) => RowHeightPopup.IsOpen = true;
    private void RowHeightReset_Click(object sender, RoutedEventArgs e) => AnimateRowHeight(38);
    private void RowHeightPopup_Closed(object? sender, EventArgs e)
    {
        if (!listDisplayInitialized) return;
        CommitRowHeightInput();
        SaveListDisplay();
    }
    private void RowHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!listDisplayInitialized || synchronizingRowHeight) return;
        ApplyModRowHeight(e.NewValue);
        UpdateRowHeightInput();
    }
    internal void SetModRowHeight(double height, bool save = true)
    {
        if (!double.IsFinite(height)) return;
        height = Math.Clamp(Math.Round(height), 28, 64);
        synchronizingRowHeight = true;
        try
        {
            rowHeightMotionRevision++;
            rowHeightMotionTarget = null;
            RowHeightSlider.BeginAnimation(Slider.ValueProperty, null);
            RowHeightSlider.Value = height;
            ApplyModRowHeight(height);
            UpdateRowHeightInput();
        }
        finally { synchronizingRowHeight = false; }
        if (save) SaveListDisplay();
    }

    private void ApplyModRowHeight(double height)
    {
        height = Math.Clamp(Math.Round(height), 28, 64);
        listDisplay = listDisplay with { RowHeight = height };
        // A continuous slider can emit several fractional values in one pixel.
        // Avoid invalidating every virtualized list until the displayed height changes.
        if (!Equals(Application.Current.Resources["ModRowHeight"], height))
            Application.Current.Resources["ModRowHeight"] = height;
        var thumbnail = Math.Min(30, height - 4);
        if (!Equals(Application.Current.Resources["ModThumbnailSize"], thumbnail))
            Application.Current.Resources["ModThumbnailSize"] = thumbnail;
    }

    private void UpdateRowHeightInput()
    {
        var text = listDisplay.RowHeight.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        if (RowHeightInput.Text == text) return;
        var previous = synchronizingRowHeight;
        synchronizingRowHeight = true;
        try { RowHeightInput.Text = text; }
        finally { synchronizingRowHeight = previous; }
    }

    private void RowHeightInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!listDisplayInitialized || synchronizingRowHeight) return;
        StopRowHeightMotion();
        if (int.TryParse(RowHeightInput.Text, out var height) && height is >= 28 and <= 64)
            SetModRowHeight(height, save: false);
    }

    private void CommitRowHeightInput()
    {
        var height = rowHeightMotionTarget ?? (int.TryParse(RowHeightInput.Text, out var value) ? value : listDisplay.RowHeight);
        SetModRowHeight(height, save: false);
    }

    private void RowHeightInput_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRowHeightInput();

    private void RowHeightInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter) CommitRowHeightInput(); else UpdateRowHeightInput();
            RowHeightSlider.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            var current = int.TryParse(RowHeightInput.Text, out var value) ? value : listDisplay.RowHeight;
            SetModRowHeight(current + (e.Key == Key.Up ? 1 : -1), save: false);
            e.Handled = true;
        }
    }

    private void StopRowHeightMotion()
    {
        rowHeightMotionRevision++;
        var current = RowHeightSlider.Value;
        rowHeightMotionTarget = null;
        synchronizingRowHeight = true;
        try
        {
            RowHeightSlider.BeginAnimation(Slider.ValueProperty, null);
            RowHeightSlider.Value = current;
        }
        finally { synchronizingRowHeight = false; }
    }

    private void RowHeightSlider_KeyDown(object sender, KeyEventArgs e) => StopRowHeightMotion();

    private void RowHeightSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        StopRowHeightMotion();
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != RowHeightSlider; element = VisualTreeHelper.GetParent(element))
            if (element is Thumb) return; // Native dragging follows the pointer without animation lag.
        if (RowHeightSlider.Template.FindName("PART_Track", RowHeightSlider) is not Track track) return;
        var target = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), 28, 64);
        if (!double.IsFinite(target)) return;
        RowHeightSlider.Focus();
        AnimateRowHeight(target);
        e.Handled = true;
    }

    internal void AnimateRowHeight(double target)
    {
        if (!double.IsFinite(target)) return;
        target = Math.Clamp(target, 28, 64);
        StopRowHeightMotion();
        var current = RowHeightSlider.Value;
        synchronizingRowHeight = true;
        try { RowHeightSlider.Value = target; }
        finally { synchronizingRowHeight = false; }
        if (SystemParameters.ClientAreaAnimation)
        {
            rowHeightMotionTarget = target;
            var revision = rowHeightMotionRevision;
            var motion = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            };
            motion.Completed += (_, _) =>
            {
                // WPF may already have queued a completion when a new input removes
                // its clock. Only the latest operation may commit a final value.
                if (rowHeightMotionRevision == revision && rowHeightMotionTarget == target)
                    SetModRowHeight(target, save: false);
            };
            RowHeightSlider.BeginAnimation(Slider.ValueProperty, motion, HandoffBehavior.SnapshotAndReplace);
        }
        else { ApplyModRowHeight(target); UpdateRowHeightInput(); }
    }
    private void BadgeText_Click(object sender, RoutedEventArgs e) => SetBadgeDisplay(ModBadgeDisplay.Text);
    internal void SetBadgeDisplay(ModBadgeDisplay display)
    {
        listDisplay = listDisplay with { Badges = display };
        ApplyBadgeDisplay();
        SaveListDisplay();
    }

    private void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => SavePaneWidths();
    private void PaneSplitter_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right) SavePaneWidths();
    }

    private void SavePaneWidths()
    {
        var total = DetailsColumn.ActualWidth + AvailableColumn.ActualWidth + OrderColumn.ActualWidth;
        if (total <= 0) return;
        listDisplay = listDisplay with { DetailsWidth = DetailsColumn.ActualWidth / total,
            AvailableWidth = AvailableColumn.ActualWidth / total, OrderWidth = OrderColumn.ActualWidth / total };
        SaveListDisplay();
    }

    private void SaveListDisplay()
    {
        try { listDisplayStore.Save(listDisplay); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { ViewModel.Status = "列表设置未保存，当前显示仍可使用"; }
    }
}
