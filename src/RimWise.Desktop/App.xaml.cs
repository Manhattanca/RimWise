using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Updates;
using System.Reflection;

namespace RimWise.Desktop;

public partial class App : Application
{
    // Existence only: multiple RimWise windows remain supported. Installers must wait
    // for all instances to exit before replacing or uninstalling managed files.
    private readonly Mutex installationMutex = new(false, @"Local\RimWise.Installation.Active");
    internal ThemeService Themes { get; private set; } = null!;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isSmokeTest = false;
        string? migrationNotice = null;
#if DEBUG
        isSmokeTest = e.Args.Length == 2 && e.Args[0] is "--smoke-test" or "--interaction-smoke" or "--marker-smoke" or "--ai-ui-smoke" or "--performance-smoke" or "--runtime-ui-smoke" or "--ai-sorting-smoke" or "--message-dialog-smoke" or "--diagnostic-export-smoke" or "--diagnostics-smoke" or "--steam-ui-smoke";
#endif
        // Storage migration can include hashing/copying. Keep the dispatcher responsive and
        // show a small opening window instead of appearing to do nothing before MainWindow.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Window? opening = null;
        if (!isSmokeTest)
        {
            opening = new Window { Title = "RimWise", Width = 340, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize,
                Content = new System.Windows.Controls.TextBlock { Text = "正在打开用户数据…",
                    Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center } };
            opening.Show();
        }
        try
        {
            await Task.Run(() => WaitForPreviousInstance(e.Args));
            if (isSmokeTest)
                UserStorage.DirectoryPath = Path.Combine(Path.GetFullPath(e.Args[1]), "smoke-user-data");
            else
            {
                UserStorage.Location = await Task.Run(() => new UserDataLocation(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimWise"), AppContext.BaseDirectory));
                UserStorage.DirectoryPath = UserStorage.Location.DirectoryPath;
                try
                {
                    var movedLogs = await Task.Run(() => new AutomaticSortingLogStore(UserStorage.DirectoryPath).ResumeDirectoryMove());
                    if (movedLogs is { CleanupPending: true }) migrationNotice = "日志已迁往新目录，部分原文件暂未清理，请检查是否被占用。";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
                { migrationNotice = "日志迁移尚未完成，原文件已保留。请在路径设置中重试。\n" + ex.Message; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            opening?.Close();
            MessageBox.Show("无法打开用户数据。请检查数据目录是否可用。\n\n" + ex.Message,
                "RimWise", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        try
        {
            DiagnosticEventLog.Current = new DiagnosticEventLog(UserStorage.DirectoryPath);
            DiagnosticEventLog.Current.Record("application", "startup", "profile-ready");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        InstallationMaintenance? maintenance = null;
        string? updateNotice = migrationNotice;
        if (!isSmokeTest)
        {
            try
            {
                var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                    .InformationalVersion.Split('+')[0];
                maintenance = new InstallationMaintenance(AppContext.BaseDirectory, UserStorage.DirectoryPath, version);
                var result = await Task.Run(maintenance.Prepare);
                DiagnosticEventLog.Current?.Record("update", "prepare",
                    $"version={version}; verified={result.Verified}; removed={result.Removed}; preserved={result.Preserved}; pending={result.Pending}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException)
            {
                maintenance = null;
                updateNotice = "未能完成升级检查，已保留未清理的文件。请关闭软件后重新运行安装包；ZIP 用户请清空程序目录后重新解压，保留另行存放的用户数据。\n\n" + ex.Message;
                DiagnosticEventLog.Current?.Record("update", "prepare-failed", ex.Message);
            }
        }
        UiDiagnosticEvents.Install(this);
        InterfaceMotion.Install();
        Themes = new ThemeService(this, new AppearanceSettingsStore(UserStorage.DirectoryPath));
        var window = new MainWindow(initializePaths: !isSmokeTest);
        window.ViewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == "Status") DiagnosticEventLog.Current?.Record("interface", "status-changed", window.ViewModel.Status);
            if (change.PropertyName == "ContentRevision") DiagnosticEventLog.Current?.Record("interface", "order-changed",
                $"revision={window.ViewModel.ContentRevision}; count={window.ViewModel.Rows.Count}");
        };
        MainWindow = window;
#if DEBUG
        if (e.Args.Length == 2 && e.Args[0] == "--interaction-smoke")
            InteractionMotionSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--steam-ui-smoke")
            SteamUiSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--diagnostic-export-smoke")
            DiagnosticExportSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--diagnostics-smoke")
            DiagnosticExportSmokeCheck.Attach(window, e.Args[1], focusedDiagnostics: true);
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
            PrototypeSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--marker-smoke")
            MarkerSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--ai-ui-smoke")
            AiUiSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--performance-smoke")
            StartupPerformanceSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--runtime-ui-smoke")
            RuntimeUiSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--ai-sorting-smoke")
            AiSortingSmokeCheck.Attach(window, e.Args[1]);
        if (e.Args.Length == 2 && e.Args[0] == "--message-dialog-smoke")
            MessageDialogSmokeCheck.Attach(window, e.Args[1]);
#endif
        window.Show();
        opening?.Close();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (!isSmokeTest) _ = MaintainTestStorageAsync(UserStorage.DirectoryPath);
        if (maintenance is not null)
        {
            try { await Task.Run(maintenance.MarkStartupSuccessful); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException)
            { DiagnosticEventLog.Current?.Record("update", "record-failed", ex.Message); }
        }
        if (updateNotice is not null && window.IsVisible)
            Views.MessageDialog.Show(window, updateNotice, "升级检查", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (UserStorage.Location?.StartupNotice is { } notice)
            _ = window.Dispatcher.BeginInvoke(() => Views.MessageDialog.Show(window, notice, "用户数据", MessageBoxButton.OK, MessageBoxImage.Information));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticEventLog.Current?.Record("app", "exit");
        DiagnosticEventLog.Current?.Dispose();
        Themes?.Dispose();
        UserStorage.Location?.Dispose();
        base.OnExit(e);
        GC.KeepAlive(installationMutex);
    }

    private static async Task MaintainTestStorageAsync(string profile)
    {
        try
        {
            var result = await Task.Run(() => TestDataRetention.MaintainProfile(profile));
            DiagnosticEventLog.Current?.Record("test-storage", "startup-cleanup",
                $"busy={result.Busy}; bytes={result.AfterBytes}; withinLimit={result.WithinLimit}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { DiagnosticEventLog.Current?.Record("test-storage", "startup-cleanup-failed", ex.Message); }
    }

    private static void WaitForPreviousInstance(string[] args)
    {
        if (args.Length != 3 || args[0] != "--resume-after" || !int.TryParse(args[1], out var pid)
            || !long.TryParse(args[2], out var ticks) || pid == Environment.ProcessId) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (previous.StartTime.ToUniversalTime().Ticks == ticks && !previous.WaitForExit(30000))
                throw new IOException("旧窗口尚未退出。请关闭后重新启动，迁移将在下次启动继续。");
        }
        catch (ArgumentException) { } // The previous process already exited.
    }
}
