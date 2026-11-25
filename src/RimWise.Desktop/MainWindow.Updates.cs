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
    private ReleaseDownload? fullUpdateRelease;
    private PreparedBinaryUpdate? preparedDelta;
    private bool preparingUpdate;
    private ProcessStartInfo? pendingUpdateInstallation;
    private Exception? pendingUpdateStartError;

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
        preparedDelta?.Dispose();
        preparedDelta = null;
        downloadedRelease = null;
        fullUpdateRelease = null;
        downloadedUpdatePath = null;
        UpdateInstallButton.Visibility = Visibility.Collapsed;
        UpdateRetryButton.Visibility = Visibility.Collapsed;
        var downloading = false;
        var reportProgress = false;
        try
        {
            await Task.Run(PreparedBinaryUpdate.ReclaimExpired, token);
            token.ThrowIfCancellationRequested();
            if (manual) ShowUpdateNotification("正在检查更新", "正在连接更新源，不影响当前操作。", false);
            using var http = GitHubReleaseUpdater.CreateHttpClient();
            using var mirror = GitHubReleaseUpdater.CreateMirrorHttpClient();
            var updater = new GitHubReleaseUpdater(http, mirror);
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
            CacheReleaseNotes(release);
            reportProgress = true;
            ShowUpdateNotification($"发现新版本 {release.Version}", "正在后台下载；可以继续使用软件。", false);
            var progress = new Progress<DownloadProgress>(value =>
            {
                if (reportProgress && !token.IsCancellationRequested)
                    UpdateNoticeText.Text = value.UsingMirror
                        ? $"已切换服务器源，正在后台下载：{value.Received * 100 / value.Total}%，不影响当前操作。"
                        : $"正在后台下载：{value.Received * 100 / value.Total}%，不影响当前操作。";
            });
            var cache = Path.Combine(UserStorage.DirectoryPath, "Updates");
            var fullRelease = release;
            fullUpdateRelease = fullRelease;
            string? saved = null;
            if (release.Delta is { } delta)
            {
                try
                {
                    var archive = await Task.Run(() => updater.DownloadAsync(delta, cache, progress, token), token);
                    reportProgress = false;
                    UpdateNoticeText.Text = "正在校验并重建新版程序，不影响当前操作。";
                    var prepared = await Task.Run(() => BinaryUpdatePackage.Prepare(archive, AppContext.BaseDirectory, delta, token), token);
                    if (token.IsCancellationRequested) prepared.Dispose();
                    token.ThrowIfCancellationRequested();
                    preparedDelta = prepared;
                    saved = preparedDelta.InstallerPath;
                    release = preparedDelta.Installer;
                    DiagnosticEventLog.Current?.Record("update", "delta-verified", $"from={delta.FromVersion};to={delta.Version};bytes={delta.Size}");
                }
                catch (Exception ex) when (!token.IsCancellationRequested && IsDeltaFailure(ex))
                {
                    UpdateNoticeText.Text = "增量更新不可用，正在改为下载完整安装包。";
                    DiagnosticEventLog.Current?.Record("update", "delta-fallback", ex.GetType().Name);
                    reportProgress = true;
                }
            }
            saved ??= await Task.Run(() => updater.DownloadAsync(fullRelease, cache, progress, token), token);
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
                HttpRequestException => "更新源暂时不可用，请检查网络后重试。",
                UnauthorizedAccessException => "无法写入更新缓存，请检查用户数据目录权限。",
                IOException => "更新文件无法读写，可能正在被其他程序使用，请稍后重试。",
                JsonException or InvalidOperationException or KeyNotFoundException or FormatException => "更新源返回的版本信息无法读取。",
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
        var reveal = UpdateNotice.Visibility != Visibility.Visible;
        UpdateNotice.Visibility = Visibility.Visible;
        if (reveal) Services.InterfaceMotion.RevealNotification(UpdateNotice);
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
            CancelContentEnrichment();
            var release = downloadedRelease;
            var path = downloadedUpdatePath;
            UpdateNoticeText.Text = "正在校验安装包…";
            try { await VerifyAndStartUpdateAsync(path, release); }
            catch (Exception ex) when (preparedDelta is not null && !lifetime.IsCancellationRequested && IsDeltaFailure(ex))
            {
                pendingUpdateInstallation = null;
                await PrepareFullUpdateAsync(ex);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsDeltaFailure(ex) || ex is InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            ShowUpdateNotification("更新未开始", "安装包无法校验或启动，请重新检查更新。当前软件仍可使用。", true);
            DiagnosticEventLog.Current?.Record("update", "install-start-failed", ex.GetType().Name);
        }
        finally
        {
            pendingUpdateInstallation = null;
            pendingUpdateStartError = null;
            preparingUpdate = false;
            UpdateInstallButton.IsEnabled = true;
        }
    }

    private async Task VerifyAndStartUpdateAsync(string path, ReleaseDownload release)
    {
        // Keep the checked executable read-only until Setup accepts the handoff.
        using var installerLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!await GitHubReleaseUpdater.IsVerifiedAsync(path, release, lifetime.Token))
            throw new InvalidDataException("安装包已变化或丢失，请重新检查更新。");
        if (preparedDelta is { } delta)
            await Task.Run(() => delta.VerifyPayload(lifetime.Token), lifetime.Token);
        if (lifetime.IsCancellationRequested || !CanInstallUpdate()) return;
        using var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RimWise.Desktop_is1");
        var registered = registry?.GetValue("InstallLocation") as string;
        var directory = UpdateInstallation.MatchInstalledDirectory(AppContext.BaseDirectory, registered);
        var portable = string.IsNullOrWhiteSpace(registered) || !string.Equals(directory,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)), StringComparison.OrdinalIgnoreCase);
        pendingUpdateInstallation = UpdateInstallation.CreateStartInfo(path, release, directory, portable);
        Close(); // OnClosing handles unsaved edits before starting the installer.
        if (pendingUpdateStartError is { } error && preparedDelta is not null) throw error;
    }

    private async Task PrepareFullUpdateAsync(Exception reason)
    {
        var release = fullUpdateRelease ?? throw new InvalidDataException("缺少完整安装包。");
        preparedDelta?.Dispose();
        preparedDelta = null;
        downloadedRelease = null;
        downloadedUpdatePath = null;
        ShowUpdateNotification("正在改用完整更新", "增量文件未通过复核，正在下载完整安装包。当前软件仍可使用。", false);
        DiagnosticEventLog.Current?.Record("update", "delta-install-fallback", reason.GetType().Name);
        using var http = GitHubReleaseUpdater.CreateHttpClient();
        using var mirror = GitHubReleaseUpdater.CreateMirrorHttpClient();
        var updater = new GitHubReleaseUpdater(http, mirror);
        var cache = Path.Combine(UserStorage.DirectoryPath, "Updates");
        var token = lifetime.Token;
        var path = await Task.Run(() => updater.DownloadAsync(release, cache, null, token), token);
        token.ThrowIfCancellationRequested();
        downloadedRelease = release;
        downloadedUpdatePath = path;
        ShowUpdateNotification("完整更新已就绪", "点击“更新并重启”应用新版。", false, ready: true);
    }

    internal static bool IsDeltaFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or Win32Exception or HttpRequestException or OperationCanceledException or InvalidOperationException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    internal bool HasUpdateBlockingWork => aiSortingWindow?.IsWorking == true || ViewModel.IsScanning || settingsOpen
        || importCancellation is not null || marketplacePage?.IsBusy == true || pathInitialization is { IsCompleted: false };

    private bool CanInstallUpdate()
    {
        if (HasUpdateBlockingWork)
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
            preparedDelta?.RecordHandoff();
            using var process = Process.Start(pendingUpdateInstallation)
                ?? throw new InvalidOperationException("Installer did not start.");
            preparedDelta?.HandOff();
            DiagnosticEventLog.Current?.Record("update", "installer-started");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            e.Cancel = true;
            pendingUpdateStartError = ex;
            ShowUpdateNotification("更新未开始", "无法启动安装程序，当前软件保持打开。请重试或检查安全软件提示。", false, ready: true);
        }
    }

    private async Task StopUpdateDownloadAsync()
    {
        if (updateTask is not { IsCompleted: false }) return;
        updateCancellation?.Cancel();
        await updateTask;
    }
}
