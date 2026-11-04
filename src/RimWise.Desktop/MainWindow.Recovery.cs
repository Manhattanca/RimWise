using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Safety;
using RimWise.Integration.AI;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private readonly EditorRecoveryStore editorRecovery = new(UserStorage.DirectoryPath);
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool recoveryEnabled;

    private void InitializeRecovery()
    {
        recoveryEnabled = true;
        ViewModel.EditorChanged += QueueRecoverySnapshot;
        recoveryTimer.Tick += (_, _) => { recoveryTimer.Stop(); SaveEditorSnapshot(); };
    }

    private void QueueRecoverySnapshot() { if (recoveryEnabled) { recoveryTimer.Stop(); recoveryTimer.Start(); } }
    private bool SaveEditorSnapshot()
    {
        try
        {
            if (ViewModel.HasDocument && ViewModel.HasChanges) editorRecovery.Save(ViewModel.CreateOrderFile(), ViewModel.SourceLabel);
            else editorRecovery.ClearCurrent();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { ViewModel.Status = "恢复快照保存失败：" + ex.Message; return false; }
    }

    private void OfferEditorRecovery()
    {
        try
        {
            foreach (var item in editorRecovery.ReadAbandoned())
            {
                var choice = MessageDialog.Show(this, $"发现未保存的排序（{item.TimeUtc.ToLocalTime():g}，{item.Document.Configuration.Order.PackageIds.Count} 项）。\n是否恢复到编辑清单？游戏文件不会被改动。\n\n是：恢复；否：丢弃这份快照；取消：留待下次处理。",
                    "恢复未保存排序", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) break;
                if (choice == MessageBoxResult.Yes)
                {
                    ViewModel.ReplaceFileUndoable(item.Document, item.Source, "恢复未保存排序");
                    ViewModel.MarkRecoveredDirty();
                    if (!SaveEditorSnapshot()) break;
                }
                editorRecovery.Remove(item.Id);
                if (choice == MessageBoxResult.Yes) break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { ViewModel.Status = "恢复快照无法读取，原文件已保留：" + ex.Message; }
    }

    private void InterruptedFiles_Click(object sender, RoutedEventArgs e) => ShowInterruptedFiles();

    private void TaskRecovery_Click(object sender, RoutedEventArgs e) => OpenAiSorting().OpenTaskRecovery();

    private async Task<IReadOnlyList<string>> AskInterruptedTasksAsync()
    {
        try
        {
            return await AiStartupRecovery.AskAsync(new(UserStorage.DirectoryPath), async task =>
                await Dispatcher.InvokeAsync(() =>
                {
                    if (lifetime.IsCancellationRequested) return true;
                    var detail = task.Checkpoint is { } s ? $"（{s.StartedUtc.LocalDateTime:g}）" : "（记录损坏）";
                    return MessageDialog.Show(this, $"发现上次中断的 AI 任务{detail}。是否继续？\n\n是：恢复已保存进度，重新检查后继续。\n否：删除任务并停止后续 AI 请求。\n\n未完成的远程请求不会自动重发；已发生的费用记录单独保留。",
                        "继续之前的任务", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No) == MessageBoxResult.Yes;
                }), lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { ViewModel.Status = "任务恢复检查未完成，原记录已保留：" + ex.Message; }
        return [];
    }

    private void ContinueInterruptedTasks(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        try
        {
            if (ids.Count == 1)
            {
                using var lease = new AiTaskRecoveryStore(UserStorage.DirectoryPath).Open(ids[0]);
                var saved = lease.Current;
                if (saved is { Kind: AiTaskKind.Maintenance, Finished: false, HasUnresolvedRequest: false })
                {
                    lease.Dispose();
                    OpenAiSorting().RestoreTask(ids[0]);
                    return;
                }
                if (saved is { Kind: AiTaskKind.Sorting, Finished: false }
                    && (saved.Phase is AiTaskPhase.Applying or AiTaskPhase.Applied
                        || saved.Phase == AiTaskPhase.ReplySaved && saved.MayApply))
                {
                    lease.Dispose();
                    OpenAiSorting().RestoreTask(ids[0]);
                    return;
                }
            }
            OpenAiSorting().OpenTaskRecovery();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { ViewModel.Status = "任务已保留，恢复前检查未通过：" + ex.Message; }
    }

    private void RemovedMods_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new ModRestoreWindow(new LocalModArchive(UserStorage.DirectoryPath, paths.LocalModsDirectory),
                EnsureGameClosed, () => _ = RestartScanAsync()) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { ViewModel.Status = "模组备份无法读取：" + ex.Message; }
    }

    private void ShowInterruptedFiles(bool startup = false)
    {
        try
        {
            var store = CreateGameOrderStore();
            var items = store.InspectInterrupted();
            if (items.Count == 0)
            {
                if (!startup) ViewModel.Status = "没有待处理的中断写入";
                return;
            }
            var window = new FileRecoveryWindow(store, () => EnsureGameClosed(), plan =>
            {
                ValidateTargetGameVersion(plan.After);
                PreviewAndRestoreGamePlan(plan, store.TargetPath);
            }) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { ViewModel.Status = "中断恢复检查未完成：" + ex.Message; }
    }
}
