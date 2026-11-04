using System.IO;
using System.Windows;
using Microsoft.Win32;
using RimWise.Core;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private readonly OrderFileService orderFiles = new();
    private readonly OrderBackupStore orderBackups = new(Path.Combine(UserStorage.DirectoryPath, "Data", "Backups"));
    private readonly NamedCollectionStore namedCollections = new(Path.Combine(UserStorage.DirectoryPath, "Data", "Collections"));

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        string? file = null;
        try
        {
            var locations = new OrderDialogLocations(UserStorage.DirectoryPath);
            var config = Path.GetDirectoryName(CurrentOrderLocation.Resolve(paths))!;
            file = OrderImportDialog.Show(this, kind => locations.Resolve(kind, config));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Keep manual import available on systems where the shell dialog cannot initialize.
            var fallback = new OpenFileDialog { Title = "导入排序或存档", CheckFileExists = true,
                Filter = "排序或存档 (*.xml;*.json;*.rml;*.rws)|*.xml;*.json;*.rml;*.rws|所有文件 (*.*)|*.*" };
            if (fallback.ShowDialog(this) == true) file = fallback.FileName;
        }
        if (file is not null) await ImportOrderAsync(file);
    }

    private static void RememberOrderDirectory(string file, bool exported)
    {
        try { new OrderDialogLocations(UserStorage.DirectoryPath).Remember(file, exported); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { /* A navigation preference must not turn a completed import/export into an error. */ }
    }

    private void Collections_Click(object sender, RoutedEventArgs e)
    {
        RunFileAction(() =>
        {
            var current = ViewModel.HasDocument ? ViewModel.CreateOrderFile() : null;
            var window = new CollectionWindow(namedCollections, current, ViewModel.SourceFileName) { Owner = this };
            if (window.ShowDialog() == true && window.LoadedDocument is not null)
            {
                // A named collection is only an order snapshot; keep the current source profile
                // so the backend retention protection is not silently discarded on load.
                var label = ViewModel.HasDocument ? ViewModel.SourceLabel : "合集：" + window.LoadedName;
                PreviewAndLoad(window.LoadedDocument, label, "切换合集");
            }
        });
    }

    internal bool PreviewAndLoad(OrderFileDocument loaded, string path, string title)
    {
        var comparison = new OrderComparison(ViewModel.HasDocument ? ViewModel.CreateExportDocument().Order : new LoadOrder([]), loaded.Configuration.Order);
        var notice = $"{OrderFileCodec.DisplayName(loaded.SourceFormat)} · 游戏版本：{(loaded.Configuration.GameVersion.Length > 0 ? loaded.Configuration.GameVersion : "未注明")}";
        notice += "\n载入后替换当前编辑清单，不写入游戏配置。";
        if (ViewModel.HasChanges) notice += "当前未保存的调整会被替换，载入后可撤销。";
        var preview = new OrderPreviewWindow(comparison, title, notice, "载入清单") { Owner = this };
        if (preview.ShowDialog() != true) return false;
        ViewModel.ReplaceFileUndoable(loaded, path, title);
        ViewModel.SelectedRow = ViewModel.Rows.FirstOrDefault();
        return true;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasDocument) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出排序为新文件",
            Filter = OrderExportTargets.DialogFilter,
            FileName = "ModsConfig-export", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        RunFileAction(() =>
        {
            var choice = OrderExportTargets.All[dialog.FilterIndex - 1];
            var target = choice.Format;
            var document = ViewModel.CreateOrderFile();
            var preview = CreateExportPreview(document, target, choice.Name);
            if (preview.ShowDialog() != true) return;
            orderFiles.ExportNew(dialog.FileName, document, target);
            RememberOrderDirectory(dialog.FileName, exported: true);
            ViewModel.MarkExported($"导出排序：{Path.GetFileName(dialog.FileName)}");
            ViewModel.Status = $"已导出：{Path.GetFileName(dialog.FileName)}";
        });
    }

    internal OrderPreviewWindow CreateExportPreview(OrderFileDocument document, OrderFileFormat target, string? targetName = null)
    {
        var warnings = new OrderFileCodec().ConversionWarnings(document, target);
        var comparison = new OrderComparison(document.Configuration.Order, document.Configuration.Order);
        var preview = new OrderPreviewWindow(comparison, "导出预览",
            $"目标：{targetName ?? OrderFileCodec.DisplayName(target)}\n" + string.Join("\n", warnings), "导出新文件") { Owner = this };
        preview.Summary.Text = $"{document.Configuration.Order.PackageIds.Count} 个模组 · 保留原始标识与顺序";
        return preview;
    }

    private void BackupOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasDocument) return;
        RunFileAction(() =>
        {
            var backup = orderBackups.Save(ViewModel.CreateOrderFile(), ViewModel.SourceFileName);
            ViewModel.Status = "已备份当前排序：" + Path.GetFileName(backup);
        });
    }

    private void RestoreOrder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "恢复排序备份", Filter = "排序备份 (*.rimbackup)|*.rimbackup", CheckFileExists = true, Multiselect = false,
            InitialDirectory = Directory.Exists(orderBackups.DirectoryPath) ? orderBackups.DirectoryPath : UserStorage.DirectoryPath
        };
        if (dialog.ShowDialog(this) != true) return;
        RunFileAction(() => PreviewAndLoad(orderBackups.Read(dialog.FileName), dialog.FileName, "恢复排序备份"));
    }
}
