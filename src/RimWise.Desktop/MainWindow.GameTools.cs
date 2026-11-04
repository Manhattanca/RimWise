using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RimWise.Core;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;
using RimWise.Integration.Game;
using RimWise.Integration.Safety;
using RimWise.Formats;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private readonly GameProcessService gameProcesses = new();
    private readonly ExecutionJournal executionJournal = new(UserStorage.DirectoryPath);
    private readonly CustomRuleStore ruleStore = new(UserStorage.DirectoryPath);
    private readonly DispatcherTimer gameStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private void InitializeGameTools()
    {
        gameStatusTimer.Tick += (_, _) => GameStatusItem.Header = gameProcesses.Status;
        gameStatusTimer.Start();
    }

    private async Task LoadCustomRulesAsync()
    {
        try
        {
            var rules = await Task.Run(ruleStore.Load, lifetime.Token);
            if (!lifetime.IsCancellationRequested) ViewModel.SetCustomRules(rules);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { ViewModel.Status = "自定义规则未载入：" + ex.Message; }
    }

    private async void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        if (pathInitialization is not null) await pathInitialization;
        try
        {
            gameProcesses.Launch(paths.GameDirectory);
            GameStatusItem.Header = gameProcesses.Status;
            ViewModel.Status = ViewModel.HasChanges ? "已请求启动游戏；当前未保存调整尚未写入游戏" : "已请求启动游戏";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception)
        { ViewModel.Status = "无法启动游戏：" + ex.Message; }
    }

    private void GameLog_Click(object sender, RoutedEventArgs e)
    {
        string? suggested = null;
        try
        {
            var config = Path.GetDirectoryName(CurrentOrderLocation.Resolve(paths));
            suggested = config is null ? null : Path.GetDirectoryName(config);
        }
        catch (ArgumentException) { }
        var dialog = new OpenFileDialog { Title = "选择游戏日志", Filter = "游戏日志 (*.log;*.txt)|*.log;*.txt|所有文件|*.*",
            InitialDirectory = suggested is not null && Directory.Exists(suggested) ? suggested : UserStorage.DirectoryPath };
        if (dialog.ShowDialog(this) == true) new GameLogWindow(dialog.FileName, ViewModel.CurrentCatalogue) { Owner = this }.Show();
    }

    private void CompareGameOrder_Click(object sender, RoutedEventArgs e) => RunFileAction(() =>
    {
        if (!ViewModel.HasDocument) { ViewModel.Status = "请先载入排序"; return; }
        var disk = files.Read(CurrentOrderLocation.Resolve(paths));
        var comparison = new OrderComparison(disk.Order, new LoadOrder(ViewModel.Rows.Select(row => row.PackageId)));
        new OrderPreviewWindow(comparison, "与游戏排序比较", "比较游戏文件与当前编辑清单，仅查看。", "关闭") { Owner = this }.ShowDialog();
    });

    private void OrderRules_Click(object sender, RoutedEventArgs e) => RunFileAction(() =>
    {
        var choices = (ViewModel.CurrentCatalogue?.Installations ?? []).Where(mod => mod.PackageId.Length > 0)
            .Select(mod => (mod.PackageId, mod.Name));
        var dialog = new OrderRulesWindow(ViewModel.CustomRules, choices) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ruleStore.Save(dialog.Result);
        ViewModel.SetCustomRules(dialog.Result);
        ViewModel.Status = "自定义规则已保存；当前顺序实时显示规则问题";
    });

    private void RecoverAi_Click(object sender, RoutedEventArgs e) => RunFileAction(() =>
    {
        var entries = executionJournal.ReadRecent();
        if (entries.Count == 0) { ViewModel.Status = "暂无 AI 调整记录"; return; }
        var picker = new Window { Owner = this, Title = "AI 调整记录", Width = 790, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        picker.SetResourceReference(StyleProperty, typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(20) };
        var list = new ListBox { ItemsSource = entries.Select(entry => new ListBoxItem { Tag = entry,
            Content = $"{entry.Record.StartedUtc.LocalDateTime:g} · {State(entry.State)} · {entry.Record.Before.Length} → {entry.Record.After.Length} 项" }).ToArray() };
        var restore = new Button { Content = "预览恢复原排序…", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        DockPanel.SetDock(restore, Dock.Bottom); panel.Children.Add(restore); panel.Children.Add(list); picker.Content = panel;
        restore.Click += (_, _) => RunFileAction(() =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: RimWise.Integration.Safety.RecoveryEntry entry } || !ViewModel.HasDocument) return;
            var preview = new OrderPreviewWindow(new OrderComparison(new LoadOrder(ViewModel.Rows.Select(r => r.PackageId)), new LoadOrder(entry.Record.Before)),
                "恢复 AI 调整前排序", "恢复到编辑清单；不会自动写入游戏配置。", "恢复到编辑清单") { Owner = picker };
            if (preview.ShowDialog() != true) return;
            var document = ViewModel.CreateExportDocument().WithOrder(new LoadOrder(entry.Record.Before));
            ViewModel.ReplaceFileUndoable(OrderFileDocument.FromConfiguration(document), "AI 调整记录");
            executionJournal.Dismiss(entry.Record.Id);
            picker.Close();
        });
        picker.ShowDialog();
        static string State(string state) => state switch { "applied" => "已应用", "reviewed" => "已恢复", _ => "中断，需核对" };
    });
}
