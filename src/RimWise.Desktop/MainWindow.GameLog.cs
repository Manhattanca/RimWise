using System.IO;
using System.Windows;
using RimWise.Desktop.Views;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private GameLogPage? gameLogPage;

    private void MainPage_Changed(object sender, RoutedEventArgs e)
    {
        if (GameLogHost is null || ModEditorFooter is null) return;
        var logs = LogsPageTab.IsChecked == true;
        if (logs && gameLogPage is null)
            GameLogHost.Content = gameLogPage = new GameLogPage(LocateGameLogs, () => ViewModel.CurrentCatalogue);
        ModEditorBody.Visibility = ModEditorFooter.Visibility = logs ? Visibility.Collapsed : Visibility.Visible;
        GameLogHost.Visibility = logs ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) InterfaceMotion.SlidePage(logs ? GameLogHost : ModEditorBody, fromRight: logs);
    }

    private (string Startup, string Runtime) LocateGameLogs()
    {
        var config = CurrentOrderLocation.Resolve(paths);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(config))
            ?? throw new IOException("请在路径设置中指定游戏配置目录。");
        var startup = Path.Combine(root, "Player.log");
        if (!File.Exists(startup) && !string.IsNullOrWhiteSpace(paths.GameDirectory))
        {
            foreach (var folder in new[] { "RimWorldWin64_Data", "RimWorldWin_Data", "RimWorld_Data" })
            {
                var legacy = Path.Combine(paths.GameDirectory, folder, "output_log.txt");
                if (File.Exists(legacy)) { startup = legacy; break; }
            }
        }
        var runtime = Path.Combine(root, "Logs", "Player.log");
        // Many installations use one Unity Player log throughout startup and play.
        // Keep runtime reading useful without presenting the previous session as current.
        return (startup, File.Exists(runtime) ? runtime : startup);
    }
}
