using System.IO;
using System.Windows;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop.Views;

public partial class DuplicateCopyBackupsWindow : Window
{
    private readonly DuplicateCopyBackups store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<OrderMatch>>> refresh;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    internal DuplicateCopyBackupsWindow(DuplicateCopyBackups store, Func<CancellationToken, Task<IReadOnlyList<OrderMatch>>> refresh)
    {
        InitializeComponent(); this.store = store; this.refresh = refresh;
        Reload();
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Closed += (_, _) => lifetime.Cancel();
    }
    private void Reload()
    {
        BackupsList.ItemsSource = store.List();
        BackupsList.SelectedIndex = BackupsList.Items.Count > 0 ? 0 : -1;
        StatusText.Text = BackupsList.Items.Count == 0 ? "没有待恢复的副本备份。" : "选择一条备份后恢复。";
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RestoreAsync();

    internal async Task RestoreAsync()
    {
        if (busy || BackupsList.SelectedItem is not DuplicateCopyBackup record) return;
        if (MessageDialog.Show(this, "将恢复以下目录：\n\n" + string.Join("\n", record.Moves.Select(m => m.Original))
            + "\n\n恢复后可能重新出现重复安装。是否继续？", "恢复副本", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        busy = true; RestoreButton.IsEnabled = false;
        try
        {
            await Task.Run(() => store.Restore(record.Id, lifetime.Token), lifetime.Token);
            await refresh(lifetime.Token);
            Reload(); StatusText.Text = "已恢复副本并刷新安装信息，当前排序保留。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { StatusText.Text = "恢复未完成：" + ex.Message; }
        finally { busy = false; RestoreButton.IsEnabled = true; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (!busy) Close(); }
}
