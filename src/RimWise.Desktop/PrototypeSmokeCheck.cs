#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Formats;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;
using RimWise.Desktop.Services;

namespace RimWise.Desktop;

/// <summary>Development-only UI check; absent from Release builds.</summary>
internal static class PrototypeSmokeCheck
{
    public static void Attach(MainWindow window, string outputDirectory)
    {
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                await StartupSmokeCheck.RunAsync(window, outputDirectory);
                window.LoadSample();
                var vm = window.ViewModel;
                if (vm.Rows.Count != 5) throw new InvalidOperationException("Sample rows missing.");
                var availableMenu = window.AvailableGrid.ContextMenu?.Items.OfType<System.Windows.Controls.MenuItem>().Select(item => item.Header?.ToString()).ToArray();
                var orderMenu = window.OrderGrid.ContextMenu?.Items.OfType<System.Windows.Controls.MenuItem>().Select(item => item.Header?.ToString()).ToArray();
                if (availableMenu is null || orderMenu is null
                    || !availableMenu.SequenceEqual(new[] { "加入到排序中…", "从排序中移除", "删除本地模组文件…", "从创意工坊取消订阅", "标记", "通过 Steam 订阅下载…" })
                    || !orderMenu.SequenceEqual(availableMenu))
                    throw new InvalidOperationException("Right-click context menu is missing or duplicated.");
                vm.SelectedRow = vm.Rows[2];
                vm.MoveUpCommand.Execute(null);
                if (vm.Rows[1].PackageId != "example.buildings")
                    throw new InvalidOperationException("Move command did not update the order.");
                vm.SearchText = "weapons";
                if (vm.VisibleRows.Cast<object>().Count() != 1 || vm.MoveUpCommand.CanExecute(null))
                    throw new InvalidOperationException("Search or filtered move protection failed.");
                vm.SearchText = string.Empty;
                vm.SelectedRow = vm.Rows[1];

                var exportedPath = Path.Combine(outputDirectory, "中文 导出 样例.xml");
                var files = new PrototypeFileService();
                files.ExportNew(exportedPath, vm.CreateExportDocument());
                if (!files.Read(exportedPath).Order.PackageIds.SequenceEqual(vm.Rows.Select(row => row.PackageId)))
                    throw new InvalidOperationException("Unicode path export did not round trip.");
                vm.MarkExported();
                await ScannerSmokeCheck.RunAsync(window, outputDirectory);
                await DiagnosticsSmokeCheck.RunAsync(window, outputDirectory);
                await FileWorkflowSmokeCheck.RunAsync(window, outputDirectory);
                await RetentionSmokeCheck.RunAsync(window, outputDirectory);
                await NamedCollectionSmokeCheck.RunAsync(window, outputDirectory);
                await HistoryAndSaveSmokeCheck.RunAsync(window, outputDirectory);
                await ClearAndActionSmokeCheck.RunAsync(window, outputDirectory);
                await SortingAndRefreshSmokeCheck.RunAsync(window, outputDirectory);
                await StartupCacheSmokeCheck.RunAsync(window, outputDirectory);
                await BatchEditingSmokeCheck.RunAsync(window, outputDirectory);
                await MarkerSmokeCheck.RunAsync(window, outputDirectory);

                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                SavePreview(window, Path.Combine(outputDirectory, "界面预览.png"));
                window.Width = 960;
                window.Height = 640;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                SavePreview(window, Path.Combine(outputDirectory, "最小窗口预览.png"));

                // Inspect the hierarchy without relying on mouse capture in a background UI check.
                if (!window.MainMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
                        .SequenceEqual(new[] { "文件", "设置", "游戏", "帮助" })
                    || !window.FileMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
                        .SequenceEqual(new[] { "导入排序或存档…", "合集管理…", "备份当前排序", "恢复排序备份…", "恢复游戏配置…", "恢复中断写入…", "恢复任务…", "恢复移除的模组…", "退出" }))
                    throw new InvalidOperationException("Compact menu contains missing or duplicate actions.");
                if (!window.SettingsMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
                        .SequenceEqual(new[] { "路径", "外观", "AI 设置…", "AI 用量预估…", "导出诊断日志…", "自定义排序规则…", "数据库来源…", "模组标识" })
                    || window.PathsMenu.Items.Count != 2 || window.AppearanceMenu.Items.Count != 3)
                    throw new InvalidOperationException("Settings menu hierarchy missing.");

                var settings = new PathSettingsWindow(new GamePaths
                {
                    GameDirectory = Path.Combine(outputDirectory, "Steam 游戏库", "steamapps", "common", "RimWorld"),
                    LocalModsDirectory = Path.Combine(outputDirectory, "Steam 游戏库", "steamapps", "common", "RimWorld", "Mods"),
                    WorkshopDirectory = Path.Combine(outputDirectory, "Steam 游戏库", "steamapps", "workshop", "content", "294100")
                }, new PathSettingsStore(outputDirectory)) { Owner = window };
                settings.Show();
                await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                settings.UpdateLayout();
                if (!settings.GamePathBox.Text.Contains("Steam 游戏库", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unicode settings input not displayed.");
                SavePreview(settings, Path.Combine(outputDirectory, "路径设置预览.png"));
                await ThemeSmokeCheck.RunAsync(window, settings, outputDirectory);
                File.WriteAllText(Path.Combine(outputDirectory, "smoke-stage.txt"), "Steam path detection");
                await settings.DetectAsync();
                File.WriteAllText(Path.Combine(outputDirectory, "smoke-stage.txt"), "Steam root discovery");
                var discovery = await Task.Run(() => SteamRootLocator.Discover());
                if (discovery.Installations.Count > 0)
                {
                    File.WriteAllText(Path.Combine(outputDirectory, "smoke-stage.txt"), "Installed metadata scan");
                    await ScannerSmokeCheck.CheckInstalledGameAsync(discovery.Installations[0].Paths, outputDirectory);
                }
                File.WriteAllText(Path.Combine(outputDirectory, "smoke-stage.txt"), "Final report");
                if (discovery.Installations.Count > 0 && settings.GamePathBox.Text != discovery.Installations[0].Paths.GameDirectory)
                    throw new InvalidOperationException("Settings UI did not display detected game path.");
                settings.Close();
                File.WriteAllText(Path.Combine(outputDirectory, "steam-discovery.json"), JsonSerializer.Serialize(discovery, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    BaseDirectory = UserStorage.DirectoryPath,
                    SampleRows = vm.Rows.Count,
                    SortingAndRefreshChecks = "refresh reloads both panes; cancellation and failure preserve history, last action and pending order; stale refresh preserves new edit; sort respects whole filtered order, no-op history, undo/cancel undo; cycle tooltip and error summary; light/dark minimum-size single-row buttons",
                    SteamInstallationsFound = discovery.Installations.Count,
                    Checks = new[] { "WPF startup", "clear protects active base and DLC, clears hidden mods and is one undo step", "clear preserves source metadata and inactive partition", "clear handles empty modern and legacy orders", "last action persists through search and transient status", "one-based positions 1 2 99 100 including undo and cancel undo", "ten bottom actions and activity render in both themes", "undo/redo move add remove and import with metadata", "100-step history and redo branching", "export dirty checkpoints and native text undo", "Ctrl+Z Ctrl+Y Ctrl+Shift+Z bindings", "direct game save without confirmation", "game save undo redo exact bytes and dirty state", "external change blocks UI rollback without losing history", "durable recovery restores editor and disk in one step", "read-only RWS import undo redo and all target conversions", "light/dark bottom actions remain single row at minimum size", "automatic current config and catalogue load", "custom Unicode Config directory", "startup read leaves game file unchanged", "stale startup read preserves manual import and edit/export", "missing/malformed config preserves document", "missing startup config remains actionable", "permanent details left of inactive and active lists", "description before paths before compatibility", "issue summary buttons filter and locate", "issue window follows diagnostics reset", "icon hit target and rendered reason tooltip", "import preview cancel preserves edits", "import confirm loads source metadata", "edited source format retained through backup restore preview", "conversion warnings render in both themes at minimum size", "multiline mod description", "local preview and workshop link", "malformed/oversized preview ignored", "stale preview cannot replace current selection", "preview file lock released", "embedded sample", "move", "two-pane search", "Unicode export/read", "minimum window", "settings menu hierarchy", "path settings render", "light/dark live switch", "severity colors follow theme", "system preference response", "menu popup render", "local Steam discovery report", "inactive and active partition", "automatic order-scoped diagnostics", "dependency move immediately changes severity", "add/remove immediately updates severity", "empty order clears severity", "missing and duplicate matches", "scan retains pending order edits and diagnostics", "UI scan cancellation", "installed game and current config report", "context menu add appends to end and reports one-based position", "retention protection remains backend-only before AI tiers", "named collection save/list UI" }
                }));
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outputDirectory, "failure.txt"), ex.ToString());
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }

    internal static void SavePreview(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var margin = content.Margin;
        var width = content.ActualWidth + margin.Left + margin.Right;
        var height = content.ActualHeight + margin.Top + margin.Bottom;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        // Render includes the root's layout offset. The bitmap includes margins
        // once, so bottom buttons remain visible without shifting the content.
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    internal static void SavePreview(FrameworkElement content, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
#endif
