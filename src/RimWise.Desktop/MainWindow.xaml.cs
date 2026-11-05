using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Xml;
using System.Text.Json;
using Microsoft.Win32;
using RimWise.Desktop.Presentation;
using RimWise.Formats;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;
using RimWise.Integration.Steam;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

public partial class MainWindow : Window
{
    private static string ProductTitle => "RimWise " +
        (RimWise.Integration.Diagnostics.ApplicationBuildIdentity.ProductVersion(typeof(MainWindow).Assembly) ?? "开发版");
    private readonly PrototypeFileService files = new();
    private readonly PathSettingsStore pathStore = new(UserStorage.DirectoryPath);
    private readonly CancellationTokenSource lifetime = new();
    private GamePaths paths = new();
    private Task? pathInitialization;
    private bool settingsOpen;
    private Task? scanTask;
    private CancellationTokenSource? scanCancellation;
    private int scanGeneration;
    public MainViewModel ViewModel { get; } = new();

    public MainWindow(bool initializePaths = true)
    {
        InitializeComponent();
        Title = ProductTitle;
        DataContext = ViewModel;
        InitializeListDisplay();
        InitializeModInteraction();
        InitializeContextMenus();
        InitializeGameTools();
        if (initializePaths) InitializeRecovery();
        Themes.Changed += Theme_Changed;
        UpdateThemeChecks();
        if (initializePaths) Loaded += async (_, _) =>
        {
            if (pathInitialization is not null) return;
            pathInitialization = InitializeStartupAsync();
            _ = StartBackgroundUpdateAsync();
            await pathInitialization;
        };
    }

    private async Task InitializeStartupAsync()
    {
        await InitializePathsAsync();
        await LoadCustomRulesAsync();
        if (lifetime.IsCancellationRequested) return;
        try { await LoadStartupSnapshotAsync(); }
        catch (OperationCanceledException) { return; }
        if (lifetime.IsCancellationRequested) return;
        // Show cached content before inspecting historical task journals. Recovery still
        // requires the user's answer and never starts an AI call during initialization.
        var interruptedTasks = await AskInterruptedTasksAsync();
        if (lifetime.IsCancellationRequested) return;
        await LoadCommunityDatabaseAsync();
        if (ShouldReadStartupOrder) await RestartRefreshAsync(deferContent: true);
        else if (HasScanPaths()) await RestartScanAsync();
        if (!lifetime.IsCancellationRequested)
        {
            OfferEditorRecovery();
            ShowInterruptedFiles(startup: true);
            ContinueInterruptedTasks(interruptedTasks);
        }
    }

    private async Task ReadOrderAndScanAsync(bool onlyWhenEmpty)
    {
        if (!onlyWhenEmpty)
        {
            await RestartRefreshAsync();
            return;
        }
        var notice = await LoadCurrentOrderAsync(onlyWhenEmpty);
        var revision = ViewModel.ContentRevision;
        if (!lifetime.IsCancellationRequested && HasScanPaths()) await RestartScanAsync();
        if (lifetime.IsCancellationRequested) return;
        if (notice is not null && revision == ViewModel.ContentRevision) ViewModel.Status = notice;
        else if (ViewModel.HasDocument && ViewModel.HasCatalogue && !ViewModel.HasChanges)
            ViewModel.Status = $"已读取排序 {ViewModel.Rows.Count} 项，未启用 {ViewModel.AvailableMods.Count} 个模组";
        if (ViewModel.SelectedRow is null && ViewModel.SelectedAvailable is null)
            ViewModel.SelectedRow = ViewModel.Rows.FirstOrDefault();
    }

