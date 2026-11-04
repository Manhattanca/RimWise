using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Updates;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? updateCancellation;
    private Task? updateTask;
    private string? downloadedUpdatePath;
    private ReleaseDownload? downloadedRelease;
    private bool preparingUpdate;
    private ProcessStartInfo? pendingUpdateInstallation;

    private async Task StartBackgroundUpdateAsync()
    {
        try
        {
            // Let the main window render and startup work begin. No startup stage awaits this task.
            await Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);
            await BeginUpdateCheckAsync(manual: false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await BeginUpdateCheckAsync(manual: true);

    internal Task BeginUpdateCheckAsync(bool manual)
    {
        if (updateTask is { IsCompleted: false } || preparingUpdate || settingsOpen || lifetime.IsCancellationRequested)
            return Task.CompletedTask;
        updateCancellation?.Dispose();
        updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateTask = CheckAndDownloadUpdateAsync(manual, updateCancellation.Token);
        return updateTask;
    }

    private async Task CheckAndDownloadUpdateAsync(bool manual, CancellationToken token)
    {
        CheckUpdatesMenu.IsEnabled = false;
        downloadedRelease = null;
        downloadedUpdatePath = null;
        UpdateInstallButton.Visibility = Visibility.Collapsed;
        UpdateRetryButton.Visibility = Visibility.Collapsed;
        var downloading = false;
        var reportProgress = false;
        try
        {
            if (manual) ShowUpdateNotification("正在检查更新", "正在连接 GitHub，不影响当前操作。", false);
            using var http = GitHubReleaseUpdater.CreateHttpClient();
            var updater = new GitHubReleaseUpdater(http);
            var version = ApplicationBuildIdentity.ProductVersion(typeof(MainWindow).Assembly) ?? "0.0.0";
            var release = await updater.CheckAsync(version, token);
            token.ThrowIfCancellationRequested();
            if (release is null)
            {
                if (manual) ShowUpdateNotification("已是最新版本", "当前没有更高的正式发布版本。", false);
                DiagnosticEventLog.Current?.Record("update", "current");
                return;
            }
            downloading = true;
            reportProgress = true;
            ShowUpdateNotification($"发现新版本 {release.Version}", "正在后台下载；可以继续使用软件。", false);
            var progress = new Progress<DownloadProgress>(value =>
            {
                if (reportProgress && !token.IsCancellationRequested)
                    UpdateNoticeText.Text = $"正在后台下载：{value.Received * 100 / value.Total}% ，不影响当前操作。";
            });
            var cache = Path.Combine(UserStorage.DirectoryPath, "Updates");
            var saved = await Task.Run(() => updater.DownloadAsync(release, cache, progress, token), token);
            reportProgress = false;
            token.ThrowIfCancellationRequested();
            downloadedUpdatePath = saved;
            downloadedRelease = release;
            ShowUpdateNotification($"新版本 {release.Version} 已下载", release.IsInstaller
                ? "点击“更新并重启”即可自动安装并打开新版。未保存的调整会先询问处理。"
                : "此版本尚未提供安装包，暂时无法一键更新，请稍后重试。", !release.IsInstaller, ready: release.IsInstaller);
            DiagnosticEventLog.Current?.Record("update", "download-verified", $"version={release.Version};bytes={release.Size}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested) UpdateNotice.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Updater failures are nonfatal. Never include server bodies, signed redirect URLs,
            // account data or local paths in this notification or diagnostic record.
            if (token.IsCancellationRequested) return;
            var reason = ex switch
            {
                UpdateDownloadException failure => failure.Message,
                OperationCanceledException => "网络等待超时，请稍后重试。",
                HttpRequestException => "无法连接 GitHub，请检查网络后重试。",
                UnauthorizedAccessException => "无法写入更新缓存，请检查用户数据目录权限。",
                IOException => "更新文件无法读写，可能正在被其他程序使用，请稍后重试。",
                JsonException or InvalidOperationException or KeyNotFoundException or FormatException => "GitHub 返回的版本信息无法读取。",
                _ => "本次更新未完成，请稍后重试。"
            };
            ShowUpdateNotification(downloading ? "更新下载失败" : "更新检测失败", reason + " 当前功能可继续使用。", true);
            DiagnosticEventLog.Current?.Record("update", downloading ? "download-failed" : "check-failed", ex.GetType().Name);
        }
        finally
        {
            reportProgress = false;
            if (!lifetime.IsCancellationRequested) CheckUpdatesMenu.IsEnabled = true;
        }
    }

    internal void ShowUpdateNotification(string title, string text, bool retry, bool ready = false)
    {
        UpdateNoticeTitle.Text = title;
        UpdateNoticeText.Text = text;
        UpdateRetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        UpdateInstallButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotice.Visibility = Visibility.Visible;
        Services.InterfaceMotion.Reveal(UpdateNotice);
        // Do not call Activate/Focus/ShowDialog: keyboard focus and sorting stay where they are.
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => UpdateNotice.Visibility = Visibility.Collapsed;

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (preparingUpdate || downloadedRelease is null || downloadedUpdatePath is null) return;
        preparingUpdate = true;
        UpdateInstallButton.IsEnabled = false;
        try
        {
            if (!CanInstallUpdate()) return;
            var release = downloadedRelease;
            var path = downloadedUpdatePath;
            UpdateNoticeText.Text = "正在校验安装包…";
            // Keep the checked bytes read-only until Setup has been started.
            using var installerLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!await GitHubReleaseUpdater.IsVerifiedAsync(path, release, lifetime.Token))
                throw new InvalidDataException("安装包已变化或丢失，请重新检查更新。");
            if (lifetime.IsCancellationRequested || !CanInstallUpdate()) return;
            using var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RimWise.Desktop_is1");
            var directory = UpdateInstallation.MatchInstalledDirectory(AppContext.BaseDirectory,
                registry?.GetValue("InstallLocation") as string);
            pendingUpdateInstallation = UpdateInstallation.CreateStartInfo(path, release, directory);
            Close(); // OnClosing handles unsaved edits before starting the installer.
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            ShowUpdateNotification("更新未开始", "安装包无法校验或启动，请重新检查更新。当前软件仍可使用。", true);
            DiagnosticEventLog.Current?.Record("update", "install-start-failed", ex.GetType().Name);
        }
        finally
        {
            pendingUpdateInstallation = null;
            preparingUpdate = false;
            UpdateInstallButton.IsEnabled = true;
        }
    }

    private bool CanInstallUpdate()
    {
        if (aiSortingWindow?.IsWorking == true || ViewModel.IsScanning || settingsOpen || pathInitialization is { IsCompleted: false })
        {
            ShowUpdateNotification("更新已就绪", "请在当前排序、测试或刷新完成后点击更新，软件不会中断正在进行的任务。", false, ready: true);
            return false;
        }
        var others = Process.GetProcessesByName("RimWise");
        try
        {
            if (others.Any(process => process.Id != Environment.ProcessId))
            {
                ShowUpdateNotification("更新已就绪", "请先保存并关闭其他 RimWise 窗口，再点击更新并重启。", false, ready: true);
                return false;
            }
        }
        finally { foreach (var process in others) process.Dispose(); }
        return true;
    }

    private bool ConfirmUpdateClose()
    {
        if (!CanInstallUpdate()) return false;
        if (ConfirmUpdateEdits()) return true;
        ShowUpdateNotification("更新已就绪", "已保留当前调整。准备好后可再次点击“更新并重启”。", false, ready: true);
        return false;
    }

    internal bool ConfirmUpdateEdits()
    {
        if (!ViewModel.HasChanges) return true;
        var choice = Views.MessageDialog.Show(this, "当前排序尚未保存。是否保存后更新？\n选择“否”会放弃未保存的调整；选择“取消”可继续使用。",
            "更新并重启", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.No) return true;
        if (choice != MessageBoxResult.Yes) return false;
        RunFileAction(SaveCurrentOrder);
        return !ViewModel.HasChanges;
    }

    private void StartPendingUpdate(CancelEventArgs e)
    {
        if (e.Cancel || pendingUpdateInstallation is null) return;
        try
        {
            using var process = Process.Start(pendingUpdateInstallation)
                ?? throw new InvalidOperationException("Installer did not start.");
            DiagnosticEventLog.Current?.Record("update", "installer-started");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            e.Cancel = true;
            ShowUpdateNotification("更新未开始", "无法启动安装程序，当前软件保持打开。请重试或检查安全软件提示。", false, ready: true);
        }
    }

    private async Task StopUpdateDownloadAsync()
    {
        updateCancellation?.Cancel();
        if (updateTask is not null) await updateTask;
    }
}
