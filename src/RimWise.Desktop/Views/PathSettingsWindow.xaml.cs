using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Steam;

namespace RimWise.Desktop.Views;

public partial class PathSettingsWindow : Window
{
    private readonly PathSettingsStore store;
    private readonly AutomaticSortingLogStore logStore;
    private readonly Func<string?>? moveBlocker;
    private readonly Func<string?>? logMoveBlocker;
    private readonly GamePaths previousPaths;
    private bool dirty;
    private bool suppressEdits;
    private bool detecting;
    private bool closed;
    private bool saving;
    private CancellationTokenSource? discoveryCancellation;
    public GamePaths? SavedPaths { get; private set; }
    public bool UserDataMoveRequested { get; private set; }

    public PathSettingsWindow(GamePaths current, PathSettingsStore store, Func<string?>? moveBlocker = null,
        Func<string?>? logMoveBlocker = null)
    {
        InitializeComponent();
        this.store = store;
        logStore = new(Path.GetDirectoryName(Path.GetDirectoryName(store.SettingsPath))!);
        LogDirectoryBox.Text = logStore.LoadSettings().Directory;
        this.moveBlocker = moveBlocker;
        this.logMoveBlocker = logMoveBlocker;
        previousPaths = current;
        UserDataPathBox.Text = UserStorage.DirectoryPath;
        SetPaths(current);
        foreach (var box in new[] { GamePathBox, LocalPathBox, WorkshopPathBox, ConfigPathBox, TargetVersionBox })
            box.TextChanged += (_, _) => { if (!suppressEdits) dirty = true; };
        Closed += (_, _) => { closed = true; discoveryCancellation?.Cancel(); };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
    }

    private GamePaths CurrentPaths() => new()
    {
        GameDirectory = GamePathBox.Text,
        LocalModsDirectory = LocalPathBox.Text,
        WorkshopDirectory = WorkshopPathBox.Text,
        ConfigDirectory = ConfigPathBox.Text,
        TargetVersionOverride = TargetVersionBox.Text
    };

    internal void SetPaths(GamePaths paths)
    {
        suppressEdits = true;
        GamePathBox.Text = paths.GameDirectory;
        LocalPathBox.Text = paths.LocalModsDirectory;
        WorkshopPathBox.Text = paths.WorkshopDirectory;
        ConfigPathBox.Text = paths.ConfigDirectory;
        TargetVersionBox.Text = paths.TargetVersionOverride;
        suppressEdits = false;
    }

    public async Task DetectAsync()
    {
        if (detecting || closed) return;
        if (dirty && MessageDialog.Show(this, "重新识别会替换窗口中尚未保存的路径，是否继续？", "重新识别",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        detecting = true;
        DetectButton.IsEnabled = false;
        SetEditingEnabled(false);
        discoveryCancellation = new CancellationTokenSource();
        DetectionStatus.Text = "正在识别 Steam 游戏库…";
        DetectionDetails.Visibility = Visibility.Collapsed;
        try
        {
            var token = discoveryCancellation.Token;
            var result = await Task.Run(() => SteamRootLocator.Discover(token), token);
            if (closed) return;
            InstallationsBox.ItemsSource = result.Installations;
            InstallationsPanel.Visibility = result.Installations.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (result.Installations.Count > 0)
            {
                InstallationsBox.SelectedIndex = 0;
                SetPaths(result.Installations[0].Paths with { TargetVersionOverride = TargetVersionBox.Text, ConfigDirectory = ConfigPathBox.Text });
                dirty = true;
                DetectionStatus.Text = result.Installations.Count == 1 ? "已找到 Steam 安装。保存后生效。" : "找到多个安装，请选择后保存。";
            }
            else DetectionStatus.Text = "未找到环世界，请检查识别详情或手动选择目录。";
            if (result.Warnings.Count > 0)
            {
                DetectionDetails.Visibility = Visibility.Visible;
                DetectionDetailsText.Text = string.Join("\n\n", result.Warnings);
            }
            else if (result.Installations.Count == 0)
            {
                DetectionDetails.Visibility = Visibility.Visible;
                DetectionDetailsText.Text = "Steam 未登记可用的环世界安装。请确认游戏已安装，或手动选择目录。";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (!closed) DetectionStatus.Text = "识别失败，请手动选择目录。";
        }
        finally
        {
            detecting = false;
            if (!closed) { DetectButton.IsEnabled = true; SetEditingEnabled(true); }
            discoveryCancellation?.Dispose();
            discoveryCancellation = null;
        }
    }

    private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private void SetEditingEnabled(bool enabled)
    {
        foreach (var box in new[] { GamePathBox, LocalPathBox, WorkshopPathBox, ConfigPathBox, TargetVersionBox }) box.IsEnabled = enabled;
    }

    private void Installation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (InstallationsBox.SelectedItem is SteamGameInstallation installation)
        { SetPaths(installation.Paths with { TargetVersionOverride = TargetVersionBox.Text, ConfigDirectory = ConfigPathBox.Text }); dirty = true; }
    }

    private string? PickDirectory(string title, string initial)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        var selected = PickDirectory("选择环世界游戏目录", GamePathBox.Text);
        if (selected is null) return;
        GamePathBox.Text = selected;
        var local = Path.Combine(selected, "Mods");
        if (string.IsNullOrWhiteSpace(LocalPathBox.Text) && Directory.Exists(local)) LocalPathBox.Text = local;
    }

    private void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        var selected = PickDirectory("选择本地模组目录", LocalPathBox.Text);
        if (selected is not null) LocalPathBox.Text = selected;
    }