    // A reader parameter lets UI checks hold a read open and verify that intervening
    // imports/edits win. Production always uses the read-only file service.
    internal async Task<string?> LoadCurrentOrderAsync(bool onlyWhenEmpty,
        Func<string, Task<ModsConfigDocument>>? reader = null)
    {
        if (onlyWhenEmpty && ViewModel.HasDocument) return null;
        var revision = ViewModel.ContentRevision;
        try
        {
            var file = CurrentOrderLocation.Resolve(paths);
            var loaded = await (reader?.Invoke(file) ?? Task.Run(() => files.Read(file), lifetime.Token));
            if (lifetime.IsCancellationRequested || revision != ViewModel.ContentRevision) return null;
            ViewModel.Load(loaded, file);
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            if (lifetime.IsCancellationRequested || revision != ViewModel.ContentRevision) return null;
            return ex is FileNotFoundException or DirectoryNotFoundException
                ? "未找到当前排序：请启动游戏生成配置，或在路径设置中指定配置目录；也可导入排序文件"
                : "无法读取当前排序，请检查游戏配置目录或手动导入排序文件";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (pendingUpdateInstallation is not null)
        {
            if (!ConfirmUpdateClose()) e.Cancel = true;
        }
        else if (ViewModel.HasChanges && !ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
        StartPendingUpdate(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        gameLogPage?.Dispose();
        recoveryEnabled = false;
        recoveryTimer.Stop();
        if (ViewModel is not null)
        {
            ViewModel.EditorChanged -= QueueRecoverySnapshot;
            try { editorRecovery.ClearCurrent(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        lifetime.Cancel();
        ViewModel?.CancelPreviewLoads();
        Themes.Changed -= Theme_Changed;
        gameStatusTimer.Stop();
        gameProcesses.Dispose();
        base.OnClosed(e);
    }

    private async Task InitializePathsAsync()
    {
        try
        {
            var saved = await Task.Run(pathStore.Load, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (saved is not null)
            {
                paths = saved;
                var valid = true;
                try { GamePathValidation.Validate(paths); }
                catch (ArgumentException) { valid = false; }
                if (!ViewModel.HasDocument) ViewModel.Status = !valid ? "配置目录不可用，请检查路径设置" :
                    string.IsNullOrEmpty(paths.GameDirectory) ? "未设置游戏目录" : "";
                return;
            }
            var found = await Task.Run(() => SteamRootLocator.Discover(lifetime.Token), lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (found.Installations.Count == 1)
            {
                paths = found.Installations[0].Paths;
                if (!ViewModel.HasDocument) ViewModel.Status = "已识别 Steam 游戏路径";
            }
            else if (!ViewModel.HasDocument) ViewModel.Status = found.Installations.Count > 1 ? "找到多个安装，请在路径设置中选择" : "未找到环世界，请检查路径设置";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            if (!lifetime.IsCancellationRequested) ViewModel.Status = "无法读取路径配置，请在设置中检查";
        }
    }

    private async Task OpenPathsAsync(bool detect)
    {
        if (settingsOpen) return;
        settingsOpen = true;
        try
        {
            await StopUpdateDownloadAsync();
            if (pathInitialization is not null) await pathInitialization;
            if (lifetime.IsCancellationRequested) return;
            var dialog = new PathSettingsWindow(paths, pathStore, UserDataMoveBlocker,
                () => aiSortingWindow?.IsWorking == true ? "请等待自动排序或日志保存结束后再更换日志目录。" : null) { Owner = this };
            if (detect) dialog.ContentRendered += async (_, _) => await dialog.DetectAsync();
            if (dialog.ShowDialog() == true && dialog.SavedPaths is { } saved)
            {
                paths = saved;
                if (dialog.UserDataMoveRequested) { RestartForUserDataMove(); return; }
                ViewModel.Status = "路径已保存";
                await ReadOrderAndScanAsync(onlyWhenEmpty: true);
            }
        }
        finally { settingsOpen = false; }
    }

    private string? UserDataMoveBlocker()
    {
        if (ViewModel.HasChanges) return "请先保存当前排序，再更换用户数据目录。";
        if (aiSortingWindow?.IsWorking == true || importCancellation is not null || scanTask is { IsCompleted: false })
            return "请等待刷新、下载或 AI 任务结束后再更换用户数据目录。";
        if (OwnedWindows.Cast<Window>().Any(window => window is not PathSettingsWindow))
            return "请先关闭其他设置和任务窗口，再更换用户数据目录。";
        return null;
    }

    private void RestartForUserDataMove()
    {
        try
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--resume-after");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var next = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("未能创建重启进程。");
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UserStorage.Location!.CancelMove();
            MessageDialog.Show(this, "无法自动重启，仍使用原数据目录。\n" + ex.Message, "用户数据", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Paths_Click(object sender, RoutedEventArgs e) => await OpenPathsAsync(detect: false);
    private async void DetectPaths_Click(object sender, RoutedEventArgs e) => await OpenPathsAsync(detect: true);
    private void Available_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || System.Windows.Controls.ItemsControl.ContainerFromElement(AvailableGrid, source) is not System.Windows.Controls.DataGridRow row
            || row.Item is not InstalledModRow item) return;
        ViewModel.SelectedAvailable = item;
        if (ViewModel.AddToOrderCommand.CanExecute(null)) ViewModel.AddToOrderCommand.Execute(null);
        e.Handled = true;
    }

    private void Order_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || System.Windows.Controls.ItemsControl.ContainerFromElement(OrderGrid, source) is not System.Windows.Controls.DataGridRow row
            || row.Item is not LoadOrderRow item) return;
        ViewModel.SelectedRow = item;
        if (ViewModel.RemoveFromOrderCommand.CanExecute(null)) ViewModel.RemoveFromOrderCommand.Execute(null);
        e.Handled = true;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsScanning) { scanCancellation?.Cancel(); return; }
        if (pathInitialization is not null) await pathInitialization;
        if (ViewModel.IsScanning) { scanCancellation?.Cancel(); return; }
        if (!lifetime.IsCancellationRequested && ConfirmDiscard())
            await ReadOrderAndScanAsync(onlyWhenEmpty: false);
    }

    private bool HasScanPaths() => paths.GameDirectory.Length > 0 || paths.LocalModsDirectory.Length > 0 || paths.WorkshopDirectory.Length > 0;

    internal async Task RestartScanAsync()
    {
        var generation = ++scanGeneration;
        scanCancellation?.Cancel();
        if (scanTask is not null) await scanTask;
        if (generation != scanGeneration || lifetime.IsCancellationRequested) return;
        if (!HasScanPaths()) { ViewModel.Status = "请先在设置中选择游戏或模组目录"; return; }
        scanTask = PerformScanAsync(generation, paths);
        await scanTask;
    }

    private async Task PerformScanAsync(int generation, GamePaths selectedPaths)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        scanCancellation = cancellation;
        ViewModel.IsScanning = true;
        ViewModel.Status = "正在刷新模组和当前排序…";
        var progress = new Progress<ScanProgress>(value =>
        {
            if (generation == scanGeneration && ViewModel.IsScanning)
                ViewModel.Status = $"正在刷新，已检查 {value.Processed} 项…";
        });
        try
        {
            var result = await Task.Run(() => new ModScanner().Scan(selectedPaths, cancellation.Token, progress), cancellation.Token);
            if (result.Diagnostics.Any(issue => issue.Code is "ScanLimit" or "ScanRootUnreadable"))
                throw new IOException("模组目录未完整读取，请检查路径设置后重试。");
            var records = await ReadActivityAsync(result, observe: true);
            if (generation != scanGeneration || cancellation.IsCancellationRequested) return;
            ViewModel.ApplyScan(result, records);
            ViewModel.IsShowingCache = false;
        }
        catch (OperationCanceledException)
        {
            if (generation == scanGeneration && !lifetime.IsCancellationRequested)
                ViewModel.Status = "已取消刷新，保留上次结果";
        }
        catch (Exception ex)
        {
            if (generation == scanGeneration && !lifetime.IsCancellationRequested)
                ViewModel.Status = "刷新未完成，保留上次结果：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(scanCancellation, cancellation))
            {
                ViewModel.IsScanning = false;
                scanCancellation = null;
            }
        }
    }

#if DEBUG
    internal Task StartupForCheckAsync(GamePaths selectedPaths)
    {
        paths = selectedPaths;
        return ReadOrderAndScanAsync(onlyWhenEmpty: true);
    }
    internal Task ScanForCheckAsync(GamePaths selectedPaths)
    {
        paths = selectedPaths;
        return RestartScanAsync();
    }
    internal Task RefreshForCheckAsync(GamePaths selectedPaths,
        Func<string, CancellationToken, Task<ModsConfigDocument>>? reader = null,
        Func<GamePaths, CancellationToken, IProgress<ScanProgress>, Task<ModScanResult>>? scanner = null)
    {
        paths = selectedPaths;
        return RestartRefreshAsync(reader, scanner);
    }
    internal void CancelScanForCheck() => scanCancellation?.Cancel();
#endif
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Workshop_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (ViewModel.SelectedWorkshopUri is not { } uri || uri != e.Uri) return;
        if (new ModLocationOpener().OpenWorkshop(uri) == WorkshopOpenResult.Failed)
            ViewModel.Status = "无法打开创意工坊，可复制链接手动访问";
    }

    private void ModDirectory_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!new ModLocationOpener().OpenDirectory(ViewModel.SelectedDirectory))
            ViewModel.Status = "无法打开模组文件夹，请检查路径是否仍然存在";
    }
    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageDialog.Show(this, ProductTitle, "关于", MessageBoxButton.OK, MessageBoxImage.Information);

    private ThemeService Themes => ((App)Application.Current).Themes;
    private void Theme_Changed(object? sender, EventArgs e) => UpdateThemeChecks();
    private void UpdateThemeChecks()
    {
        SystemThemeItem.IsChecked = Themes.Preference == ThemePreference.System;
        LightThemeItem.IsChecked = Themes.Preference == ThemePreference.Light;
        DarkThemeItem.IsChecked = Themes.Preference == ThemePreference.Dark;
    }
    private void SetTheme(ThemePreference preference)
    {
        try { Themes.Select(preference); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            UpdateThemeChecks();
            MessageDialog.Show(this, ex.Message, "无法保存外观设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void SystemTheme_Click(object sender, RoutedEventArgs e) => SetTheme(ThemePreference.System);
    private void LightTheme_Click(object sender, RoutedEventArgs e) => SetTheme(ThemePreference.Light);
    private void DarkTheme_Click(object sender, RoutedEventArgs e) => SetTheme(ThemePreference.Dark);

    internal void LoadSample()
    {
        var resource = Application.GetResourceStream(new Uri(
            "/RimWise;component/Samples/ModsConfig.xml", UriKind.Relative))
            ?? throw new InvalidOperationException("演示资源缺失。");
        using var input = resource.Stream;
        ViewModel.Load(new ModsConfigXmlFormat().Read(input), "ModsConfig.xml");
    }

    private bool ConfirmDiscard()
    {
        // Closing or explicitly re-reading starts a new editing session.
        return !ViewModel.HasChanges
            || MessageDialog.Show(this, "当前有未保存的调整。继续操作会放弃这些调整，是否继续？", "未保存的调整",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    private void RunFileAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or ArgumentException or JsonException)
        {
            ViewModel.Status = "操作未完成：" + ex.Message;
            MessageDialog.Show(this, ex.Message, "文件操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
