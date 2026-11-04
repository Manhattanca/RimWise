#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class BatchEditingSmokeCheck
{
    internal static async Task RunAsync(MainWindow parent, string output)
    {
        var window = new MainWindow(initializePaths: false) { Owner = parent };
        window.Show();
        window.LoadSample();
        var vm = window.ViewModel;
        var original = vm.CreateExportDocument();
        var count = 0;
        void Check(bool success, string reason)
        {
            if (!success) throw new InvalidOperationException(reason);
            count++;
        }
        string[] Order() => vm.Rows.Select(row => row.PackageId).ToArray();
        void Load(params string[] ids) => vm.Load(original.WithOrder(new LoadOrder(ids)), "批量 测试.xml");
        async Task Idle() => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        void KeyPress(DataGrid grid, Key key) => grid.RaiseEvent(
            new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        try
        {
            Load("a", "b", "c", "d", "e");
            vm.SelectOrderRows([vm.Rows[1], vm.Rows[2]], vm.Rows[2]);
            vm.MoveUpCommand.Execute(null);
            Check(Order().SequenceEqual(new[] { "b", "c", "a", "d", "e" }), "Contiguous batch move failed.");
            await Idle();
            Check(window.OrderGrid.SelectedItems.Count == 2 && vm.SelectedOrderRows.Count == 2, "WPF reset lost batch selection.");
            vm.UndoCommand.Execute(null);
            await Idle();
            Check(Order().SequenceEqual(new[] { "a", "b", "c", "d", "e" })
                && vm.SelectedOrderRows.Count == 2 && vm.SelectedRow?.PackageId == "c" && window.OrderGrid.SelectedItems.Count == 2
                && !vm.UndoCommand.CanExecute(null), "Undo did not restore selection or single-step history.");
            vm.RedoCommand.Execute(null);
            Check(Order()[0] == "b" && vm.SelectedOrderRows.Count == 2, "Cancel undo lost batch.");
            Load("a", "b", "c", "d", "e");
            vm.SelectOrderRows([vm.Rows[0], vm.Rows[2], vm.Rows[4]]);
            vm.MoveUpCommand.Execute(null);
            Check(Order().SequenceEqual(new[] { "a", "c", "b", "e", "d" }), "Separated blocks or upper boundary moved incorrectly.");
            Load("a", "b", "c", "d", "e");
            vm.SelectOrderRows([vm.Rows[0], vm.Rows[2], vm.Rows[4]]);
            vm.MoveDownCommand.Execute(null);
            Check(Order().SequenceEqual(new[] { "b", "a", "d", "c", "e" }), "Separated blocks or lower boundary moved incorrectly.");
            vm.SelectOrderRows(vm.Rows.ToArray());
            Check(!vm.MoveUpCommand.CanExecute(null) && !vm.MoveDownCommand.CanExecute(null), "Selecting all should disable moves.");
            Load("a", "same", "b", "same", "c");
            var second = vm.Rows[3];
            vm.SelectOrderRows([second]);
            Check(vm.MoveRowsTo([second], 0) && ReferenceEquals(vm.Rows[0], second)
                && Order().Count(id => id == "same") == 2, "Drag merged duplicate identifiers.");
            vm.UndoCommand.Execute(null);
            var revision = vm.ContentRevision;
            Check(!vm.MoveRowsTo([vm.Rows[3]], 4) && vm.ContentRevision == revision && !vm.UndoCommand.CanExecute(null), "No-op drag consumed history.");
            Check(!vm.MoveRowsTo([second], 0), "Stale row from undo accepted.");
            Load("a", "b", "c", "d", "e");
            vm.SelectOrderRows([vm.Rows[1], vm.Rows[3]]);
            Check(vm.MoveRowsTo(vm.SelectedOrderRows, 5) && Order().SequenceEqual(new[] { "a", "c", "e", "b", "d" }), "Drag group did not retain relative order.");
            vm.UndoCommand.Execute(null);
            vm.SearchText = "b";
            Check(!vm.MoveUpCommand.CanExecute(null) && !vm.MoveRowsTo(vm.SelectedOrderRows, 0)
                && vm.VisibleRows.Cast<LoadOrderRow>().Single().Index == 2, "Filtered move protection or global numbering failed.");
            await Idle();
            window.OrderGrid.UnselectAll();
            window.OrderGrid.SelectedItems.Add(vm.Rows[1]);
            window.OrderGrid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Delete)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(Order().SequenceEqual(new[] { "a", "c", "d", "e" }), "Filtered Delete removed hidden rows.");
            vm.UndoCommand.Execute(null);
            vm.SearchText = "";
            vm.SelectOrderRows([vm.Rows[1], vm.Rows[3]]);
            var drag = window.StartDragForCheck(false);
            Check(window.AcceptsDragForCheck(drag, true), "Valid internal drag rejected.");
            window.DropForCheck(drag, true);
            Check(Order().SequenceEqual(new[] { "a", "c", "e" }), "Cross-pane batch removal failed.");
            vm.UndoCommand.Execute(null);
            Check(Order().Length == 5 && vm.SelectedOrderRows.Count == 2, "Batch removal undo lost rows or selection.");
            drag = window.StartDragForCheck(false);
            vm.MoveDownCommand.Execute(null);
            Check(!window.AcceptsDragForCheck(drag, true), "Stale revision drag accepted.");
            var external = new DataObject(DataFormats.FileDrop, new[] { "C:\\external.txt" });
            Check(!window.AcceptsDragForCheck(external, false), "External files accepted as mods.");
            var other = new MainWindow(initializePaths: false);
            Check(!other.AcceptsDragForCheck(drag, true), "Another window accepted drag.");
            other.Close();
            var fixture = Path.Combine(output, "批量 本地 [样例] & mods");
            for (var i = 0; i < 4; i++)
            {
                var about = Path.Combine(fixture, "模组" + i, "About");
                Directory.CreateDirectory(about);
                File.WriteAllText(Path.Combine(about, "About.xml"), $"<ModMetaData><name>模组{i}</name><packageId>batch.{i}</packageId><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
            }
            Load();
            var scan = new ModScanner().Scan(new GamePaths { LocalModsDirectory = fixture, TargetVersionOverride = "1.7" });
            vm.ApplyScan(scan);
            await Idle();
            var available = vm.AvailableMods.Take(3).ToArray();
            window.AvailableGrid.SelectedItems.Add(available[0]);
            window.AvailableGrid.SelectedItems.Add(available[1]);
            window.AvailableGrid.SelectedItems.Add(available[2]);
            Check(vm.SelectedAvailableRows.Count == 3 && vm.AddToOrderCommand.CanExecute(null), "UI multi-selection not synchronized.");
            window.AvailableGrid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(vm.Rows.Count == 3 && vm.AvailableMods.Count == 1 && vm.SelectedOrderRows.Count == 3, "Batch Enter did not activate all rows.");
            await Idle();
            Check(window.OrderGrid.SelectedItems.Count == 3 && window.AvailableGrid.SelectedItems.Count == 0, "Activation selection shown in both panes.");
            var selectedIds = Order();
            window.OrderGrid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(vm.Rows.Count == 0 && vm.AvailableMods.Count == 4, "Active Enter did not remove the complete selected group.");
            vm.UndoCommand.Execute(null);
            await Idle();
            Check(Order().SequenceEqual(selectedIds) && vm.SelectedOrderRows.Count == 3 && window.OrderGrid.SelectedItems.Count == 3,
                "Enter removal was not one undoable group operation.");
            vm.ToggleRetained(vm.Rows[1]);
            vm.SelectOrderRows(vm.Rows.ToArray());
            await Idle();
            var protectedRevision = vm.ContentRevision;
            window.OrderGrid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(vm.ContentRevision == protectedRevision && vm.Rows.Count == 3, "Enter bypassed retained protection for a mixed selection.");
            vm.UndoCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            await Idle();
            Check(vm.Rows.Count == 0 && vm.SelectedAvailableRows.Count == 3 && window.AvailableGrid.SelectedItems.Count == 3, "Undo activation did not restore inactive multi-selection.");
            drag = window.StartDragForCheck(true);
            window.DropForCheck(drag, false);
            Check(vm.Rows.Count == 3 && vm.SelectedOrderRows.Count == 3, "Drag into empty active pane failed.");
            var unused = vm.AvailableMods.Single();
            Check(vm.AddAvailableRows([unused], 1) && vm.Rows[1].PackageId == unused.PackageId, "Cross-pane insertion index failed.");
            Check(!vm.AddAvailableRows([unused], 0), "Stale inactive row accepted.");
            Check(vm.Rows.Select(row => row.Index).SequenceEqual(Enumerable.Range(1, vm.Rows.Count)), "Batch numbering is not one-based.");
            vm.SelectOrderRows(vm.Rows.ToArray());
            vm.RemoveFromOrderCommand.Execute(null);
            Check(vm.Rows.Count == 0 && vm.AvailableMods.Count == 4 && vm.ErrorCount == 0 && vm.WarningCount == 0, "Batch remove partition or diagnostics stale.");
            vm.UndoCommand.Execute(null);
            Check(vm.Rows.Count == 4 && vm.SelectedOrderRows.Count == 4, "Batch remove is not one history step.");
            Check(Directory.GetFiles(fixture, "About.xml", SearchOption.AllDirectories).Length == 4, "Editing touched installed files.");
            Load();
            await Idle();
            var availablePair = vm.AvailableMods.Take(2).ToArray();
            vm.SelectAvailableRows(availablePair);
            await Idle();
            Check(window.ContextTargetsForCheck(availablePair[1], ModSource.Local).ToHashSet().SetEquals(
                availablePair.Select(row => row.Installation.Directory)), "Local delete did not capture the complete selection.");
            window.AddFromContextForCheck(availablePair[1]);
            Check(vm.Rows.Count == 2 && vm.Rows.Select(row => row.PackageId).ToHashSet().SetEquals(availablePair.Select(row => row.PackageId)),
                "Right-click add only added the clicked row.");
            vm.UndoCommand.Execute(null);
            Check(vm.Rows.Count == 0, "Right-click batch add was not one undo step.");
            var subscriptions = new ModCatalogue(Enumerable.Range(1, 3).Select(i => new ModInstallation(
                Path.Combine(fixture, "workshop" + i), "workshop" + i, ModSource.Workshop, (1000 + i).ToString(),
                new("Workshop " + i, "fixture", "workshop." + i, ["1.7"], "", ModRequirements.Empty, false), [])).ToArray());
            vm.ApplyScan(scan with { Catalogue = subscriptions });
            await Idle();
            var workshopPair = vm.AvailableMods.Take(2).ToArray();
            vm.SelectAvailableRows(workshopPair);
            await Idle();
            Check(window.ContextTargetsForCheck(workshopPair[1], ModSource.Workshop).Length == 2,
                "Unsubscribe did not capture the complete selected group.");
            Check(window.ContextTargetsForCheck(vm.AvailableMods[2], ModSource.Workshop).Length == 1,
                "Right-click outside the group did not narrow the unsubscribe targets.");
            Load("a", "b", "c", "d", "e");
            vm.SelectOrderRows([vm.Rows[1], vm.Rows[3]]);
            await Idle();
            window.RemoveFromContextForCheck(vm.Rows[3]);
            Check(Order().SequenceEqual(["a", "c", "e"]), "Right-click removal collapsed the selected group.");
            vm.UndoCommand.Execute(null);
            Check(Order().SequenceEqual(["a", "b", "c", "d", "e"]) && !vm.UndoCommand.CanExecute(null),
                "Right-click batch removal was not one undo step.");
            vm.SelectOrderRows([vm.Rows[1], vm.Rows[3]]);
            await Idle();
            window.RemoveFromContextForCheck(vm.Rows[2]);
            Check(Order().SequenceEqual(["a", "b", "d", "e"]), "Right-click outside selection did not target the new row alone.");
            Load(Enumerable.Range(1, 300).Select(i => "scroll." + i).ToArray());
            await Idle();
            var orderScroll = window.ScrollForCheck(false)!;
            window.OrderGrid.ScrollIntoView(vm.Rows[150]);
            await Idle();
            var offset = orderScroll.VerticalOffset;
            Check(offset > 0, "Scroll test did not establish a middle viewport.");
            vm.SelectOrderRows([vm.Rows[0], vm.Rows[150]], vm.Rows[0]);
            await Idle();
            Check(Math.Abs(orderScroll.VerticalOffset - offset) < 0.01, "Selection synchronization scrolled to the first selected mod.");
            window.SelectAllForCheck(false);
            await Idle();
            Check(window.OrderGrid.SelectedItems.Count == 300 && Math.Abs(orderScroll.VerticalOffset - offset) < 0.01,
                "Ctrl+A changed the active viewport.");
            KeyPress(window.OrderGrid, Key.Down);
            await Idle();
            Check(orderScroll.VerticalOffset > offset && window.OrderGrid.SelectedItems.Count == 300,
                "Down must scroll without collapsing the group.");
            KeyPress(window.OrderGrid, Key.Up);
            await Idle();
            Check(Math.Abs(orderScroll.VerticalOffset - offset) < 0.01 && vm.SelectedOrderRows.Count == 300,
                "Up must reverse scrolling without changing selection.");
            Load();
            var many = new ModCatalogue(Enumerable.Range(1, 300).Select(i => new ModInstallation(
                Path.Combine(fixture, "scroll" + i), "scroll" + i, ModSource.Local, "",
                new("Mod " + i, "fixture", "inactive." + i, ["1.7"], "", ModRequirements.Empty, false), [])).ToArray());
            vm.ApplyScan(scan with { Catalogue = many });
            await Idle();
            var availableScroll = window.ScrollForCheck(true)!;
            window.AvailableGrid.ScrollIntoView(vm.AvailableMods[150]);
            await Idle();
            offset = availableScroll.VerticalOffset;
            vm.SelectAvailableRows([vm.AvailableMods[0], vm.AvailableMods[150]], vm.AvailableMods[0]);
            await Idle();
            Check(offset > 0 && Math.Abs(availableScroll.VerticalOffset - offset) < 0.01,
                "Inactive selection synchronization changed viewport.");
            window.SelectAllForCheck(true);
            await Idle();
            Check(window.AvailableGrid.SelectedItems.Count == 300 && Math.Abs(availableScroll.VerticalOffset - offset) < 0.01,
                "Inactive Ctrl+A changed viewport.");
            KeyPress(window.AvailableGrid, Key.Down);
            await Idle();
            Check(availableScroll.VerticalOffset > offset && vm.SelectedAvailableRows.Count == 300,
                "Inactive Down changed the group or did not scroll.");
            var wheelOffset = availableScroll.VerticalOffset;
            availableScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                { RoutedEvent = Mouse.MouseWheelEvent });
            await Idle();
            Check(vm.SelectedAvailableRows.Count == 300 && availableScroll.VerticalOffset > wheelOffset,
                "Mouse wheel failed to scroll the selected group.");
            // Include the real failure shape: a directory with disabled metadata beside valid mods.
            var disabled = Path.Combine(fixture, "未识别 CE 补丁", "About");
            Directory.CreateDirectory(disabled);
            File.WriteAllText(Path.Combine(disabled, "About.xml.disabled"),
                "<ModMetaData><name>Disabled patch</name><packageId>disabled.patch</packageId></ModMetaData>");
            var broken = new ModScanner().Scan(new GamePaths
                { LocalModsDirectory = fixture, TargetVersionOverride = "1.7" }).Catalogue.Installations
                .Single(mod => mod.FolderName == "未识别 CE 补丁");
            vm.ApplyScan(scan with { Catalogue = new ModCatalogue(many.Installations.Append(broken).ToArray()) });
            await Idle();
            window.SelectAllForCheck(true);
            Check(vm.SelectedAvailableRows.Count == 301 && vm.AddToOrderCommand.CanExecute(null),
                "One unidentified mod disabled the complete batch.");
            KeyPress(window.AvailableGrid, Key.Right);
            Check(vm.Rows.Count == 300 && vm.AvailableMods.Count == 1 && vm.Status.Contains("未识别 CE 补丁"),
                "Right arrow must add valid mods and identify the skipped patch.");
            await Idle();
            KeyPress(window.OrderGrid, Key.Left);
            Check(vm.Rows.Count == 0 && vm.AvailableMods.Count == 301, "Left arrow did not remove the group.");
            await Idle();
            foreach (var action in new[] { "enter", "context", "drag" })
            {
                window.SelectAllForCheck(true);
                await Idle();
                var anchor = vm.AvailableMods.First();
                window.AvailableGrid.ScrollIntoView(anchor);
                await Idle();
                var row = (DataGridRow)window.AvailableGrid.ItemContainerGenerator.ContainerFromItem(anchor);
                row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseUpEvent });
                await Idle();
                Check(window.AvailableGrid.SelectedItems.Count == 301 && vm.SelectedAvailableRows.Count == 301,
                    "Click/release on a selected row collapsed the batch.");
                if (action == "enter") KeyPress(window.AvailableGrid, Key.Enter);
                else if (action == "context")
                {
                    window.AddFromContextForCheck(anchor);
                }
                else window.DropForCheck(window.StartDragForCheck(true), false);
                Check(vm.Rows.Count == 300 && vm.AvailableMods.Count == 1 && vm.Status.Contains("未识别 CE 补丁"),
                    $"Mixed batch {action} lost valid mods or skipped-item feedback.");
                vm.UndoCommand.Execute(null);
                await Idle();
                Check(vm.Rows.Count == 0 && vm.SelectedAvailableRows.Count == 301,
                    $"Mixed batch {action} was not restored in one undo.");
            }
            vm.SelectAvailableRows(vm.AvailableMods.Where(row => string.IsNullOrEmpty(row.Installation.PackageId)));
            await Idle();
            var invalidRevision = vm.ContentRevision;
            KeyPress(window.AvailableGrid, Key.Enter);
            Check(vm.Rows.Count == 0 && vm.ContentRevision == invalidRevision && vm.Status.Contains("未加入"),
                "Invalid-only selection changed history or failed silently.");
            Check(File.Exists(Path.Combine(disabled, "About.xml.disabled"))
                && !File.Exists(Path.Combine(disabled, "About.xml")), "Batch editing altered disabled metadata.");
            await Idle();
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "多选批量操作.png"));
            File.WriteAllText(Path.Combine(output, "batch-editing.json"), JsonSerializer.Serialize(new { Passed = true, Checks = count }));
        }
        finally
        {
            vm.MarkExported();
            window.Close();
        }
    }
}
#endif
