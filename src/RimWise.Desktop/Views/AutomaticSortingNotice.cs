using System.IO;
using System.Text.Json;
using System.Windows;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Views;

internal static class AutomaticSortingNotice
{
    internal const string Text = "当前功能仍在测试，虽已在作者电脑测试成功，但可能出现包括但不限于：排序失败，生吞大白饭，排了等于没排等情况。";

    internal static void ShowIfNeeded(Window owner, string dataDirectory)
    {
        var store = new AutomaticSortingPreferencesStore(dataDirectory);
        try { if (store.Load().NoticeAcknowledged) return; }
        catch (Exception ex) when (IsStorageError(ex))
        { DiagnosticEventLog.Current?.Record("automatic-sorting", "notice-state-unavailable", ex.Message); }

        new MessageDialogWindow(owner, Text, "自动排序测试提醒", MessageBoxButton.OK,
            MessageBoxImage.Warning, MessageBoxResult.OK, TimeSpan.FromSeconds(3)).ShowDialog();
        try { store.AcknowledgeNotice(); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            DiagnosticEventLog.Current?.Record("automatic-sorting", "notice-save-failed", ex.Message);
            MessageDialog.Show(owner, "提醒确认记录未能保存，下次打开时会再次显示。", "未能保存设置");
        }
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException;
}
