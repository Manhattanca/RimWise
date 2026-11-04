#if DEBUG
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Desktop.Views;

namespace RimWise.Desktop;

/// <summary>Exercises production history and save flows against disposable Config copies only.</summary>
internal static class HistoryAndSaveSmokeCheck
{
    internal static void CheckDirectSaveWithoutChanges(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var original = vm.CreateOrderFile();
        var root = Path.Combine(output, "直接保存 [中文] & space");
        Directory.CreateDirectory(root);
        var store = new GameOrderStore(root, Path.Combine(root, "备份"));
        var bytes = new ModsConfigXmlFormat().Write(original.Configuration);
        File.WriteAllBytes(store.TargetPath, bytes);
        var previewShown = false;
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        guard.Tick += (_, _) =>
        {
            foreach (var preview in window.OwnedWindows.OfType<OrderPreviewWindow>().ToArray())
            {
                previewShown = true;
                preview.Close();
            }
        };
        try
        {
            vm.Load(original.Configuration, store.TargetPath);
            Assert((string)window.SaveOrderButton.Content == "保存排序", "Save button label is stale.");
            guard.Start();
            window.ApplyGamePlan(store.Prepare(vm.CreateExportDocument()), store.TargetPath);
            Assert(!previewShown && File.ReadAllBytes(store.TargetPath).SequenceEqual(bytes)
                && !Directory.Exists(store.BackupDirectory) && !vm.UndoCommand.CanExecute(null),
                "Unchanged direct save opened a preview or changed disk/history.");

            var stale = store.Prepare(vm.CreateExportDocument());
            File.WriteAllText(store.TargetPath, "外部修改");
            var rejected = false;
            try { window.ApplyGamePlan(stale, store.TargetPath); }
            catch (IOException) { rejected = true; }
            Assert(rejected && File.ReadAllText(store.TargetPath) == "外部修改" && !vm.UndoCommand.CanExecute(null),
                "Direct save overwrote an external change or added history after failure.");
        }
        finally
        {
            guard.Stop();
            vm.Load(original.Configuration, "ModsConfig.xml");
        }
    }

