using System.Windows;

namespace RimWise.Desktop.Views;

internal static class MarketplaceFirstUseNotice
{
    internal const string Text = "此功能为测试版，可能会出现问题。\n\n发布排序时请尽量绑定 Steam 创意工坊合集。\n\n排序标题和简介请勿包含政治相关内容，上传图片请勿包含色情或裸露内容。";

    internal static bool Show(Window owner)
    {
        var dialog = new MessageDialogWindow(owner, Text, "警告", MessageBoxButton.OK,
            MessageBoxImage.Warning, MessageBoxResult.OK, TimeSpan.FromSeconds(5),
            confirmationText: "已知晓", allowDismissAfterDelay: true);
        dialog.ShowDialog();
        return dialog.Result == MessageBoxResult.OK;
    }
}
