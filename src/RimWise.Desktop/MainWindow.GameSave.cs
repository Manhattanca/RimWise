using System.IO;
using System.Windows;
using Microsoft.Win32;
using RimWise.Core;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private GameOrderStore CreateGameOrderStore() => new(Path.GetDirectoryName(CurrentOrderLocation.Resolve(paths))!,
        Path.Combine(UserStorage.DirectoryPath, "Data", "GameBackups"));

    internal Action PrepareAutomaticOrderSave()
    {
        EnsureGameClosed();
        var originalPaths = paths;
        var store = CreateGameOrderStore();
        var runtimeVersion = ResolveTargetGameVersion(ViewModel.CreateExportDocument());
        var baseline = store.Prepare(ViewModel.CreateExportDocument(), runtimeVersion);
        return () =>
        {
            if (paths != originalPaths) throw new IOException("游戏路径在测试期间已变化，未保存排序。");
            EnsureGameClosed();
            if (ResolveTargetGameVersion(ViewModel.CreateExportDocument()) != runtimeVersion)
                throw new IOException("游戏版本在测试期间发生变化，请重新测试后保存。");
            var plan = baseline.WithOrder(ViewModel.CreateExportDocument().Order);
            ValidateTargetGameVersion(plan.After);
            ApplyGamePlan(plan, store.TargetPath);
        };
    }

    private static void EnsureGameClosed()
    {
        if (RimWise.Integration.Game.GameProcessService.IsAnyRunning())
            throw new IOException("请先关闭环世界，再保存或回滚游戏排序。");
    }

    private async void SaveOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasDocument) return;
        if (pathInitialization is not null) await pathInitialization;
        RunFileAction(SaveCurrentOrder);
    }

    private void SaveCurrentOrder()
    {
        if (ViewModel.IsShowingCache) throw new InvalidDataException("当前资料来自缓存，请刷新成功后再保存排序。");
        EnsureGameClosed();
        var store = CreateGameOrderStore();
        var plan = store.Prepare(ViewModel.CreateExportDocument(), ResolveTargetGameVersion(ViewModel.CreateExportDocument()));
        ValidateTargetGameVersion(plan.After);
        ApplyGamePlan(plan, store.TargetPath);
    }

    private void ValidateTargetGameVersion(ModsConfigDocument configuration)
        => ResolveTargetGameVersion(configuration);

    private GameVersion ResolveTargetGameVersion(ModsConfigDocument configuration)
    {
        var configVersion = GameVersion.Parse(configuration.GameVersion);
        var installed = GameVersionDetector.Detect(paths.GameDirectory).Version;
        var selected = GameVersion.Parse(paths.TargetVersionOverride);
        var effective = installed ?? selected ?? configVersion
            ?? throw new InvalidDataException("游戏及配置版本均未识别，请在路径设置中指定目标版本。");
        GameVersionCompatibility.EnsureCompatibleIdentifiers(configVersion, effective);
        return effective;
    }

    internal void PreviewAndRestoreGamePlan(GameOrderPlan plan, string target)
    {
        var comparison = new OrderComparison(plan.Before.Order, plan.After.Order);
        var notice = $"游戏配置：{target}\n确认后更新游戏启用清单，并备份原配置。";
        if (ViewModel.ErrorCount > 0 || ViewModel.WarningCount > 0)
            notice += $"\n当前有 {ViewModel.ErrorCount} 个错误、{ViewModel.WarningCount} 个警告；保存不代表问题已解决。";
        var preview = new OrderPreviewWindow(comparison, "恢复游戏配置", notice, "恢复到游戏") { Owner = this };
        if (preview.ShowDialog() != true) return;
        ApplyGamePlan(plan, target, restoreEditor: true);
    }

    internal void ApplyGamePlan(GameOrderPlan plan, string target, bool restoreEditor = false)
    {
        // Saving is a direct action; restore alone asks for a comparison preview.
        // The store still checks the expected bytes, makes a backup and commits atomically.
        EnsureGameClosed();
        var commit = plan.Apply();
        if (commit.Changed)
            ViewModel.RecordGameSave(() => { EnsureGameClosed(); commit.Undo(); }, () => { EnsureGameClosed(); commit.Redo(); },
                restoreEditor ? OrderFileDocument.FromConfiguration(plan.After) : null, target);
        else
        {
            if (restoreEditor) ViewModel.ReplaceFileUndoable(OrderFileDocument.FromConfiguration(plan.After), target);
            ViewModel.MarkExported(restoreEditor ? "恢复游戏配置：与当前文件一致" : "保存排序：游戏清单与当前排序一致");
        }
        ViewModel.Status = commit.Changed ? "已保存到游戏，原配置已备份；可撤销本次保存" : "游戏排序与当前清单一致";
    }

    private async void RestoreGameConfig_Click(object sender, RoutedEventArgs e)
    {
        if (pathInitialization is not null) await pathInitialization;
        RunFileAction(() =>
        {
            EnsureGameClosed();
            var store = CreateGameOrderStore();
            var dialog = new OpenFileDialog
            {
                Title = "恢复游戏配置", Filter = "游戏配置备份 (*-before.xml)|*-before.xml", CheckFileExists = true,
                InitialDirectory = Directory.Exists(store.BackupDirectory) ? store.BackupDirectory : UserStorage.DirectoryPath
            };
            if (dialog.ShowDialog(this) != true) return;
            var plan = store.PrepareRestore(dialog.FileName);
            ValidateTargetGameVersion(plan.After);
            PreviewAndRestoreGamePlan(plan, store.TargetPath);
        });
    }
}
