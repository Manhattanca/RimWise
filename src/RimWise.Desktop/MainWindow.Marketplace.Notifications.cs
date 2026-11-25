using System.Windows;
using RimWise.Desktop.Views;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private bool marketplaceNoticeDismissed;
    private bool marketplaceNoticeBusy;

    internal void ShowMarketplaceDownloadNotification(MarketplaceDownloadNotice notice)
    {
        Dispatcher.VerifyAccess();
        // A new operation and its final result are visible; progress does not reopen
        // a notification dismissed while that same operation is running.
        if (!marketplaceNoticeBusy || !notice.Busy) marketplaceNoticeDismissed = false;
        marketplaceNoticeBusy = notice.Busy;
        MarketplaceNoticeTitle.Text = notice.Title;
        MarketplaceNoticeText.Text = notice.Message;
        MarketplaceNoticeProgress.IsIndeterminate = notice.Busy && notice.Percent is null;
        MarketplaceNoticeProgress.Value = notice.Percent is { } value ? Math.Clamp(value, 0, 100) : 0;
        MarketplaceNoticeProgress.Visibility = notice.Busy || notice.Percent is not null ? Visibility.Visible : Visibility.Collapsed;
        MarketplaceNoticeCancelButton.Visibility = notice.Busy ? Visibility.Visible : Visibility.Collapsed;
        if (marketplaceNoticeDismissed) return;
        var reveal = MarketplaceNotice.Visibility != Visibility.Visible;
        MarketplaceNotice.Visibility = Visibility.Visible;
        if (reveal) Services.InterfaceMotion.RevealNotification(MarketplaceNotice);
    }

    private void DismissMarketplaceNotice_Click(object sender, RoutedEventArgs e)
    {
        marketplaceNoticeDismissed = true;
        MarketplaceNotice.Visibility = Visibility.Collapsed;
    }

    private void CancelMarketplaceDownload_Click(object sender, RoutedEventArgs e) => marketplacePage?.Cancel();
}
