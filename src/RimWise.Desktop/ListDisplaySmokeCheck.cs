#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class ListDisplaySmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var enabledMods = vm.Rows.Select((row, index) => new ModInstallation(Path.Combine(output, "启用样例", row.PackageId), row.PackageId,
            index == 0 ? ModSource.Game : index % 2 == 0 ? ModSource.Workshop : ModSource.Local, "",
            new(index == 0 ? "Core" : "启用模组 " + index, "测试作者", row.PackageId, ["1.7"], "", ModRequirements.Empty, false), [])
            { CodeKind = index == 0 ? ModCodeKind.Unknown : index % 2 == 0 ? ModCodeKind.CSharp : ModCodeKind.Xml }).ToArray();
        vm.ApplyScan(new ModScanResult(new(null, "", ""), GameVersion.Parse("1.7"),
            new(vm.AvailableMods.Select(row => row.Installation).Concat(enabledMods).ToArray()), []));
        SourceFilteringSmokeCheck.Run(window, output);
        var revision = vm.ContentRevision;
        var order = vm.Rows.Select(row => row.PackageId).ToArray();
        var original = new[] { window.DetailsColumn.Width, window.AvailableColumn.Width, window.OrderColumn.Width };
        async Task Layout() => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            foreach (var display in new[] { ModBadgeDisplay.Icons, ModBadgeDisplay.Text })
            {
                window.SetBadgeDisplay(display);
                await Layout();
                Assert(vm.ShowModBadgeText == (display == ModBadgeDisplay.Text), "Badge mode did not change.");
                Assert(new ListDisplaySettingsStore(UserStorage.DirectoryPath).Load().Badges == display, "Badge choice not saved.");
                foreach (var grid in new[] { window.AvailableGrid, window.OrderGrid })
                {
                    Assert(!grid.Columns.Any(column => Equals(column.Header, "来源")), "Right-side source column still exists.");
                    var first = Descendants(grid).OfType<DockPanel>().FirstOrDefault(panel => panel.Children.OfType<ContentControl>()
                        .Any(child => child.ContentTemplate == window.FindResource("ModBadges")));
                    Assert(first is not null, "No row badges rendered.");
                    var badges = first!.Children.OfType<ContentControl>().First();
                    var name = first.Children.OfType<TextBlock>().Single();
                    Assert(DockPanel.GetDock(badges) == Dock.Left && badges.TranslatePoint(new(), first).X < name.TranslatePoint(new(), first).X,
                        "Badges are not left of name.");
                    grid.SelectedItems.Clear();
                    await Layout();
                    var firstRow = Descendants(grid).OfType<DataGridRow>().First();
                    var before = Descendants(firstRow).OfType<DataGridCell>().Select(cell => cell.TranslatePoint(new(), grid)).ToArray();
                    foreach (var item in grid.Items.Cast<object>().Take(2)) grid.SelectedItems.Add(item);
                    var cell = Descendants(grid).OfType<DataGridCell>().First(c => c.IsSelected);
                    cell.Focus();
                    await Layout();
                    AssertSelectedText(grid);
                    var after = Descendants(firstRow).OfType<DataGridCell>().Select(cell => cell.TranslatePoint(new(), grid)).ToArray();
                    Assert(before.Length == after.Length && before.Zip(after).All(pair => Math.Abs(pair.First.X - pair.Second.X) < 0.1),
                        "Selecting a Mod shifted its cells or columns.");
                    window.SourceFilterBox.Focus();
                    await Layout();
                    AssertSelectedText(grid);
                }
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"{theme}-{display}-模组标识.png"));
            }
        }
        var ambiguous = new Presentation.LoadOrderRow("duplicate", 1);
        var sample = vm.AvailableMods[0].Installation with { CodeKind = ModCodeKind.CSharp };
        ambiguous.SetMatch(new("duplicate", [sample, sample with { Source = ModSource.Workshop }]));
        Assert(ambiguous.CodeKind == ModCodeKind.Unknown && ambiguous.SourceLabel == "多个来源", "Ambiguous source guessed a mod type.");
        var xml = new Presentation.InstalledModRow(sample with { CodeKind = ModCodeKind.Xml }, false);
        Assert(xml.CodeKindIcon == "XML", "XML icon is not readable.");
        var translation = new Presentation.InstalledModRow(sample with { CodeKind = ModCodeKind.Xml, ContentKinds = ModContentKinds.Translation }, false);
        Assert(translation.CodeKindIcon == "翻译" && translation.CodeKindTooltip.Contains("翻译"), "Translation kind missing.");
        var resources = new Presentation.InstalledModRow(sample with { CodeKind = ModCodeKind.Xml, ContentKinds = ModContentKinds.Textures | ModContentKinds.Audio }, false);
        Assert(resources.CodeKindIcon == "资源" && resources.CodeKindTooltip.Contains("音效"), "Resource kind missing.");
        var mixed = new Presentation.InstalledModRow(sample with { CodeKind = ModCodeKind.CSharp, ContentKinds = ModContentKinds.ManagedCode | ModContentKinds.Patches | ModContentKinds.Translation }, false);
        Assert(mixed.CodeKindIcon == "C#" && mixed.CodeKindTooltip.Contains("XML 补丁") && mixed.CodeKindTooltip.Contains("翻译"), "Mixed content lost secondary features.");
        foreach (var splitter in new[] { window.DetailsSplitter, window.OrderSplitter })
        {
            var peer = new GridSplitterAutomationPeer(splitter);
            var transform = (ITransformProvider)peer.GetPattern(PatternInterface.Transform);
            var before = splitter == window.DetailsSplitter ? window.DetailsColumn.ActualWidth : window.OrderColumn.ActualWidth;
            transform.Move(24, 0);
            await Layout();
            var after = splitter == window.DetailsSplitter ? window.DetailsColumn.ActualWidth : window.OrderColumn.ActualWidth;
            Assert(Math.Abs(before - after) > 10, "Splitter failed to resize adjacent columns.");
            splitter.RaiseEvent(new DragCompletedEventArgs(24, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            var stored = new ListDisplaySettingsStore(UserStorage.DirectoryPath).Load();
            Assert(Math.Abs(stored.DetailsWidth + stored.AvailableWidth + stored.OrderWidth - 1) < 0.001, "Pane ratios not saved.");
        }
        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "拖动调宽.png"));
        var savedWidths = new ListDisplaySettingsStore(UserStorage.DirectoryPath).Load();
        window.SourceFilterBox.SelectedIndex = 1;
        window.OrderSourceFilterBox.SelectedIndex = 2;
        var restored = new MainWindow(initializePaths: false);
        Assert(restored.SourceFilterBox.SelectedIndex == 1 && restored.OrderSourceFilterBox.SelectedIndex == 2,
            "New window did not restore independent source filters.");
        Assert(Math.Abs(restored.DetailsColumn.Width.Value - savedWidths.DetailsWidth) < 0.0001
            && Math.Abs(restored.AvailableColumn.Width.Value - savedWidths.AvailableWidth) < 0.0001
            && Math.Abs(restored.OrderColumn.Width.Value - savedWidths.OrderWidth) < 0.0001, "New window did not restore pane ratios.");
        restored.Close();
        window.SourceFilterBox.SelectedIndex = 0;
        window.OrderSourceFilterBox.SelectedIndex = 0;
        window.DetailsColumn.Width = original[0]; window.AvailableColumn.Width = original[1]; window.OrderColumn.Width = original[2];
        window.SetBadgeDisplay(ModBadgeDisplay.Icons);
        Assert(vm.ContentRevision == revision && vm.Rows.Select(row => row.PackageId).SequenceEqual(order), "Display preferences edited load order.");
        vm.SelectedAvailable = vm.AvailableMods.FirstOrDefault(row => row.Installation.ContentKinds.HasFlag(ModContentKinds.Translation));
        Assert(vm.HasSelectedContent && vm.SelectedContent.Contains("翻译"), "Details did not expose content types.");
        File.WriteAllText(Path.Combine(output, "list-display.json"), "{\"Passed\":true}");
        await ModListLayoutSmokeCheck.RunAsync(window, output);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void AssertSelectedText(DataGrid grid)
    {
        var expected = ((SolidColorBrush)Application.Current.FindResource("TextBrush")).Color;
        var cells = Descendants(grid).OfType<DataGridCell>().Where(cell => cell.IsSelected).ToArray();
        Assert(cells.Length > 0, "No selected cells rendered.");
        foreach (var cell in cells)
        {
            Assert(cell.Foreground is SolidColorBrush brush && brush.Color == expected,
                "Selected cell inherited the Windows highlight foreground instead of the application theme.");
            Assert(cell.FocusVisualStyle is null && VisualTreeHelper.GetChild(cell, 0) is Border { BorderThickness: var thickness } && thickness == new Thickness(0),
                "Selected Mod cell still renders a focus rectangle or column border.");
        }
        if (grid.Name == "AvailableGrid")
            foreach (var text in cells.SelectMany(Descendants).OfType<TextBlock>().Where(text => text.Style == grid.FindResource("ModName")))
                Assert(text.Foreground is SolidColorBrush brush && brush.Color == expected, "Selected Mod name is unreadable.");
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
#endif
