#if DEBUG
using System.IO;
using RimWise.Core;
using RimWise.Desktop.Presentation;

namespace RimWise.Desktop;

internal static class SourceFilteringSmokeCheck
{
    internal static void Run(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var order = vm.Rows.Select(row => (row.PackageId, row.Index)).ToArray();
        var revision = vm.ContentRevision;
        var history = (vm.HasChanges, vm.UndoCommand.CanExecute(null), vm.LastAction);
        window.OrderSourceFilterBox.SelectedIndex = 0;
        foreach (var (index, source) in new[] { (1, ModSource.Local), (2, ModSource.Workshop) })
        {
            var hidden = vm.AvailableMods.First(row => row.Installation.Source != source);
            vm.SelectAvailableRows([hidden]);
            window.SourceFilterBox.SelectedIndex = index;
            var available = vm.VisibleAvailableMods.Cast<InstalledModRow>().ToArray();
            var enabled = vm.VisibleRows.Cast<LoadOrderRow>().ToArray();
            Assert(available.Length > 0 && available.All(row => row.Installation.Source == source), "Available source filter failed.");
            Assert(enabled.Length == vm.Rows.Count, "Inactive source filter unexpectedly changed enabled view.");
            Assert(!vm.SelectedAvailableRows.Contains(hidden), "Hidden row remained selected for batch actions.");
            Assert(vm.AvailableCountLabel.StartsWith(available.Length + " / "), "Filtered count failed.");
            vm.SearchText = available[0].PackageId;
            Assert(vm.VisibleAvailableMods.Cast<InstalledModRow>().All(row => row.Installation.Source == source), "Search bypassed source filter.");
            vm.SearchText = "";
            foreach (var (activeIndex, activeSource) in new[] { (1, ModSource.Local), (2, ModSource.Workshop) })
            {
                window.OrderSourceFilterBox.SelectedIndex = activeIndex;
                Assert(vm.VisibleRows.Cast<LoadOrderRow>().Any()
                    && vm.VisibleRows.Cast<LoadOrderRow>().All(row => row.Match!.Candidates.Any(mod => mod.Source == activeSource)), "Independent enabled filter failed.");
                Assert(vm.VisibleAvailableMods.Cast<InstalledModRow>().All(row => row.Installation.Source == source), "Enabled filter changed inactive view.");
            }
            window.OrderSourceFilterBox.SelectedIndex = 0;
        }
        window.SourceFilterBox.SelectedIndex = 0;
        Assert(vm.VisibleRows.Cast<object>().Count() == vm.Rows.Count
            && vm.VisibleAvailableMods.Cast<object>().Count() == vm.AvailableMods.Count, "All sources failed to restore lists.");
        Assert(order.SequenceEqual(vm.Rows.Select(row => (row.PackageId, row.Index))) && vm.ContentRevision == revision
            && history == (vm.HasChanges, vm.UndoCommand.CanExecute(null), vm.LastAction), "Filtering mutated order or history.");
        File.WriteAllText(Path.Combine(output, "source-filtering.json"), "{\"Passed\":true}");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
