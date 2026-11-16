using System.Windows;

namespace RimWise.Desktop.Views;

internal static class MarketplaceFirstUseNotice
{
    internal const string Text = "此功能为测试版，可能会出现问题。\n请确保您可以连接 GitHub。\n发布排序时请尽量链接 Steam 合集。";

    internal static bool Show(Window owner)
    {
        var dialog = new MessageDialogWindow(owner, Text, "警告", MessageBoxButton.OK,
            MessageBoxImage.Warning, MessageBoxResult.OK, TimeSpan.FromSeconds(5),
            confirmationText: "已知晓", allowDismissAfterDelay: true);
        dialog.ShowDialog();
        return dialog.Result == MessageBoxResult.OK;
    }
}
