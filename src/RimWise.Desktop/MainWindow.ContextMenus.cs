using RimWise.Desktop.Views;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Integration.Safety;
using RimWise.Integration.Steam;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private LoadOrderRow? contextOrderRow;
    private InstalledModRow? contextAvailableRow;
    private bool contextBatchRunning;
    private MenuItem addContextItem = null!;
    private MenuItem removeContextItem = null!;
    private MenuItem deleteContextItem = null!;
    private MenuItem unsubscribeContextItem = null!;

    private void InitializeContextMenus()
    {
        AvailableGrid.ContextMenu = CreateContextMenu();
        OrderGrid.ContextMenu = CreateContextMenu();
        AvailableGrid.ContextMenuOpening += ContextMenu_Opening;
        OrderGrid.ContextMenuOpening += ContextMenu_Opening;
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        var add = new MenuItem { Header = "加入到排序中…" };
        var remove = new MenuItem { Header = "从排序中移除" };
        var delete = new MenuItem { Header = "删除本地模组文件…" };
        var unsubscribe = new MenuItem { Header = "从创意工坊取消订阅" };
        var mark = new MenuItem { Header = "标记" };
        var download = new MenuItem { Header = "通过 Steam 订阅下载…", Tag = "download-missing" };
        download.Click += ContextDownload_Click;
        foreach (var color in MarkerPalette.Colors)
        {
            var item = new MenuItem { Header = color.Name, Icon = new System.Windows.Shapes.Rectangle
                { Width = 16, Height = 16, RadiusX = 3, RadiusY = 3, Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color.Hex)) } };
            item.Click += (_, _) => ViewModel.ApplyColor(color.Hex, ViewModel.SelectedOrderRows, ViewModel.SelectedAvailableRows);
            mark.Items.Add(item);
        }
        var clearColor = new MenuItem { Header = "清除颜色" };
        clearColor.Click += (_, _) => ViewModel.ApplyColor(null, ViewModel.SelectedOrderRows, ViewModel.SelectedAvailableRows);
        mark.Items.Add(new Separator()); mark.Items.Add(clearColor);
        add.Click += ContextAdd_Click;
        remove.Click += ContextRemove_Click;
        delete.Click += ContextDelete_Click;
        unsubscribe.Click += ContextUnsubscribe_Click;
        menu.Items.Add(add);
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.Items.Add(unsubscribe);
        menu.Items.Add(new Separator());
        menu.Items.Add(mark);
        menu.Items.Add(download);
        addContextItem ??= add;
        removeContextItem ??= remove;
        deleteContextItem ??= delete;
        unsubscribeContextItem ??= unsubscribe;
        return menu;
    }

    private void ContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        contextOrderRow = null;
        contextAvailableRow = null;
        var grid = (DataGrid)sender;
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row)
        {
            e.Handled = true;
            return;
        }

        if (row.Item is LoadOrderRow order)
        {
            contextOrderRow = order;
            if (!ViewModel.SelectedOrderRows.Contains(order)) ViewModel.SelectOrderRows([order], order);
        }
        else if (row.Item is InstalledModRow available)
        {
            contextAvailableRow = available;
            if (!ViewModel.SelectedAvailableRows.Contains(available)) ViewModel.SelectAvailableRows([available], available);
        }

        var items = grid.ContextMenu?.Items.OfType<MenuItem>().ToArray() ?? [];
        if (items.Length < 5) { e.Handled = true; return; }
        items[0].IsEnabled = contextAvailableRow is not null && ViewModel.HasDocument;
        items[1].IsEnabled = contextOrderRow is not null && ViewModel.RemoveFromOrderCommand.CanExecute(null);
        items[2].IsEnabled = !contextBatchRunning && ContextInstallations(ModSource.Local).Length > 0;
        items[3].IsEnabled = !contextBatchRunning && ContextInstallations(ModSource.Workshop).Length > 0;
        items[4].IsEnabled = ViewModel.SelectedOrderRows.Count > 0 || ViewModel.SelectedAvailableRows.Count > 0;
        var download = items.Single(item => item.Tag as string == "download-missing");
        download.Visibility = contextOrderRow is not null && contextOrderRow.Match?.Candidates.Count is null or 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ContextDownload_Click(object sender, RoutedEventArgs e)
    {
        if (contextOrderRow is null) return;
        var missing = ViewModel.SelectedOrderRows.Where(row => row.Match?.Candidates.Count is null or 0)
            .Select(row => row.PackageId).ToArray();
        await DownloadMissingModsAsync(missing);
    }

    private void ContextAdd_Click(object sender, RoutedEventArgs e)
    {
        if (contextAvailableRow is not { } row || !ViewModel.HasDocument) return;
        if (!ViewModel.SelectedAvailableRows.Contains(row)) return;
        var selected = ViewModel.SelectedAvailableRows.ToArray();
        ViewModel.AddAvailableRows(selected, ViewModel.Rows.Count);
    }

    private void ContextRemove_Click(object sender, RoutedEventArgs e)
    {
        if (contextOrderRow is not { } row || !ViewModel.SelectedOrderRows.Contains(row)) return;
        if (ViewModel.RemoveFromOrderCommand.CanExecute(null)) ViewModel.RemoveFromOrderCommand.Execute(null);
    }

    private void ContextMark_Click(object sender, RoutedEventArgs e) => OpenBatchMarkWindow();

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (contextBatchRunning) return;
        var targets = ContextInstallations(ModSource.Local);
        if (targets.Length == 0) return;
        if (MessageDialog.Show(this, $"将把以下 {targets.Length} 个本地模组移入软件备份目录：\n{DescribeTargets(targets)}\n\n仅处理所选本地模组，其他来源不受影响。是否继续？",
                "删除本地模组文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var completed = new List<ModInstallation>();
        var failures = new List<string>();
        try
        {
            EnsureGameClosed();
            // Validate every target before the first write; retained rows cannot disappear in a batch.
            foreach (var mod in targets)
            {
                ValidateContextRemoval(mod);
                if (!IsDirectChildOf(mod.Directory, paths.LocalModsDirectory))
                    throw new IOException("模组目录不在设置的本地 Mods 目录中：" + mod.Name);
                RejectReparsePoint(mod.Directory);
            }
            var archive = new LocalModArchive(UserStorage.DirectoryPath, paths.LocalModsDirectory);
            foreach (var mod in targets)
            {
                try { EnsureGameClosed(); ValidateContextRemoval(mod); archive.Archive(mod.Directory); completed.Add(mod); }
                catch (Exception ex) when (IsContextOperationError(ex)) { failures.Add(mod.Name + "：" + ex.Message); }
            }
            var active = ViewModel.Rows.Where(row => completed.Any(mod => SameInstallation(row.Match?.UniqueInstallation, mod))).ToArray();
            if (active.Length > 0)
            {
                ViewModel.SelectOrderRows(active);
                if (ViewModel.RemoveFromOrderCommand.CanExecute(null)) ViewModel.RemoveFromOrderCommand.Execute(null);
            }
            ViewModel.Status = $"已将 {completed.Count} 个本地模组移入备份，可从文件菜单恢复";
            ShowContextFailures(failures);
        }
        catch (Exception ex) when (IsContextOperationError(ex))
        { ViewModel.Status = "删除未完成：" + ex.Message; }
        finally { if (completed.Count > 0) _ = RestartScanAsync(); }
    }

    private async void ContextUnsubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (contextBatchRunning) return;
        var targets = ContextInstallations(ModSource.Workshop);
        if (targets.Length == 0) return;
        if (MessageDialog.Show(this, $"将取消订阅以下 {targets.Length} 个模组：\n{DescribeTargets(targets)}\n\n仅处理所选创意工坊模组，Steam 将管理下载文件的移除。是否继续？",
                "取消创意工坊订阅", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var completed = 0;
        var failures = new List<string>();
        contextBatchRunning = true;
        try
        {
            EnsureGameClosed();
            foreach (var mod in targets)
            {
                ValidateContextRemoval(mod);
                if (ViewModel.Rows.Any(row => SameInstallation(row.Match?.UniqueInstallation, mod)))
                    throw new InvalidOperationException("请先从当前排序移除：" + mod.Name);
            }
            if (sender is MenuItem item) item.IsEnabled = false;
            var client = new SteamWorkshopClient(AppContext.BaseDirectory);
            foreach (var mod in targets)
            {
                ViewModel.Status = $"正在取消订阅 {completed + failures.Count + 1}/{targets.Length}：{mod.Name}";
                try
                {
                    EnsureGameClosed(); ValidateContextRemoval(mod);
                    if (ViewModel.Rows.Any(row => SameInstallation(row.Match?.UniqueInstallation, mod)))
                        throw new InvalidOperationException("等待期间已加入排序，请先移出。");
                    await client.UnsubscribeAsync(mod.WorkshopId, CancellationToken.None); completed++;
                }
                catch (Exception ex) when (IsContextOperationError(ex)) { failures.Add(mod.Name + "：" + ex.Message); }
            }
            ViewModel.Status = $"Steam 已确认取消 {completed}/{targets.Length} 个订阅";
            ShowContextFailures(failures);
        }
        catch (Exception ex) when (IsContextOperationError(ex))
        { ViewModel.Status = "取消订阅未确认：" + ex.Message; }
        finally { contextBatchRunning = false; if (completed > 0) _ = RestartScanAsync(); }
    }

    private ModInstallation[] ContextInstallations(ModSource source)
    {
        var selected = contextAvailableRow is not null
            ? ViewModel.SelectedAvailableRows.Where(ViewModel.AvailableMods.Contains).Select(row => row.Installation)
            : contextOrderRow is not null ? ViewModel.SelectedOrderRows.Where(ViewModel.Rows.Contains)
                .Select(row => row.Match?.UniqueInstallation).OfType<ModInstallation>() : [];
        return selected.Where(mod => mod.Source == source).DistinctBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool SameInstallation(ModInstallation? left, ModInstallation right) =>
        left?.Directory.Equals(right.Directory, StringComparison.OrdinalIgnoreCase) == true;

    private void ValidateContextRemoval(ModInstallation mod)
    {
        if (ViewModel.IsRetained(mod.PackageId)
            || ViewModel.Rows.Any(row => SameInstallation(row.Match?.UniqueInstallation, mod) && row.IsRetained)
            || ViewModel.AvailableMods.Any(row => SameInstallation(row.Installation, mod) && row.IsRetained))
            throw new IOException("已标记为必须保留，不能移除：" + mod.Name);
        if (mod.Source == ModSource.Workshop && (!ulong.TryParse(mod.WorkshopId, out var id) || id == 0))
            throw new IOException("创意工坊编号无效：" + mod.Name);
    }

    private static string DescribeTargets(IEnumerable<ModInstallation> targets) =>
        string.Join("\n", targets.Select(mod => mod.Name + " · " + (mod.Source == ModSource.Local ? mod.Directory : mod.WorkshopId)));

    private static bool IsContextOperationError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or Win32Exception or InvalidOperationException or ArgumentException or System.Text.Json.JsonException;

    private void ShowContextFailures(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0) return;
        ViewModel.Status += $"；{failures.Count} 项未完成";
        MessageDialog.Show(this, string.Join("\n", failures), "以下操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool IsDirectChildOf(string directory, string root)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(root)) return false;
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parent is not null && string.Equals(parent, Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("模组目录是链接或联接，已拒绝删除。");
    }
}
