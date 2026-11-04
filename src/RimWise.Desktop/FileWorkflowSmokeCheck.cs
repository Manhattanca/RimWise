#if DEBUG
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class FileWorkflowSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var original = vm.CreateExportDocument();
        var themes = ((App)Application.Current).Themes;
        var originalTheme = themes.Preference;
        const string json = """{"version":"1.7.4985","activeMods":["example.framework","example.buildings"],"knownExpansions":[],"future":{"note":"中文字段保留"}}""";
        var codec = new OrderFileCodec();
        var imported = codec.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        try
        {
            var revision = vm.ContentRevision;
            var cancelPreview = window.Dispatcher.BeginInvoke(() =>
            {
                var preview = window.OwnedWindows.OfType<OrderPreviewWindow>().Single();
                PrototypeSmokeCheck.SavePreview(preview, Path.Combine(output, "导入差异预览.png"));
                preview.DialogResult = false;
            }, DispatcherPriority.ApplicationIdle);
            window.PreviewAndLoad(imported, "中文 导入.json", "导入排序");
            await cancelPreview;
            if (vm.ContentRevision != revision || !vm.Rows.Select(row => row.PackageId).SequenceEqual(original.Order.PackageIds))
                throw new InvalidOperationException("Cancelling import preview changed the order.");

            var confirmPreview = window.Dispatcher.BeginInvoke(() =>
            {
                var preview = window.OwnedWindows.OfType<OrderPreviewWindow>().Single();
                preview.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
            window.PreviewAndLoad(imported, "中文 导入.json", "导入排序");
            await confirmPreview;
            vm.SelectedRow = vm.Rows[1];
            vm.MoveUpCommand.Execute(null);
            var edited = vm.CreateOrderFile();
            if (!vm.HasChanges || edited.SourceFormat != OrderFileFormat.RimSortJson
                || JsonNode.Parse(codec.Write(edited, OrderFileFormat.RimSortJson))!["future"]!["note"]!.GetValue<string>() != "中文字段保留")
                throw new InvalidOperationException("Import and edit lost source-specific metadata.");

            var store = new OrderBackupStore(Path.Combine(output, "排序备份 [中文] & 测试"));
            var backup = store.Save(edited, vm.SourceFileName);
            var restored = store.Read(backup);
            vm.Load(original, "其他排序.xml");
            var confirmRestore = window.Dispatcher.BeginInvoke(() =>
            {
                var preview = window.OwnedWindows.OfType<OrderPreviewWindow>().Single();
                PrototypeSmokeCheck.SavePreview(preview, Path.Combine(output, "恢复差异预览.png"));
                preview.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
            window.PreviewAndLoad(restored, backup, "恢复排序备份");
            await confirmRestore;
            if (!vm.Rows.Select(row => row.PackageId).SequenceEqual(edited.Configuration.Order.PackageIds)
                || vm.CreateOrderFile().SourceFormat != OrderFileFormat.RimSortJson)
                throw new InvalidOperationException("Restore preview did not restore the edited source format.");

            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                themes.Select(theme, save: false);
                var preview = window.CreateExportPreview(vm.CreateOrderFile(), OrderFileFormat.ModListXml);
                preview.Width = 560;
                preview.Height = 380;
                preview.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                preview.UpdateLayout();
                if (!preview.Notice.Text.Contains("不记录游戏版本") || preview.PreviewGrid.ActualHeight < 80
                    || preview.ConfirmButton.ActualHeight < 25)
                    throw new InvalidOperationException("Conversion preview lost warnings or controls at minimum size.");
                PrototypeSmokeCheck.SavePreview(preview, Path.Combine(output, theme + "转换预览.png"));
                preview.Close();
            }
        }
        finally
        {
            foreach (var preview in window.OwnedWindows.OfType<OrderPreviewWindow>().ToArray()) preview.Close();
            themes.Select(originalTheme, save: false);
            vm.Load(original, "ModsConfig.xml");
            vm.SelectedRow = vm.Rows.FirstOrDefault(row => row.PackageId == "example.buildings");
        }
    }
}
#endif
