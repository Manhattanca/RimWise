#if DEBUG
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Formats;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class ClearAndActionSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var original = vm.CreateOrderFile();
        var themes = ((App)Application.Current).Themes;
        var preference = themes.Preference;
        var width = window.Width;
        var height = window.Height;
        try
        {
            var fresh = new MainViewModel();
            Assert(!fresh.ClearOrderCommand.CanExecute(null) && !fresh.HasLastAction, "Empty editor can clear.");
            CheckClear(window);
            CheckPositions(vm, original.Configuration);
            var action = vm.LastAction;
            vm.SearchText = "test.position";
            vm.Status = "正在刷新模组…";
            Assert(vm.LastAction == action && vm.Rows.Select(row => row.Index).SequenceEqual(Enumerable.Range(1, 100)),
                "Search/status changed action or absolute row positions.");
            vm.SearchText = "";
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                themes.Select(theme, save: false);
                window.Width = 960;
                window.Height = 640;
                await Idle(window);
                Assert(window.RedoButton.Content as string == "取消撤销" && ReferenceEquals(window.ClearOrderButton.Command, vm.ClearOrderCommand),
                    "Rename or clear button binding missing.");
                Assert(window.LastActionText.Text == vm.LastAction && window.LastActionText.ActualWidth >= 220,
                    "Last action not visible or usable at minimum size.");
                var panel = (StackPanel)window.SaveOrderButton.Parent;
                var buttons = panel.Children.OfType<Button>().ToArray();
                var points = buttons.Select(button => button.TranslatePoint(new Point(), window)).ToArray();
                Assert(buttons.Length == 10 && points[0].X > window.LastActionText.TranslatePoint(new Point(), window).X + window.LastActionText.ActualWidth,
                    "Bottom buttons overlap activity.");
                for (var i = 1; i < buttons.Length; i++)
                    Assert(Math.Abs(points[i].Y - points[0].Y) < 1 && points[i].X >= points[i - 1].X + buttons[i - 1].ActualWidth,
                        "Bottom buttons wrap or overlap.");
                Assert(points[^1].X + buttons[^1].ActualWidth <= window.ActualWidth, "Bottom buttons clipped.");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "清空与操作记录最小窗口.png"));
                window.Width = width;
                window.Height = height;
                await Idle(window);
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, theme + "清空与操作记录.png"));
            }
        }
        finally
        {
            themes.Select(preference, save: false);
            window.Width = width;
            window.Height = height;
            vm.LoadFile(original, "ModsConfig.xml");
            vm.SelectedRow = vm.Rows.FirstOrDefault(row => row.PackageId == "example.buildings");
        }
    }

    private static void CheckClear(MainWindow window)
    {
        var vm = window.ViewModel;
        var codec = new OrderFileCodec();
        const string json = """{"version":"1.7.4985","activeMods":["ludeon.rimworld","example.buildings","missing.mod","ludeon.rimworld.royalty","ludeon.rimworld.custommod","ludeon.rimworld.odyssey"],"knownExpansions":[],"future":{"note":"中文字段保留"}}""";
        vm.LoadFile(codec.Read(new MemoryStream(Encoding.UTF8.GetBytes(json))), "中文 清空样例.json");
        var original = Order(vm);
        vm.SearchText = "example.buildings";
        Assert(vm.ClearOrderCommand.CanExecute(null), "Cannot clear ordinary mods.");
        window.ClearOrderButton.Command.Execute(null);
        Assert(Order(vm).SequenceEqual(new[] { "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.odyssey" })
            && vm.Rows.Select(row => row.Index).SequenceEqual(new[] { 1, 2, 3 }) && vm.SearchText.Length == 0 && vm.HasChanges,
            "Clear did not retain exactly active game content or renumber.");
        Assert(!vm.ClearOrderCommand.CanExecute(null) && vm.LastAction.Contains("清空 3 个模组"), "Clear no-op or action description failed.");
        var action = vm.LastAction;
        vm.ClearOrderCommand.Execute(null);
        Assert(vm.LastAction == action, "No-op clear changed last action.");
        vm.UndoCommand.Execute(null);
        Assert(Order(vm).SequenceEqual(original) && !vm.HasChanges && vm.LastAction.Contains("撤销清空")
            && !vm.UndoCommand.CanExecute(null), "Clear is not one undo step.");
        Assert(vm.CreateOrderFile().SourceFormat == OrderFileFormat.RimSortJson
            && JsonNode.Parse(codec.Write(vm.CreateOrderFile(), OrderFileFormat.RimSortJson))!["future"]!["note"]!.GetValue<string>() == "中文字段保留",
            "Clear undo lost source fields.");
        vm.RedoCommand.Execute(null);
        Assert(vm.Rows.Count == 3 && vm.LastAction.Contains("取消撤销") && vm.LastAction.Contains("清空 3"), "Cancel undo clear failed.");
        Assert(vm.AvailableMods.Any(row => row.PackageId == "example.buildings"), "Cleared installed mod not returned to inactive list.");

        var modern = vm.CreateExportDocument();
        vm.Load(modern.WithOrder(new LoadOrder(["missing.mod"])), "无本体.xml");
        vm.ClearOrderCommand.Execute(null);
        Assert(vm.Rows.Count == 0 && !vm.ClearOrderCommand.CanExecute(null), "Clear invented/enabled game content.");
        vm.UndoCommand.Execute(null);
        Assert(vm.Rows.Count == 1, "Undo empty result failed.");
        const string legacy = "<ModsConfigData><version>0.18.1722</version><activeMods><li>Core</li><li>旧版模组</li></activeMods></ModsConfigData>";
        var old = new MainViewModel();
        old.LoadFile(codec.Read(new MemoryStream(Encoding.UTF8.GetBytes(legacy))), "旧版.xml");
        old.ClearOrderCommand.Execute(null);
        Assert(Order(old).SequenceEqual(new[] { "Core" }), "Legacy Core removed.");
        old.UndoCommand.Execute(null);
        Assert(old.Rows.Count == 2, "Legacy clear undo failed.");
    }

    private static void CheckPositions(MainViewModel vm, ModsConfigDocument source)
    {
        var ids = new[] { "ludeon.rimworld", "example.buildings" }.Concat(Enumerable.Range(3, 98).Select(i => $"test.position{i}"));
        vm.Load(source.WithOrder(new LoadOrder(ids)), "100个模组.xml");
        Assert(vm.Rows.First().Index == 1 && vm.Rows.Last().Index == 100, "Row numbering is not one-based.");
        var last = vm.Rows[99];
        vm.SelectedRow = last;
        vm.MoveUpCommand.Execute(null);
        Assert(vm.LastAction.Contains($"{last.Name} 位置修改到 99"), "Last row move reported wrong coordinate.");
        vm.UndoCommand.Execute(null);
        Assert(vm.LastAction.Contains($"{last.Name} 位置修改到 100"), "Undo reported wrong coordinate.");
        vm.RedoCommand.Execute(null);
        Assert(vm.LastAction.Contains("取消撤销") && vm.LastAction.Contains("位置修改到 99"), "Cancel undo reported wrong coordinate.");
        vm.SelectedRow = vm.Rows[1];
        var name = vm.SelectedRow.Name;
        vm.MoveUpCommand.Execute(null);
        Assert(vm.LastAction.Contains($"{name} 位置修改到 1"), "Move to first position reported zero.");
        vm.UndoCommand.Execute(null);
        Assert(vm.LastAction.Contains($"{name} 位置修改到 2"), "Undo to second position failed.");
        vm.SelectedRow = vm.Rows[1];
        vm.RemoveFromOrderCommand.Execute(null);
        Assert(vm.LastAction.Contains(name) && vm.LastAction.Contains("原位置 2"), "Remove description lost mod/position.");
        vm.UndoCommand.Execute(null);
        Assert(vm.LastAction.Contains(name) && vm.LastAction.Contains("恢复到位置 2"), "Undo remove description failed.");
        vm.SelectedRow = vm.Rows[99];
        vm.MoveUpCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert(vm.LastAction.Contains("位置修改到 100"), "Final screenshot should show position 100.");
    }

    private static string[] Order(MainViewModel vm) => vm.Rows.Select(row => row.PackageId).ToArray();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
#endif
