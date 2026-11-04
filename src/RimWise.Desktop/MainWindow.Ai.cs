using System.IO;
using System.Text.Json;
using System.Windows;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;
using RimWise.Desktop.Services;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private readonly AiSettingsStore aiSettingsStore = new(UserStorage.DirectoryPath);
    private readonly AiTokenEstimator aiTokenEstimator = new(UserStorage.DirectoryPath);
    private readonly ISecretStore aiSecrets = new LocalEncryptedSecretStore(UserStorage.DirectoryPath);
    private AiSortingWindow? aiSortingWindow;

    private void AiSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = aiSettingsStore.Load();
            var dialog = new AiSettingsWindow(current, aiSettingsStore, aiSecrets) { Owner = this };
            if (dialog.ShowDialog() == true) ViewModel.Status = "AI 设置已保存";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or PlatformNotSupportedException)
        { MessageDialog.Show(this, ex.Message, "无法打开 AI 设置", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void AiUsage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentCatalogue is null || !ViewModel.HasDocument || ViewModel.IsShowingCache || ViewModel.IsScanning)
        { ViewModel.Status = "请载入排序并等待刷新完成后再预估 AI 用量"; return; }
        try
        {
            var request = AiSortingSession.CreateRequest(ViewModel, aiSettingsStore.Load(), null);
            new AiUsageWindow(aiTokenEstimator.Estimate(request)) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { ViewModel.Status = "无法预估 AI 用量：" + ex.Message; }
    }

    private void AiSorting_Click(object sender, RoutedEventArgs e) => OpenAiSorting();

    internal AiSortingWindow OpenAiSorting()
    {
        if (aiSortingWindow is not null)
        {
            if (aiSortingWindow.WindowState == WindowState.Minimized) aiSortingWindow.WindowState = WindowState.Normal;
            aiSortingWindow.Activate();
            return aiSortingWindow;
        }
        AutomaticSortingNotice.ShowIfNeeded(this, UserStorage.DirectoryPath);
        var window = new AiSortingWindow(ViewModel, UserStorage.DirectoryPath, lifetime.Token, gamePaths: () => paths,
            prepareSave: PrepareAutomaticOrderSave, prepareCatalogue: PrepareAutomaticCatalogueAsync) { Owner = this };
        aiSortingWindow = window;
        window.Closed += (_, _) => aiSortingWindow = null;
        window.Show();
        return window;
    }
}
