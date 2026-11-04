#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Formats;

namespace RimWise.Desktop;

internal static class NamedCollectionSmokeCheck
{
    internal static async Task RunAsync(MainWindow parent, string output)
    {
        var directory = Path.Combine(output, "命名合集 UI [中文] & test");
        var store = new NamedCollectionStore(directory);
        var window = new CollectionWindow(store, parent.ViewModel.CreateOrderFile(), "当前排序.xml") { Owner = parent };
        window.Show();
        try
        {
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            window.NameBox.Text = "测试合集";
            window.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert(window.CollectionList.Items.Count == 1, "Named collection window did not save the current order.");
            Assert(window.CollectionList.Items.OfType<NamedCollectionSummary>().Single().Name == "测试合集",
                "Named collection name did not render.");
            Assert(window.SaveButton.IsEnabled && window.LoadButton.IsEnabled, "Named collection button state is incorrect.");
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "命名合集窗口.png"));
            File.WriteAllText(Path.Combine(output, "named-collection-ui.json"), "{\"Passed\":true}");
        }
        finally { window.Close(); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