    internal static async Task RunAsync(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var original = vm.CreateExportDocument();
        var themes = ((App)Application.Current).Themes;
        var preference = themes.Preference;
        var width = window.Width;
        var height = window.Height;
        try
        {
            CheckHistory(window, original);
            await CheckSaveAsync(window, output, original);
            await CheckSavegameAsync(window, output);
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                themes.Select(theme, save: false);
                window.Width = 960;
                window.Height = 640;
                await Idle(window);
                var panel = (StackPanel)window.SaveOrderButton.Parent;
                var buttons = panel.Children.OfType<Button>().ToArray();
                var positions = buttons.Select(button => button.TranslatePoint(new Point(), window)).ToArray();
                Assert(buttons.Length == 10 && buttons.All(button => button.ActualWidth > 30), "Bottom actions missing.");
                for (var i = 1; i < buttons.Length; i++)
                    Assert(Math.Abs(positions[i].Y - positions[0].Y) < 1
                        && positions[i].X >= positions[i - 1].X + buttons[i - 1].ActualWidth, "Bottom actions overlap or wrap.");
                Assert(positions[0].X >= 0 && positions[^1].X + buttons[^1].ActualWidth <= window.ActualWidth,
                    "Bottom actions clipped at minimum width.");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "撤销与保存最小窗口.png"));
            }
        }
        finally
        {
            foreach (var preview in window.OwnedWindows.OfType<OrderPreviewWindow>().ToArray()) preview.Close();
            themes.Select(preference, save: false);
            window.Width = width;
            window.Height = height;
            vm.Load(original, "ModsConfig.xml");
            vm.SelectedRow = vm.Rows.FirstOrDefault(row => row.PackageId == "example.buildings");
        }
    }

    private static void CheckHistory(MainWindow window, ModsConfigDocument original)
    {
        var vm = window.ViewModel;
        vm.Load(original, "历史 样例.xml");
        var initial = Order(vm);
        vm.SelectedRow = vm.Rows[1];
        vm.MoveUpCommand.Execute(null);
        var edited = Order(vm);
        Assert(vm.HasChanges && !edited.SequenceEqual(initial), "Move did not enter history.");
        Assert(ReferenceEquals(window.UndoButton.Command, vm.UndoCommand)
            && ReferenceEquals(window.RedoButton.Command, vm.RedoCommand), "Bottom commands not wired.");
        window.UndoButton.Command.Execute(null);
        Assert(Order(vm).SequenceEqual(initial) && !vm.HasChanges && vm.RedoCommand.CanExecute(null), "Undo move/dirty checkpoint failed.");
        window.RedoButton.Command.Execute(null);
        Assert(Order(vm).SequenceEqual(edited) && vm.HasChanges, "Redo move failed.");
        vm.MarkExported();
        vm.UndoCommand.Execute(null);
        Assert(vm.HasChanges, "Undo before export should be dirty.");
        vm.RedoCommand.Execute(null);
        Assert(!vm.HasChanges, "Redo to exported checkpoint should be clean.");

        vm.SelectedAvailable = vm.AvailableMods.First(row => row.PackageId == "inactive.buildings");
        vm.AddToOrderCommand.Execute(null);
        Assert(vm.Rows.Count == initial.Length + 1, "Add did not execute.");
        vm.UndoCommand.Execute(null);
        Assert(Order(vm).SequenceEqual(edited) && !vm.HasChanges && vm.AvailableMods.Any(row => row.PackageId == "inactive.buildings"),
            "Undo add did not restore partition.");
        vm.RedoCommand.Execute(null);
        vm.RemoveFromOrderCommand.Execute(null);
        Assert(Order(vm).SequenceEqual(edited), "Remove did not execute.");
        vm.UndoCommand.Execute(null);
        Assert(vm.Rows.Count == initial.Length + 1, "Undo remove failed.");
        vm.SelectedRow = vm.Rows[1];
        vm.MoveDownCommand.Execute(null);
        Assert(!vm.RedoCommand.CanExecute(null), "New edit did not clear redo branch.");

        var beforeImport = Order(vm);
        var codec = new OrderFileCodec();
        const string json = """{"version":"1.7.4985","activeMods":["example.framework","example.buildings"],"knownExpansions":[],"future":{"note":"中文保留"}}""";
        var imported = codec.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        vm.ReplaceFileUndoable(imported, "导入.json");
        vm.SelectedRow = vm.Rows[1];
        vm.MoveUpCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert(Order(vm).SequenceEqual(beforeImport), "Undo import did not restore previous order.");
        vm.RedoCommand.Execute(null);
        vm.RedoCommand.Execute(null);
        Assert(vm.CreateOrderFile().SourceFormat == OrderFileFormat.RimSortJson
            && JsonNode.Parse(codec.Write(vm.CreateOrderFile(), OrderFileFormat.RimSortJson))!["future"]!["note"]!.GetValue<string>() == "中文保留",
            "Redo lost source-specific unknown fields.");
        // Search has its own native text undo and must not consume an order-history step.
        var current = Order(vm);
        window.SearchBox.IsUndoEnabled = false;
        window.SearchBox.IsUndoEnabled = true;
        window.SearchBox.Text = "history search";
        window.SearchBox.AppendText(" 中文");
        Assert(window.SearchBox.CanUndo, "Text input undo unavailable.");
        window.SearchBox.Undo();
        Assert(Order(vm).SequenceEqual(current), "Text undo modified order history.");
        vm.SearchText = "";
        var bindings = window.InputBindings.OfType<KeyBinding>().ToArray();
        Assert(bindings.Any(binding => binding.Key == Key.Z && binding.Modifiers == ModifierKeys.Control && ReferenceEquals(binding.Command, vm.UndoCommand))
            && bindings.Any(binding => binding.Key == Key.Y && binding.Modifiers == ModifierKeys.Control && ReferenceEquals(binding.Command, vm.RedoCommand))
            && bindings.Any(binding => binding.Key == Key.Z && binding.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && ReferenceEquals(binding.Command, vm.RedoCommand)),
            "Undo/redo keyboard bindings missing.");

        // 101 edits retain the latest 100 steps; neither scan nor search consumes a step.
        vm.Load(original, "历史上限.xml");
        vm.SelectedRow = vm.Rows[1];
        for (var i = 0; i < 101; i++)
            (i % 2 == 0 ? vm.MoveUpCommand : vm.MoveDownCommand).Execute(null);
        var count = 0;
        while (vm.UndoCommand.CanExecute(null)) { vm.UndoCommand.Execute(null); count++; }
        Assert(count == 100, "History bound or move history failed.");
    }

    private static async Task CheckSaveAsync(MainWindow window, string output, ModsConfigDocument original)
    {
        var root = Path.Combine(output, "保存 合集 [中文] & café '");
        var config = Path.Combine(root, "Config");
        Directory.CreateDirectory(config);
        var store = new GameOrderStore(config, Path.Combine(root, "游戏备份"));
        var target = store.TargetPath;
        var format = new ModsConfigXmlFormat();
        var initial = format.Write(original);
        File.WriteAllBytes(target, initial);
        var vm = window.ViewModel;
        vm.Load(original, target);
        vm.SelectedRow = vm.Rows[1];
        vm.MoveUpCommand.Execute(null);
        var editor = Order(vm);
        var plan = store.Prepare(vm.CreateExportDocument());
        window.ApplyGamePlan(plan, target);
        var saved = File.ReadAllBytes(target);
        Assert(!saved.SequenceEqual(initial) && !vm.HasChanges, "Direct save failed.");
        vm.UndoCommand.Execute(null);
        Assert(File.ReadAllBytes(target).SequenceEqual(initial) && Order(vm).SequenceEqual(editor) && vm.HasChanges,
            "Undo save did not restore disk/dirty state.");
        vm.RedoCommand.Execute(null);
        Assert(File.ReadAllBytes(target).SequenceEqual(saved) && !vm.HasChanges, "Redo save failed.");
        File.WriteAllText(target, "external change");
        vm.UndoCommand.Execute(null);
        Assert(File.ReadAllText(target) == "external change" && vm.Status.StartsWith("操作未完成") && !vm.HasChanges
            && vm.UndoCommand.CanExecute(null) && !vm.RedoCommand.CanExecute(null), "Blocked undo changed editor/history or external file.");
        File.WriteAllBytes(target, saved);
        vm.UndoCommand.Execute(null);
        Assert(File.ReadAllBytes(target).SequenceEqual(initial), "Blocked undo not retryable.");
        vm.RedoCommand.Execute(null);

        // Durable recovery is one history operation across editor and disk.
        vm.SelectedRow = vm.Rows[1];
        vm.RemoveFromOrderCommand.Execute(null);
        var beforeRestore = Order(vm);
        // Several writes can share the same second; GUID filename order is not creation order.
        var backupPath = Directory.EnumerateFiles(store.BackupDirectory, "*-before.xml")
            .First(path => File.ReadAllBytes(path).SequenceEqual(initial));
        var restore = store.PrepareRestore(backupPath);
        var restoreConfirm = PreviewResponse(window, confirm: true);
        window.PreviewAndRestoreGamePlan(restore, target);
        await restoreConfirm;
        Assert(File.ReadAllBytes(target).SequenceEqual(initial) && Order(vm).SequenceEqual(original.Order.PackageIds) && !vm.HasChanges,
            "Recovery did not restore original file and editor.");
        vm.UndoCommand.Execute(null);
        Assert(File.ReadAllBytes(target).SequenceEqual(saved) && Order(vm).SequenceEqual(beforeRestore) && vm.HasChanges,
            "Recovery undo was not a single operation.");
        vm.RedoCommand.Execute(null);
        Assert(File.ReadAllBytes(target).SequenceEqual(initial) && Order(vm).SequenceEqual(original.Order.PackageIds), "Recovery redo failed.");
    }

    private static async Task CheckSavegameAsync(MainWindow window, string output)
    {
        const string text = "<savegame><meta><gameVersion>1.7.4985</gameVersion><modIds><li>example.framework</li><li>example.buildings</li></modIds><modNames><li>框架</li><li>建筑</li></modNames><modSteamIds><li>100</li><li>0</li></modSteamIds></meta><game><world>不得写入导出</world></game></savegame>";
        var path = Path.Combine(output, "中文 存档 [样例] & café '.rws");
        File.WriteAllText(path, text);
        var source = File.ReadAllBytes(path);
        var vm = window.ViewModel;
        var before = Order(vm);
        var file = new OrderFileService().Read(path);
        var confirm = PreviewResponse(window, confirm: true);
        window.PreviewAndLoad(file, path, "导入存档排序");
        await confirm;
        Assert(vm.CreateOrderFile().SourceFormat == OrderFileFormat.SavegameRws && vm.Rows.Count == 2, "Save import UI failed.");
        vm.UndoCommand.Execute(null);
        Assert(Order(vm).SequenceEqual(before), "Undo save import failed.");
        vm.RedoCommand.Execute(null);
        var backupStore = new OrderBackupStore(Path.Combine(output, "存档排序快照"));
        var backup = backupStore.Save(vm.CreateOrderFile(), path);
        vm.ReplaceFileUndoable(backupStore.Read(backup), backup);
        vm.UndoCommand.Execute(null);
        Assert(vm.CreateOrderFile().SourceFormat == OrderFileFormat.SavegameRws, "Undo snapshot restore lost original source.");
        vm.RedoCommand.Execute(null);
        Assert(vm.CreateOrderFile().SourceFormat == OrderFileFormat.RmlXml, "Redo extracted snapshot failed.");
        foreach (var target in OrderFileCodec.ExportFormats)
        {
            var exported = new OrderFileCodec().Write(vm.CreateOrderFile(), target);
            Assert(new OrderFileCodec().Read(new MemoryStream(exported)).Configuration.Order.PackageIds.SequenceEqual(Order(vm)),
                "Save import conversion lost order.");
            Assert(!Encoding.UTF8.GetString(exported).Contains("不得写入导出"), "Save body leaked into export.");
        }
        Assert(File.ReadAllBytes(path).SequenceEqual(source), "Source save was modified.");
    }

    private static DispatcherOperation PreviewResponse(MainWindow window, bool confirm) => window.Dispatcher.BeginInvoke(() =>
    {
        var preview = window.OwnedWindows.OfType<OrderPreviewWindow>().Single();
        if (confirm) preview.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        else preview.DialogResult = false;
    }, DispatcherPriority.ApplicationIdle);
    private static string[] Order(MainViewModel vm) => vm.Rows.Select(row => row.PackageId).ToArray();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
#endif
