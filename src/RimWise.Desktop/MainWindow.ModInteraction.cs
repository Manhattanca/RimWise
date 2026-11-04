using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private void Retain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: LoadOrderRow orderRow }) ViewModel.ToggleRetained(orderRow);
        else if (sender is CheckBox { DataContext: InstalledModRow availableRow }) ViewModel.ToggleRetained(availableRow);
        e.Handled = true;
    }

    private bool synchronizingSelection;
    private bool selectionSyncPending;
    private Point dragStart;
    private DataGrid? dragSource;
    private object? pressedItem;
    private bool heldSelection;
    private ModDrag? activeDrag;
    private Border? dropIndicator;
    private ModDragPreview? dragPreview;
    private const string ModDragFormat = "RimWise.Internal.ModRows";
    private sealed record ModDrag(MainWindow Owner, long Revision, LoadOrderRow[] Order, InstalledModRow[] Available);

    private void InitializeModInteraction()
    {
        ViewModel.PropertyChanged += SelectionModelChanged;
        OrderGrid.PreviewMouseRightButtonDown += ModGrid_RightMouseDown;
        AvailableGrid.PreviewMouseRightButtonDown += ModGrid_RightMouseDown;
        Closed += (_, _) => { dragPreview?.Dispose(); dragPreview = null; ClearDropIndicator(); };
    }

    private void ModGrid_RightMouseDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (HitRow(grid, e.OriginalSource) is not { } row) return;
        // Keep the group when opening its menu; selecting a new row still targets that row alone.
        SelectContextRow(grid, row.Item);
        e.Handled = true;
    }

    private static void SelectContextRow(DataGrid grid, object item)
    {
        if (!grid.SelectedItems.Contains(item)) grid.SelectedItem = item;
    }

    private void SelectionModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.SelectedOrderRows) or nameof(MainViewModel.SelectedAvailableRows))) return;
        if (synchronizingSelection || selectionSyncPending) return;
        selectionSyncPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            selectionSyncPending = false;
            synchronizingSelection = true;
            try
            {
                SyncSelection(OrderGrid, ViewModel.SelectedOrderRows.Cast<object>().ToArray());
                SyncSelection(AvailableGrid, ViewModel.SelectedAvailableRows.Cast<object>().ToArray());
            }
            finally { synchronizingSelection = false; }
        }));
    }

    private static void SyncSelection(DataGrid grid, object[] selection)
    {
        foreach (var item in grid.SelectedItems.Cast<object>().Except(selection).ToArray()) grid.SelectedItems.Remove(item);
        foreach (var item in selection.Where(grid.Items.Contains))
            if (!grid.SelectedItems.Contains(item)) grid.SelectedItems.Add(item);
        // Selection changes must not navigate the viewport. Explicit diagnostic navigation owns scrolling.
    }

    private void ModSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizingSelection) return;
        var grid = (DataGrid)sender;
        synchronizingSelection = true;
        try
        {
            if (grid == OrderGrid)
                ViewModel.SelectOrderRows(grid.SelectedItems.Cast<LoadOrderRow>().ToArray(), grid.SelectedItem as LoadOrderRow);
            else ViewModel.SelectAvailableRows(grid.SelectedItems.Cast<InstalledModRow>().ToArray(), grid.SelectedItem as InstalledModRow);
        }
        finally { synchronizingSelection = false; }
        SelectionModelChanged(this, new PropertyChangedEventArgs(nameof(MainViewModel.SelectedOrderRows)));
    }

    private void ModGrid_KeyDown(object sender, KeyEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (e.IsRepeat && e.Key is Key.Enter or Key.Delete or Key.Left or Key.Right)
        {
            e.Handled = true;
            return;
        }
        e.Handled = HandleModShortcut(grid, e.Key, Keyboard.Modifiers);
    }

    private bool HandleModShortcut(DataGrid grid, Key key, ModifierKeys modifiers)
    {
        if (grid != AvailableGrid && grid != OrderGrid) return false;
        if (key == Key.A && modifiers == ModifierKeys.Control)
        {
            grid.SelectAll();
            return true;
        }
        if (modifiers == ModifierKeys.None && key is Key.Up or Key.Down)
        {
            if (FindVisual<ScrollViewer>(grid) is { } scroll)
            {
                var lines = SystemParameters.WheelScrollLines;
                if (lines < 0) { if (key == Key.Up) scroll.PageUp(); else scroll.PageDown(); }
                else scroll.ScrollToVerticalOffset(scroll.VerticalOffset
                    + (key == Key.Up ? -1 : 1) * lines * (double.IsFinite(grid.RowHeight) ? grid.RowHeight : 38));
            }
            return true;
        }
        var command = grid == AvailableGrid && key is Key.Enter or Key.Right && modifiers == ModifierKeys.None ? ViewModel.AddToOrderCommand
            : grid == OrderGrid && key is Key.Enter or Key.Delete or Key.Left && modifiers == ModifierKeys.None ? ViewModel.RemoveFromOrderCommand
            : grid == OrderGrid && key == Key.Up && modifiers == ModifierKeys.Alt ? ViewModel.MoveUpCommand
            : grid == OrderGrid && key == Key.Down && modifiers == ModifierKeys.Alt ? ViewModel.MoveDownCommand : null;
        if (command is null) return modifiers == ModifierKeys.None && key is Key.Left or Key.Right;
        if (command.CanExecute(null)) command.Execute(null);
        return true;
    }

    private static DataGridRow? HitRow(DataGrid grid, object source) => source is DependencyObject element
        ? ItemsControl.ContainerFromElement(grid, element) as DataGridRow : null;

    private void ModGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        dragSource = null;
        pressedItem = null;
        heldSelection = false;
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
        var grid = (DataGrid)sender;
        if (HitRow(grid, e.OriginalSource) is not { } row) return;
        dragStart = e.GetPosition(this);
        dragSource = grid;
        pressedItem = row.Item;
        // WPF would otherwise collapse a multi-selection before a group drag can start.
        if (Keyboard.Modifiers == ModifierKeys.None && grid.SelectedItems.Contains(row.Item) && grid.SelectedItems.Count > 1)
        {
            heldSelection = true;
            var selection = grid.SelectedItems.Cast<object>().ToArray();
            synchronizingSelection = true;
            try
            {
                grid.Focus();
                SyncSelection(grid, selection);
            }
            finally { synchronizingSelection = false; }
            e.Handled = true;
        }
    }

    private void ModGrid_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Releasing a group drag/click must not reduce the group to its anchor row.
        if (heldSelection) e.Handled = true;
        dragSource = null;
        pressedItem = null;
        heldSelection = false;
    }

    private void ModGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { dragSource = null; return; }
        if (dragSource is not { } grid || pressedItem is null
            || (Keyboard.Modifiers & ~ModifierKeys.Control) != ModifierKeys.None) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var payload = new ModDrag(this, ViewModel.ContentRevision,
            grid == OrderGrid ? grid.SelectedItems.Cast<LoadOrderRow>().ToArray() : [],
            grid == AvailableGrid ? grid.SelectedItems.Cast<InstalledModRow>().ToArray() : []);
        if (payload.Order.Length + payload.Available.Length == 0) return;
        var sourceRow = grid.ItemContainerGenerator.ContainerFromItem(pressedItem) as DataGridRow;
        var height = sourceRow?.ActualHeight ?? grid.RowHeight;
        if (!double.IsFinite(height) || height <= 0) height = 38;
        var width = Math.Clamp((sourceRow?.ActualWidth ?? grid.ActualWidth) - 12, 180, 420);
        var origin = (sourceRow ?? (FrameworkElement)grid).TranslatePoint(new Point(), ModInteractionOverlay);
        var pointer = TranslatePoint(point, ModInteractionOverlay);
        var start = TranslatePoint(dragStart, ModInteractionOverlay);
        var grip = new Point(Math.Clamp(start.X - origin.X, 12, width - 12),
            Math.Clamp(start.Y - origin.Y, 0, height));
        dragPreview?.Dispose();
        var previewRows = new[] { pressedItem }.Concat(payload.Order.Cast<object>().Concat(payload.Available)
            .Where(item => !ReferenceEquals(item, pressedItem))).ToArray();
        dragPreview = new ModDragPreview(ModInteractionOverlay, previewRows, width, pointer, grip, height: height);
        dragSource = null;
        pressedItem = null;
        heldSelection = false;
        activeDrag = payload;
        try { DragDrop.DoDragDrop(grid, new DataObject(ModDragFormat, payload), DragDropEffects.Move); }
        finally { activeDrag = null; dragPreview?.Dispose(); dragPreview = null; ClearDropIndicator(); }
        e.Handled = true;
    }

    private ModDrag? ReadDrag(IDataObject data)
    {
        if (activeDrag is not { } drag || drag.Owner != this || drag.Revision != ViewModel.ContentRevision
            || !data.GetDataPresent(ModDragFormat, false) || !ReferenceEquals(data.GetData(ModDragFormat, false), drag)) return null;
        if (drag.Order.Any(row => !ViewModel.Rows.Contains(row))
            || drag.Available.Any(row => !ViewModel.AvailableMods.Contains(row))) return null;
        return drag;
    }

    private bool CanDrop(DataGrid target, ModDrag? drag) => drag is not null && ViewModel.HasDocument
        && (target == AvailableGrid ? drag.Order.Length > 0
            : string.IsNullOrWhiteSpace(ViewModel.SearchText) && (drag.Order.Length > 0 || drag.Available.Length > 0));

    private (int Index, DataGridRow? Row, bool After) DropPosition(DataGrid grid, DragEventArgs e)
    {
        var row = HitRow(grid, e.OriginalSource);
        if (row?.Item is not LoadOrderRow item) return (ViewModel.Rows.Count, null, true);
        var after = e.GetPosition(row).Y >= row.ActualHeight / 2;
        return (ViewModel.Rows.IndexOf(item) + (after ? 1 : 0), row, after);
    }

    private void ModGrid_DragOver(object sender, DragEventArgs e)
    {
        var grid = (DataGrid)sender;
        var allowed = CanDrop(grid, ReadDrag(e.Data));
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (!allowed) { ClearDropIndicator(); return; }
        var point = e.GetPosition(grid);
        var scroll = FindVisual<ScrollViewer>(grid);
        if (point.Y < 40) scroll?.LineUp();
        else if (point.Y > grid.ActualHeight - 30) scroll?.LineDown();
        if (grid == OrderGrid)
        {
            var position = DropPosition(grid, e);
            var anchor = (FrameworkElement?)position.Row ?? grid;
            var origin = anchor.TranslatePoint(new Point(), ModInteractionOverlay);
            if (dropIndicator is null)
            {
                dropIndicator = new Border { Height = 3, Background = (Brush)FindResource("AccentBrush"), IsHitTestVisible = false };
                ModInteractionOverlay.Children.Add(dropIndicator);
            }
            dropIndicator.Width = anchor.ActualWidth;
            Canvas.SetLeft(dropIndicator, origin.X);
            Canvas.SetTop(dropIndicator, origin.Y + (position.After ? anchor.ActualHeight - 3 : 0));
        }
        else ClearDropIndicator();
    }

    private void ModGrid_DragLeave(object sender, DragEventArgs e) => ClearDropIndicator();
    private void ClearDropIndicator()
    {
        if (dropIndicator is not null) ModInteractionOverlay.Children.Remove(dropIndicator);
        dropIndicator = null;
    }

    private void ModGrid_Drop(object sender, DragEventArgs e)
    {
        var grid = (DataGrid)sender;
        var drag = ReadDrag(e.Data);
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ClearDropIndicator();
        if (!CanDrop(grid, drag) || drag is null) return;
        ApplyModDrop(grid, drag, grid == OrderGrid ? DropPosition(grid, e).Index : 0);
        e.Effects = DragDropEffects.Move;
    }

    private void ApplyModDrop(DataGrid grid, ModDrag drag, int position)
    {
        if (grid == AvailableGrid)
        {
            ViewModel.SelectOrderRows(drag.Order);
            if (ViewModel.RemoveFromOrderCommand.CanExecute(null)) ViewModel.RemoveFromOrderCommand.Execute(null);
        }
        else
        {
            if (drag.Order.Length > 0) ViewModel.MoveRowsTo(drag.Order, position);
            else ViewModel.AddAvailableRows(drag.Available, position);
        }
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

#if DEBUG
    private static void RightClickForCheck(DataGrid grid, object item)
    {
        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
            throw new InvalidOperationException("The context-menu fixture row is not visible.");
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        row.RaiseEvent(click);
        if (!click.Handled) throw new InvalidOperationException("The right-click selection handler did not run.");
    }

    internal string[] ContextTargetsForCheck(object row, ModSource source)
    {
        var grid = row is LoadOrderRow ? OrderGrid : AvailableGrid;
        RightClickForCheck(grid, row);
        contextOrderRow = row as LoadOrderRow;
        contextAvailableRow = row as InstalledModRow;
        return ContextInstallations(source).Select(mod => mod.Directory).ToArray();
    }

    internal void AddFromContextForCheck(InstalledModRow row)
    {
        RightClickForCheck(AvailableGrid, row);
        contextAvailableRow = row; contextOrderRow = null;
        ContextAdd_Click(this, new RoutedEventArgs());
    }

    internal void RemoveFromContextForCheck(LoadOrderRow row)
    {
        RightClickForCheck(OrderGrid, row);
        contextOrderRow = row;
        ContextRemove_Click(this, new RoutedEventArgs());
    }

    internal void SelectAllForCheck(bool available) =>
        HandleModShortcut(available ? AvailableGrid : OrderGrid, Key.A, ModifierKeys.Control);

    internal ScrollViewer? ScrollForCheck(bool available) => FindVisual<ScrollViewer>(available ? AvailableGrid : OrderGrid);

    internal IDataObject StartDragForCheck(bool available)
    {
        activeDrag = new ModDrag(this, ViewModel.ContentRevision,
            available ? [] : ViewModel.SelectedOrderRows.ToArray(),
            available ? ViewModel.SelectedAvailableRows.ToArray() : []);
        return new DataObject(ModDragFormat, activeDrag);
    }

    internal bool AcceptsDragForCheck(IDataObject data, bool toAvailable) =>
        CanDrop(toAvailable ? AvailableGrid : OrderGrid, ReadDrag(data));

    internal void DropForCheck(IDataObject data, bool toAvailable)
    {
        var grid = toAvailable ? AvailableGrid : OrderGrid;
        if (ReadDrag(data) is { } drag && CanDrop(grid, drag)) ApplyModDrop(grid, drag, ViewModel.Rows.Count);
        activeDrag = null;
    }
#endif
}
