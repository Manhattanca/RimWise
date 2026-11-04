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
        if (updateTask is { IsCompleted: false } || settingsOpen || lifetime.IsCancellationRequested)
            return Task.CompletedTask;
        updateCancellation?.Dispose();
        updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateTask = CheckAndDownloadUpdateAsync(manual, updateCancellation.Token);
        return updateTask;
    }

    private async Task CheckAndDownloadUpdateAsync(bool manual, CancellationToken token)
    {
        CheckUpdatesMenu.IsEnabled = false;
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
            ShowUpdateNotification($"新版本 {release.Version} 已下载", release.IsInstaller
                ? "校验通过。请完成当前操作并关闭软件后运行安装包。"
                : "校验通过。请关闭软件，清空程序文件夹后解压更新；保留独立的用户数据文件夹。", false, ready: true);
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
        UpdateFolderButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotice.Visibility = Visibility.Visible;
        // Do not call Activate/Focus/ShowDialog: keyboard focus and sorting stay where they are.
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => UpdateNotice.Visibility = Visibility.Collapsed;

    private void OpenUpdateFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(downloadedUpdatePath);
            if (folder is null || !Directory.Exists(folder)) throw new DirectoryNotFoundException();
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add(folder);
            using var process = Process.Start(info);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            ShowUpdateNotification("无法打开更新文件夹", "更新缓存可能已被移动，请重新检查更新。", true);
        }
    }

    private async Task StopUpdateDownloadAsync()
    {
        updateCancellation?.Cancel();
        if (updateTask is not null) await updateTask;
    }
}
