#if DEBUG
using System.IO;
using System.Windows.Threading;
using RimWise.Desktop.Presentation;

namespace RimWise.Desktop;

internal static class RetentionSmokeCheck
{
    internal static async Task RunAsync(MainWindow parent, string output)
    {
        var window = new MainWindow(initializePaths: false) { Owner = parent };
        window.Show();
        window.LoadSample();
        var vm = window.ViewModel;
        var label = Path.Combine(output, "必须保留 配置 [样例].xml");
        var document = vm.CreateOrderFile();
        try
        {
            vm.Load(document.Configuration, label);
            var protectedRow = vm.Rows[1];
            vm.ToggleRetained(protectedRow);
            Assert(protectedRow.IsRetained && vm.HasRetainedMods && !vm.RemoveFromOrderCommand.CanExecute(null),
                "Retained marker did not protect manual removal.");
            vm.ClearOrderCommand.Execute(null);
            Assert(vm.Rows.Contains(protectedRow) && protectedRow.IsRetained && vm.LastAction.Contains("清空"),
                "Clear removed a protected mod.");
            vm.UndoCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            Assert(!vm.Rows[1].IsRetained,
                "Undo did not restore the marker state.");
            vm.RedoCommand.Execute(null);
            Assert(vm.Rows[1].IsRetained && !vm.RemoveFromOrderCommand.CanExecute(null),
                "Cancel undo did not restore the protected marker.");
            var reloaded = new MainViewModel();
            reloaded.Load(document.Configuration, label);
            Assert(reloaded.Rows[1].IsRetained, "Retained marker did not persist by order source.");
            reloaded.ToggleRetained(reloaded.Rows[1]);
            var cleared = new MainViewModel();
            cleared.Load(document.Configuration, label);
            Assert(!cleared.Rows[1].IsRetained, "Retained marker could not be cleared.");
            Assert(!window.OrderGrid.Columns.Any(column => column.Header?.ToString() == "保留")
                && !window.AvailableGrid.Columns.Any(column => column.Header?.ToString() == "保留"),
                "Retention controls should stay hidden before AI permission tiers are enabled.");
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "必须保留标记.png"));
            File.WriteAllText(Path.Combine(output, "retention.json"), "{\"Passed\":true}");
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
}
#endif