    private void BrowseWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        var selected = PickDirectory("选择创意工坊模组目录", WorkshopPathBox.Text);
        if (selected is not null) WorkshopPathBox.Text = selected;
    }

    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        var selected = PickDirectory("选择包含 ModsConfig.xml 的 Config 目录", ConfigPathBox.Text);
        if (selected is not null) ConfigPathBox.Text = selected;
    }

    private void BrowseLogs_Click(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        var selected = PickDirectory("选择自动排序日志保存目录", LogDirectoryBox.Text);
        if (selected is not null) LogDirectoryBox.Text = selected;
    }

    internal LogDirectoryMoveResult SaveLogDirectory()
        => logStore.ChangeDirectory(LogDirectoryBox.Text);

    private void BrowseUserData_Click(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        var selected = PickDirectory("选择空文件夹存放 RimWise 用户数据", UserDataPathBox.Text);
        if (selected is not null) UserDataPathBox.Text = selected;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (detecting || saving) { DetectionStatus.Text = "请等待当前操作完成后保存。"; return; }
        try
        {
            var paths = GamePathValidation.Validate(CurrentPaths());
            var logDirectory = logStore.ValidateDirectory(LogDirectoryBox.Text);
            var moving = !string.Equals(Path.GetFullPath(UserDataPathBox.Text).TrimEnd('\\'),
                Path.GetFullPath(UserStorage.DirectoryPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(logDirectory, logStore.LoadSettings().Directory, StringComparison.OrdinalIgnoreCase)
                && logMoveBlocker?.Invoke() is { } logReason) throw new IOException(logReason);
            var location = UserStorage.Location;
            var destination = UserDataPathBox.Text;
            if (moving)
            {
                new AutomaticSortingLogStore(UserDataPathBox.Text).ValidateDirectory(logDirectory);
                if (moveBlocker?.Invoke() is { } reason) throw new IOException(reason);
                if (location is null) throw new IOException("测试模式不能迁移真实用户数据。");
            }
            saving = true;
            ((UIElement)Content).IsEnabled = false;
            DetectionStatus.Text = "正在保存路径并迁移日志，请稍候…";
            var result = await Task.Run(() =>
            {
                if (moving) location!.PrepareMove(destination, new[] { paths.GameDirectory, paths.LocalModsDirectory,
                    paths.WorkshopDirectory, paths.ConfigDirectory, previousPaths.GameDirectory,
                    previousPaths.LocalModsDirectory, previousPaths.WorkshopDirectory, previousPaths.ConfigDirectory });
                try { var moved = logStore.ChangeDirectory(logDirectory); store.Save(paths); return moved; }
                catch { if (moving) location!.CancelMove(); throw; }
            });
            saving = false;
            ((UIElement)Content).IsEnabled = true;
            if (result.Preserved > 0 || result.CleanupPending)
                MessageDialog.Show(this, "已保存日志路径。" + (result.CleanupPending ? "部分原日志被占用或已变化，已保留，下次启动重试清理。" : "")
                    + (result.Preserved > 0 ? $"{result.Preserved} 个文件无法确认为完整的 RimWise 日志，已保留在原目录。" : ""),
                    "日志迁移", MessageBoxButton.OK, MessageBoxImage.Information);
            UserDataMoveRequested = moving;
            SavedPaths = paths;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        {
            MessageDialog.Show(this, ex.Message, "无法保存路径", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { saving = false; ((UIElement)Content).IsEnabled = true; }
    }
}
