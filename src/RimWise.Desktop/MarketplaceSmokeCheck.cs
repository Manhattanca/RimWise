#if DEBUG
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Desktop.Presentation;
using RimWise.Integration.Configuration;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop;

// Synthetic UI/transport fixtures only: no GitHub identity, network or Steam subscription.
internal static class MarketplaceSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                var checks = new List<string>();
                File.WriteAllText(Path.Combine(output, "stage.txt"), "browser");
                await RunAsync(window, output, checks);
                File.WriteAllText(Path.Combine(output, "stage.txt"), "publish");
                await MarketplacePublishSmokeCheck.RunAsync(window, output, checks);
                File.WriteAllText(Path.Combine(output, "publish-result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }));
                File.WriteAllText(Path.Combine(output, "stage.txt"), "secondary-first-frame");
                await SecondaryWindowSmokeCheck.RunAsync(window, output, checks);
                File.WriteAllText(Path.Combine(output, "stage.txt"), "apply");
                await MarketplaceApplySmokeCheck.RunAsync(window, output, checks);
                File.WriteAllText(Path.Combine(output, "stage.txt"), "file-import");
                await ImportAndThumbnailSmokeCheck.RunAsync(window, output, checks);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }));
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task RunAsync(MainWindow window, string output, List<string> checks)
    {
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        window.LoadSample();
        var revision = window.ViewModel.ContentRevision;
        var original = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        var first = new MarketplaceManifest(1, "quiet-frontier", "1.0.0", "静谧边境 · 模拟样例", "ExampleAuthor",
            "围绕建造与探索组织的排序。仅用于本地界面验收，不代表已上线内容。", "1.7", [], "123456",
            [new("ludeon.rimworld", null), new("example.building", "123457"), new("example.exploration", "123458")]);
        var second = first with { Id = "new-colony", Title = "新殖民地 · 模拟样例", Description = "另一份供选择、搜索及快速切换验收的排序。" };
        var dlc = new[] { "royalty", "ideology", "biotech", "anomaly", "odyssey" }.Select(id => "ludeon.rimworld." + id).ToArray();
        first = first with { RequiredDlc = dlc, Mods = first.Mods.Concat(dlc.Select(id => new MarketplaceMod(id, null))).ToArray() };
        second = second with { RequiredDlc = dlc, Mods = first.Mods };
        var catalogue = MarketplaceJson.CreateCatalogue([first, second]);
        var byId = new Dictionary<string, MarketplaceManifest> { [first.Id] = first, [second.Id] = second };
        var loads = 0;
        var downloads = 0;
        var manifestLoads = 0;
        string? requestedVersion = null;
        var publishes = 0;
        var fail = false;
        var cache = false;
        TaskCompletionSource<MarketplaceResult<MarketplaceCatalogue>>? held = null;
        TaskCompletionSource<MarketplaceResult<MarketplaceManifest>>? heldManifest = null;
        var applySuccess = true;
        TaskCompletionSource? heldSteam = null;
        MarketplacePageActions Actions() => new()
        {
            LoadCatalogueAsync = token =>
            {
                loads++;
                if (held is not null) return held.Task;
                return fail ? Task.FromException<MarketplaceResult<MarketplaceCatalogue>>(new IOException("模拟连接失败"))
                    : Task.FromResult(new MarketplaceResult<MarketplaceCatalogue>(catalogue, cache, cache ? "网络不可用，使用已验证缓存。" : null));
            },
            LoadManifestAsync = (entry, token) =>
            { manifestLoads++; return Task.FromResult(new MarketplaceResult<MarketplaceManifest>(byId[entry.Id], cache, null)); },
            LoadManifestWithProgressAsync = async (entry, progress, token) =>
            {
                requestedVersion = entry.Version;
                manifestLoads++;
                progress.Report(new(256, 1024));
                if (heldManifest is not null) return await heldManifest.Task.WaitAsync(token);
                return new(byId[entry.Id], cache, null);
            },
            DownloadAsync = async (manifest, use, progress, token) =>
            {
                downloads++; token.ThrowIfCancellationRequested();
                if (use && heldSteam is not null)
                {
                    progress.Report("请等待创意工坊下载完成（1/2）：456 正在下载 50%");
                    await heldSteam.Task.WaitAsync(token);
                }
                return new MarketplaceDownloadResult(!use || applySuccess,
                    use ? applySuccess ? "模拟应用并保存完成" : "已取消应用，原排序未变更。" : "模拟下载另存完成");
            },
            NotifyDownload = window.ShowMarketplaceDownloadNotification,
            PublishCurrent = () => publishes++,
            OpenTrustedLink = _ => { },
            RefreshUserAsync = _ => Task.FromResult<GitHubMarketplaceUser?>(new("ExampleAuthor", null)),
            UpdateOrder = _ => { },
            Logout = () => { }
        };
        window.ConfigureMarketplacePage(Actions());
        Check(loads == 0 && window.MarketplaceHost.Visibility == Visibility.Collapsed, "Market does not fetch during startup");
        await ExerciseFirstUseWarning(window, output, checks, () => loads);
        window.MarketPageTab.IsChecked = true;
        var page = (MarketplacePage)window.MarketplaceHost.Content;
        await page.PendingOperation;
        await Idle(window);
        Check(loads == 1 && page.ResultsList.Items.Count == 2 && page.SelectedManifest is null && manifestLoads == 0,
            "Entering market reads only the catalogue and never downloads a sorting manifest");
        Check(page.AuthorMenuButton.Content.ToString()!.Contains("ExampleAuthor") && page.MyOrdersButton.IsEnabled,
            "Market header restores the shared GitHub identity and enables published-order management");
        Check(!page.Focusable && !page.IsTabStop && page.FocusVisualStyle is null
            && Descendants<ScrollViewer>(page).All(viewer => !viewer.Focusable),
            "Market containers and scroll surfaces cannot acquire the window-sized focus frame");
        Check(page.DetailDlc.Text == "皇权、文化、生物技术、异象、奥德赛" && page.SelectedEntry!.RequiredDlc.SequenceEqual(dlc)
            && GameContentNames.DlcName("unknown.expansion") == "unknown.expansion",
            "DLC labels are Chinese while dependency identifiers remain intact and unknown names remain readable");
        Check(page.DownloadButton.Content.ToString() == "下载排序并另存为" && page.UseButton.Content.ToString() == "下载排序并使用",
            "Download actions clearly distinguish save-as from applying the order");
        var mine = new MarketplaceMyOrdersWindow(catalogue.Entries) { Owner = window };
        mine.Show();
        await Idle(mine);
        mine.OrdersList.SelectedIndex = 0;
        Check(mine.OrdersList.Items.Count == 2 && mine.EditButton.IsEnabled && mine.SelectedEntry?.Id == catalogue.Entries[0].Id,
            "Published-order secondary window selects a known immutable order for updating");
        mine.Close();
        Check(window.ModEditorBody.Visibility == Visibility.Collapsed && window.ModEditorFooter.Visibility == Visibility.Collapsed,
            "Market hides editor and editor-only footer");
        page.SearchBox.Text = "新殖民地";
        Check(page.ResultsList.Items.Count == 1, "Search filters locally without another request");
        page.ResultsList.SelectedIndex = 0;
        await page.PendingOperation;
        Check(page.DetailTitle.Text == second.Title && manifestLoads == 0 && page.DownloadButton.IsEnabled,
            "Selecting and searching show catalogue metadata without downloading the sorting file");
        page.SearchBox.Clear();
        page.DownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.PendingOperation;
        Check(manifestLoads == 1 && page.SelectedManifest?.Id == second.Id, "Explicit download fetches the selected sorting file once");
        Check(window.MarketplaceNotice.IsVisible && window.MarketplaceNoticeTitle.Text == "排序已另存为"
            && window.MarketplaceNoticeCancelButton.Visibility == Visibility.Collapsed,
            "Saving a downloaded order completes the top-right notification without claiming application");
        heldSteam = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.UseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(window);
        Check(page.IsBusy && window.MarketplaceNotice.IsVisible && window.MarketplaceNoticeProgress.IsIndeterminate
            && window.MarketplaceNoticeText.Text.Contains("请等待创意工坊下载完成（1/2）")
            && window.MarketplaceNoticeCancelButton.IsVisible,
            "Waiting for Workshop downloads stays visible in the top-right progress notification before import");
        heldSteam.SetResult();
        await page.PendingOperation;
        heldSteam = null;
        Check(window.MarketplaceNoticeTitle.Text == "已成功使用排序" && window.MarketplaceNoticeProgress.Value == 100,
            "Only successful application completes with a saved-order success notification");
        applySuccess = false;
        page.UseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.PendingOperation;
        Check(window.MarketplaceNoticeTitle.Text == "排序操作未完成" && !window.MarketplaceNoticeProgress.IsVisible,
            "Declined application never displays the success notification");
        applySuccess = true;
        page.PublishButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(downloads == 3 && publishes == 1 && manifestLoads == 1, "Download and use reuse the matching verified sorting file");
        for (var i = 0; i < 12; i++)
        {
            window.ModsPageTab.IsChecked = true;
            window.LogsPageTab.IsChecked = true;
            window.MarketPageTab.IsChecked = true;
        }
        var settled = System.Diagnostics.Stopwatch.StartNew();
        do { await Task.Delay(40); await Idle(window); }
        while (window.MarketplaceHost.RenderTransform.Value.OffsetX != 0 && settled.Elapsed < TimeSpan.FromSeconds(3));
        Check(loads == 1 && ReferenceEquals(page, window.MarketplaceHost.Content), "Rapid three-tab switching retains market state without polling");
        Check(window.MarketplaceHost.RenderTransform is TranslateTransform { X: 0 }
            && Math.Abs(window.MainPageTabs.IndicatorX - window.MarketPageTab.TranslatePoint(new Point(), window.MainPageTabs).X) < 1,
            $"Page slide and traveling tab indicator settle on market: offset={window.MarketplaceHost.RenderTransform.Value.OffsetX}, indicator={window.MainPageTabs.IndicatorX}, tab={window.MarketPageTab.TranslatePoint(new Point(), window.MainPageTabs).X}");
        Check(window.MarketPageTab.TranslatePoint(new Point(), window.MainPageTabs).X
            > window.LogsPageTab.TranslatePoint(new Point(), window.MainPageTabs).X, "Market tab appears to the right of game logs");
        fail = true;
        await page.RefreshAsync();
        Check(page.ResultsList.Items.Count == 2 && page.StatusText.Text.Contains("模拟连接失败"), "Refresh failure preserves last complete results");
        fail = false; cache = true;
        await page.RefreshAsync();
        Check(page.CacheBanner.Visibility == Visibility.Visible && page.CacheText.Text.Contains("缓存"), "Offline cache is visibly marked");
        cache = false;
        await page.RefreshAsync();
        heldManifest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ShowUpdateNotification("更新已就绪", "合成更新通知", false, ready: true);
        page.DownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(window);
        Check(page.IsBusy && !page.ResultsList.IsEnabled && window.MarketplaceNotice.IsVisible
            && !window.MarketplaceNoticeProgress.IsIndeterminate && window.MarketplaceNoticeProgress.Value == 25
            && window.UpdateNotice.IsVisible && window.UpdateInstallButton.IsVisible,
            "Actual byte progress appears in its notification without replacing the independent update notice");
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            await Idle(window);
            await Task.Delay(220);
            await Idle(window);
            var chrome = (Border)page.ResultsList.Template.FindName("Chrome", page.ResultsList);
            File.WriteAllText(Path.Combine(output, $"market-list-template-{theme}.xaml"), System.Windows.Markup.XamlWriter.Save(page.ResultsList.Template));
            Check(chrome.Background is SolidColorBrush chromeBrush && page.ResultsList.Background is SolidColorBrush listBrush
                && chromeBrush.Color == listBrush.Color && chromeBrush.Color.A == 255
                && chromeBrush.Color == ((SolidColorBrush)window.FindResource("PageBrush")).Color,
                $"Disabled market results retain the themed background in {theme}: chrome={chrome.Background}, list={page.ResultsList.Background}, opacity={chrome.Opacity}");
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"market-downloading-{theme}.png"));
        }
        page.Cancel();
        await page.PendingOperation;
        Check(window.MarketplaceNoticeTitle.Text == "排序操作已取消" && !window.MarketplaceNoticeProgress.IsVisible
            && window.MarketplaceNoticeCancelButton.Visibility == Visibility.Collapsed && page.ResultsList.IsEnabled,
            "Cancelled download stops notification progress and restores market interaction");
        heldManifest = null;
        window.UpdateNotice.Visibility = window.MarketplaceNotice.Visibility = Visibility.Collapsed;
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            foreach (var width in new[] { 1360d, 960d })
            {
                window.Width = width; window.Height = width == 960 ? 640 : 820;
                await Idle(window);
                Check(page.ResultsList.ActualWidth >= 250 && page.DetailPanel.ActualWidth >= 350
                    && page.UseButton.IsVisible && page.SearchBox.ActualWidth > 200, $"Market layout {theme} at {width}");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"market-{theme}-{width}.png"));
            }
        }
        held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = page.RefreshAsync();
        Check(page.IsBusy && page.CancelButton.IsVisible && !page.UseButton.IsEnabled, "Pending network operation exposes cancel and disables apply");
        Check(page.PublishButton.IsEnabled, "Catalogue refresh never disables publication of the current order");
        page.PublishButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(publishes == 2 && page.IsBusy, "Publication can open while catalogue refresh remains pending");
        page.Cancel();
        held.SetResult(new(new(1, []), false, null));
        await pending;
        Check(page.ResultsList.Items.Count == 2 && !page.IsBusy, "Late cancelled catalogue cannot replace visible data");
        held = null;
        var oldVersion = first with { Version = "1.0.9", ReleaseNotes = "旧版排序说明", Description = "仅旧版具有的搜索词" };
        var latestVersion = first with { Version = "1.0.10", ReleaseNotes = "最新版排序说明" };
        catalogue = MarketplaceJson.CreateCatalogue([oldVersion, latestVersion, second]);
        byId[first.Id] = latestVersion;
        await page.RefreshAsync();
        Check(page.ResultsList.Items.Count == 2, "Multiple versions remain one market entry per author and order");
        page.ResultsList.SelectedIndex = 0;
        if (page.SelectedEntry?.Id != first.Id) page.ResultsList.SelectedIndex = 1;
        Check(page.SelectedEntry?.Version == "1.0.10" && page.VersionPicker.Items.Count == 2 && page.VersionHistory.Items.Count == 1,
            "Entering a grouped order selects numeric latest and exposes old-version history below DLC");
        var loadsBeforeVersions = manifestLoads;
        page.VersionPicker.SelectedIndex = 1;
        Check(page.SelectedEntry?.Version == "1.0.9" && page.SelectedManifest is null && page.DetailReleaseNotes.Text == "旧版排序说明" && manifestLoads == loadsBeforeVersions,
            "Old-version selection updates metadata without downloading manifests");
        byId[first.Id] = oldVersion;
        page.DownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.PendingOperation;
        Check(page.SelectedManifest?.Version == "1.0.9" && requestedVersion == "1.0.9", "Explicit download uses the selected old sorting version");
        await page.RefreshAsync();
        Check(page.SelectedEntry?.Version == "1.0.9" && page.VersionPicker.SelectedIndex == 1,
            "Refresh preserves the explicitly selected version when it still exists");
        page.SearchBox.Text = "仅旧版具有的搜索词";
        Check(page.ResultsList.Items.Count == 1, "Grouped search includes older-version metadata");
        page.SearchBox.Clear();
        page.VersionPicker.SelectedIndex = 0;
        byId[first.Id] = latestVersion;
        page.DownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.PendingOperation;
        Check(page.SelectedManifest?.Version == "1.0.10" && requestedVersion == "1.0.10", "Switching back to latest does not reuse an old-version manifest");
        var groupedMine = new MarketplaceMyOrdersWindow(catalogue.Entries) { Owner = window };
        Check(groupedMine.OrdersList.Items.Count == 2, "My published orders also groups versions into one entry");
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            ((App)Application.Current).Themes.Select(theme, save: false);
            window.Width = 1360; window.Height = 820;
            await Idle(window);
            PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"market-version-history-{theme}.png"));
            Check(page.VersionPicker.ActualWidth >= 100 && page.VersionHistoryPanel.IsVisible,
                $"Version picker and history are visible in {theme}");
        }
        catalogue = new(1, []);
        cache = false;
        await page.RefreshAsync();
        Check(page.ResultsList.Items.Count == 0 && page.EmptyPanel.IsVisible && !page.UseButton.IsEnabled,
            "Empty official market shows honest empty state with no fictitious results");
        var draftWindow = new MarketplacePublishWindow(first) { Owner = window };
        draftWindow.Show();
        await Idle(draftWindow);
        Check(draftWindow.IsVisible, "Publish dialog opens without GitHub login or network request");
        PrototypeSmokeCheck.SavePreview(draftWindow, Path.Combine(output, "market-publish.png"));
        draftWindow.Close();
        window.ModsPageTab.IsChecked = true;
        await Idle(window);
        Check(window.ModEditorBody.IsVisible && window.ModEditorFooter.IsVisible
            && revision == window.ViewModel.ContentRevision && original.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)),
            "Browsing, cancelling and publishing preserve editor order and history");
    }

    private static async Task ExerciseFirstUseWarning(MainWindow window, string output, List<string> checks, Func<int> loads)
    {
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        var store = new MarketplaceNoticeStore(UserStorage.DirectoryPath);
        Check(!store.IsAcknowledged(), "A new profile must see the market warning");
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{broken");
        Check(!store.IsAcknowledged(), "A corrupt acknowledgment cannot suppress the warning");
        File.WriteAllText(store.FilePath, new string('x', 2048));
        Check(!store.IsAcknowledged(), "Oversized acknowledgment is ignored safely");

        var themes = ((App)Application.Current).Themes;
        foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
        {
            themes.Select(theme, save: false);
            var integrated = theme == ThemePreference.Dark;
            Exception? failure = null;
            var observation = window.Dispatcher.InvokeAsync(async () =>
            {
                var dialog = Application.Current.Windows.OfType<MessageDialogWindow>().Single();
                var button = dialog.ButtonsPanel.Children.OfType<Button>().Single();
                var elapsed = Stopwatch.StartNew();
                try
                {
                    Check(dialog.Title == "警告" && dialog.MessageText.Text == MarketplaceFirstUseNotice.Text
                        && dialog.TriangleIcon.IsVisible, $"{theme}: Chinese warning includes its warning symbol and exact notice");
                    Check(!button.IsEnabled && button.Content.ToString() == "已知晓（5 秒）" && !dialog.CloseButton.IsEnabled,
                        $"{theme}: first frame exposes the disabled five-second acknowledgment");
                    if (integrated) Check(loads() == 0 && !store.IsAcknowledged(), "Market requests and persistence wait for acknowledgment");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    dialog.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog.Close();
                    Check(dialog.IsVisible && dialog.Result == MessageBoxResult.None, $"{theme}: click, Escape, X and window-close cannot bypass the countdown");
                    PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, $"market-warning-{theme}-countdown.png"));
                    await Task.Delay(1100);
                    Check(!button.IsEnabled && button.Content.ToString()!.Contains("秒")
                        && button.Content.ToString() != "已知晓（5 秒）", $"{theme}: remaining seconds visibly decrease while closing stays blocked");
                    if (integrated) Check(loads() == 0 && !store.IsAcknowledged(), "No market requests or acknowledgment are recorded during the countdown");
                    while (!button.IsEnabled && elapsed.Elapsed < TimeSpan.FromSeconds(9)) await Task.Delay(100);
                    Check(button.IsEnabled && button.Content.ToString() == "已知晓" && dialog.CloseButton.IsEnabled,
                        $"{theme}: confirmation and window-close unlock after five seconds");
                    PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, $"market-warning-{theme}-ready.png"));
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    // Even a failed assertion must respect the real minimum reading time.
                    await Task.Delay(5200 - (int)Math.Min(5200, elapsed.ElapsedMilliseconds));
                    if (integrated) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            }, DispatcherPriority.ApplicationIdle);
            if (integrated) window.MarketPageTab.IsChecked = true;
            else Check(MarketplaceFirstUseNotice.Show(window), "Closing an elapsed warning acknowledges it");
            await observation.Task.Unwrap();
            if (failure is not null) throw failure;
        }
        Check(new MarketplaceNoticeStore(UserStorage.DirectoryPath).IsAcknowledged(), "Acknowledgment persists across new store instances");
        Check(!new MarketplaceNoticeStore(Path.Combine(output, "another-profile")).IsAcknowledged(), "Acknowledgment remains specific to each profile");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp").Any(), "Acknowledgment leaves no temporary files");
        var requests = loads();
        window.ModsPageTab.IsChecked = true;
        window.MarketPageTab.IsChecked = true;
        Check(!Application.Current.Windows.OfType<MessageDialogWindow>().Any() && requests == loads(),
            "Reopening the acknowledged market neither prompts nor reloads its catalogue");

        // Owned windows must not trap application/owner shutdown during the countdown.
        var closingOwner = new Window { Owner = window, Width = 300, Height = 200, ShowInTaskbar = false };
        closingOwner.Show();
        _ = window.Dispatcher.BeginInvoke(closingOwner.Close, DispatcherPriority.ApplicationIdle);
        Check(!MarketplaceFirstUseNotice.Show(closingOwner) && !closingOwner.IsVisible,
            "Closing the owner terminates its countdown dialog without acknowledging it");
    }

    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
#endif
