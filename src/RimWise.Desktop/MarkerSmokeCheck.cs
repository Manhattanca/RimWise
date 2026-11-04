#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Controls;
using System.Globalization;
using RimWise.Formats;
using RimWise.Desktop.Views;
using RimWise.Integration.Safety;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;
using RimWise.Integration.AI;
using RimWise.Desktop.Services;

namespace RimWise.Desktop;

internal static class MarkerSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            try { await RunAsync(window, output); Application.Current.Shutdown(0); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                Application.Current.Shutdown(1);
            }
        };
    }

    internal static async Task RunAsync(MainWindow parent, string output)
    {
        var window = new MainWindow(initializePaths: false) { Owner = parent };
        window.Show();
        window.LoadSample();
        var vm = window.ViewModel;
        var fixture = Path.Combine(output, "近期模组 中文 [样例] & 库");
        try
        {
            var first = Path.Combine(fixture, "第一个", "About");
            var second = Path.Combine(fixture, "第二个", "About");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "About.xml"), "<ModMetaData><name>Alpha 第一个</name><packageId>marker.one</packageId><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
            File.WriteAllText(Path.Combine(second, "About.xml"), "<ModMetaData><name>Beta 第二个</name><packageId>marker.two</packageId><supportedVersions><li>1.7</li></supportedVersions></ModMetaData>");
            vm.Load(vm.CreateExportDocument().WithOrder(new LoadOrder([])), Path.Combine(fixture, "排序.xml"));
            vm.ApplyScan(new ModScanner().Scan(new GamePaths { LocalModsDirectory = fixture, TargetVersionOverride = "1.7" }));
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            Assert(vm.AvailableMods.Count == 2, "Marker fixture scan did not find both mods.");
            vm.SetRecentFilter(RecentModFilter.Added);
            Assert(!vm.VisibleAvailableMods.Cast<InstalledModRow>().Any(), "First-scan baseline incorrectly marked old mods as new.");
            vm.SetRecentFilter(RecentModFilter.All);
            vm.SelectAvailableRows(vm.AvailableMods.ToArray());
            vm.ApplyBatchMarkers(false, null, "批量测试", false, [], vm.SelectedAvailableRows);
            Assert(vm.AvailableMods.All(row => row.Tags.Contains("批量测试")), "Batch tag was not applied.");
            vm.UndoCommand.Execute(null);
            Assert(vm.AvailableMods.All(row => row.Tags.Count == 0), "Batch tag undo failed.");
            vm.RedoCommand.Execute(null);
            Assert(vm.AvailableMods.All(row => row.Tags.Contains("批量测试")), "Batch tag redo failed.");
            vm.ApplyBatchMarkers(true, true, null, false, [], vm.SelectedAvailableRows);
            Assert(vm.AvailableMods.All(row => row.IsRetained), "Batch retain was not applied.");
            Assert(!window.OrderGrid.Columns.Any(column => column.Header?.ToString() == "保留")
                && !window.AvailableGrid.Columns.Any(column => column.Header?.ToString() == "保留"), "Retention column became visible.");
            vm.ApplyColor(null, [], vm.AvailableMods);
            vm.ApplyColor(MarkerPalette.Colors[0].Hex, [], vm.AvailableMods);
            Assert(vm.AvailableMods.All(r => r.MarkerColor == MarkerPalette.Colors[0].Hex), "Batch color failed.");
            vm.UndoCommand.Execute(null);
            Assert(vm.AvailableMods.All(r => r.MarkerColor is null), "Color undo failed.");
            vm.RedoCommand.Execute(null);
            Assert(vm.AvailableMods.All(r => r.MarkerColor == MarkerPalette.Colors[0].Hex), "Color redo failed.");
            // A rule protects existing and future assignments without changing explicit retention.
            vm.ApplyBatchMarkers(true, false, null, false, [], vm.AvailableMods);
            InstalledModRow One() => vm.AvailableMods.Single(r => r.PackageId == "marker.one");
            InstalledModRow Two() => vm.AvailableMods.Single(r => r.PackageId == "marker.two");
            var red = MarkerPalette.Colors[0].Hex;
            vm.ApplyColor(null, [], [Two()]);
            vm.SetColorRetention(red, true);
            Assert(One().IsRetained && !Two().IsRetained && vm.RetainedCount == 1, "Existing color protection failed.");
            vm.ApplyColor(red, [], [Two()]);
            Assert(Two().IsRetained && vm.RetainedCount == 2, "Future color assignment was not protected.");
            vm.UndoCommand.Execute(null);
            Assert(!Two().IsRetained && One().IsRetained, "Undo color did not update retention.");
            vm.RedoCommand.Execute(null);
            Assert(Two().IsRetained, "Redo color did not update retention.");
            vm.ApplyColor(MarkerPalette.Colors[1].Hex, [], [Two()]);
            Assert(!Two().IsRetained, "Changing color did not release color protection.");
            vm.UndoCommand.Execute(null);
            vm.SetColorRetention(red, false);
            Assert(vm.AvailableMods.All(r => !r.IsRetained), "Rule cancellation failed.");
            vm.UndoCommand.Execute(null);
            Assert(vm.AvailableMods.All(r => r.IsRetained), "Rule undo failed.");
            vm.RedoCommand.Execute(null);
            Assert(vm.AvailableMods.All(r => !r.IsRetained), "Rule redo failed.");
            vm.ToggleRetained(One());
            vm.SetColorRetention(red, true);
            vm.SetColorRetention(red, false);
            Assert(One().IsRetained && !Two().IsRetained, "Cancelling a rule erased explicit retention.");
            vm.SetColorRetention(red, true);
            Assert(new RetainedColorStore(UserStorage.DirectoryPath).Load().Contains(red), "Rule was not persisted.");
            var reopened = new MainViewModel();
            Assert(reopened.ColorRetentionAvailable && reopened.IsRetained("marker.two"), "Rule was not restored on reopening.");
            vm.AddAvailableRows([vm.AvailableMods[0]], vm.Rows.Count);
            Assert(vm.Rows[0].MarkerColor == MarkerPalette.Colors[0].Hex, "Enabling lost color.");
            Assert(vm.AvailableMods.Count == 1 && vm.AvailableMods[0].PackageId != vm.Rows[0].PackageId,
                "An enabled installation still appears in the inactive list.");
            var codec = new OrderFileCodec();
            var exported = codec.Read(new MemoryStream(codec.Write(vm.CreateOrderFile(), OrderFileFormat.RimSortJson)));
            Assert(exported.Colors.Count == 1 && exported.Colors[vm.Rows[0].PackageId] == MarkerPalette.Colors[0].Hex, "Export color selection failed.");
            var request = AiSortingSession.CreateRequest(vm, AiSettings.Default, null);
            Assert(request.RetainedMods.Contains(vm.Rows[0].PackageId), "AI request omitted color retention.");
            var removed = AiOrderPlanner.Plan([new AiAction("remove", vm.Rows[0].PackageId, null, false, null, null)],
                AiPermissionLevel.Level2ModManagement, vm.Rows.Select(r => r.PackageId).ToArray(), vm.CurrentCatalogue!,
                vm.RetainedPackageIds, GameVersion.Parse("1.7"));
            Assert(removed.Rejected.Any(r => r.Contains("保留")), "AI could remove a color-protected mod.");
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var renderedAvailable = (DataGridRow)window.AvailableGrid.ItemContainerGenerator.ContainerFromIndex(0);
                Assert(renderedAvailable is not null && ReferenceEquals(renderedAvailable.Item, vm.AvailableMods[0]),
                    "Inactive visual row displays a stale installation.");
                var texts = Descendants<TextBlock>(renderedAvailable!).Select(t => t.Text).ToArray();
                Assert(texts.Contains(vm.AvailableMods[0].Name) && !texts.Contains(vm.Rows[0].Name),
                    "Inactive visual name is stale: expected " + vm.AvailableMods[0].Name + "; actual " + string.Join("|", texts));
                var markedRow = (DataGridRow)window.OrderGrid.ItemContainerGenerator.ContainerFromIndex(0);
                var expectedMarker = (SolidColorBrush)new MarkerBackgroundConverter().Convert(
                    [vm.Rows[0].MarkerColor!, 0, window.OrderGrid.Background], typeof(Brush), "", CultureInfo.InvariantCulture);
                Assert(markedRow.Background is SolidColorBrush visibleMarker && visibleMarker.Color == expectedMarker.Color,
                    "Grid background obscures the marker color: " + markedRow.Background + "; expected " + expectedMarker);
                foreach (var cell in Descendants<DataGridCell>(markedRow))
                    Assert(cell.Background is SolidColorBrush cellBrush && markedRow.Background is SolidColorBrush rowBrush
                        && cellBrush.Color == rowBrush.Color,
                        "Selected cell obscures marker: " + cell.Background + "; row " + markedRow.Background);
                var converter = new MarkerBackgroundConverter();
                var basis = theme == ThemePreference.Dark ? Brushes.Black : Brushes.White;
                var even = (SolidColorBrush)converter.Convert([MarkerPalette.Colors[0].Hex, 0, basis], typeof(Brush), "", CultureInfo.InvariantCulture);
                var odd = (SolidColorBrush)converter.Convert([MarkerPalette.Colors[0].Hex, 1, basis], typeof(Brush), "", CultureInfo.InvariantCulture);
                Assert(even.Color != odd.Color, "Same-color row brightness did not alternate.");
                // Offscreen RenderTargetBitmap can retain recycled row drawing instructions.
                foreach (var element in Descendants<UIElement>(window)) element.InvalidateVisual();
                await Task.Delay(60);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "彩色标记.png"));
                var batch = new BatchMarkWindow(2, vm.RetainedColors) { Owner = window };
                batch.Show(); batch.UpdateLayout();
                Assert(batch.FindName("TagBox") is null, "Obsolete tag input is still visible.");
                Assert(batch.RetainedColorBox.Items.Count == 16, "Color retention selector is incomplete.");
                Assert(Descendants<TextBlock>(batch).Any(t => t.Text.Contains("必须保留")), "Rule state is missing.");
                PrototypeSmokeCheck.SavePreview(batch, Path.Combine(output, theme + "按颜色保留.png"));
                batch.Close();
                var menu = window.AvailableGrid.ContextMenu!;
                var mark = menu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "标记");
                Assert(mark.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
                    .SequenceEqual(MarkerPalette.Colors.Select(color => color.Name).Append("清除颜色")),
                    "Color palette menu is incomplete or contains obsolete actions.");
                var recovery = new FileRecoveryWindow(new GameOrderStore(Path.Combine(fixture, "Config"), Path.Combine(fixture, "恢复备份")), () => { }, _ => { }) { Owner = window };
                recovery.Show(); recovery.UpdateLayout();
                PrototypeSmokeCheck.SavePreview(recovery, Path.Combine(output, theme + "中断恢复.png")); recovery.Close();
                var restore = new ModRestoreWindow(new LocalModArchive(fixture, fixture), () => { }, () => { }) { Owner = window };
                restore.Show(); restore.UpdateLayout();
                PrototypeSmokeCheck.SavePreview(restore, Path.Combine(output, theme + "模组恢复.png")); restore.Close();
            }
            vm.ApplyColor(null, vm.Rows, vm.AvailableMods);
            Assert(vm.Rows.All(r => r.MarkerColor is null) && vm.AvailableMods.All(r => r.MarkerColor is null), "Clear colors failed.");
            var rulePath = Path.Combine(UserStorage.DirectoryPath, "Data", "retained-colors.json");
            var savedRules = File.ReadAllBytes(rulePath);
            try
            {
                File.WriteAllText(rulePath, "{malformed");
                var broken = new MainViewModel();
                Assert(!broken.ColorRetentionAvailable, "Corrupt rules were treated as empty valid rules.");
                broken.Load(vm.CreateExportDocument(), "合成排序.xml");
                broken.ApplyScan(new ModScanner().Scan(new GamePaths { LocalModsDirectory = fixture, TargetVersionOverride = "1.7" }));
                Assert(new AiSortingSession(broken, UserStorage.DirectoryPath).ReadinessProblem?.Contains("保留规则读取失败") == true,
                    "AI sorting was not blocked after corrupt rule loading.");
                var refused = false;
                try { broken.SetColorRetention(red, true); } catch (InvalidOperationException) { refused = true; }
                Assert(refused, "Corrupt rules were silently overwritten.");
            }
            finally { File.WriteAllBytes(rulePath, savedRules); }
            File.WriteAllText(Path.Combine(output, "markers.json"), "{\"Passed\":true}");
        }
        finally
        {
            vm.MarkExported();
            window.Close();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
#endif

