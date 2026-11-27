using System.IO;
using System.Text.Json;
using System.Windows;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;
using RimWise.Desktop.Services;
using RimWise.Integration.Diagnostics;

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
            if (dialog.ShowDialog() == true)
            {
                ViewModel.Status = "AI 设置已保存";
                aiSortingWindow?.RefreshConfiguration();
            }
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

    private void AiSortingTab_Click(object sender, RoutedEventArgs e)
    {
        if (!SortingGuidePreferences.ShouldAsk(UserStorage.DirectoryPath)) return;
        var choice = MessageDialog.Show(this,
            "您好像是第一次使用自动排序，需要教程吗？\n\n教程会演示操作流程，不会调用 AI、启动游戏或更改当前排序。之后也可以在“帮助”中重新查看。",
            "自动排序教程", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.Yes, yesText: "需要", noText: "不需要");
        if (choice == MessageBoxResult.Yes) StartSortingGuide();
        else if (choice == MessageBoxResult.No) SaveSortingGuidePreference(SortingGuidePreferences.MarkDeclined, "declined");
    }

    private void SortingGuide_Click(object sender, RoutedEventArgs e)
    {
        if (aiSortingWindow?.IsWorking == true) { ViewModel.Status = "正在跑自动排序，等跑完再开教程。"; return; }
        StartSortingGuide();
    }

    private void StartSortingGuide()
    {
        if (SortingTutorialHost.Content is not SortingGuideView guide)
        {
            guide = new SortingGuideView();
            SortingTutorialHost.Content = guide;
            guide.Finished += (_, _) =>
            {
                SortingTutorialHost.Visibility = Visibility.Collapsed;
                HelpMenu.Focus();
                if (guide.IsCompleted) SaveSortingGuidePreference(SortingGuidePreferences.MarkCompleted, "completed");
            };
        }
        SortingTutorialHost.Visibility = Visibility.Visible;
        guide.Restart();
    }

    private static void SaveSortingGuidePreference(Action<string> save, string state)
    {
        try { save(UserStorage.DirectoryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticEventLog.Current?.Record("sorting-guide", "preference-save-failed", state + ": " + ex.Message); }
    }

    internal AiSortingWindow OpenAiSorting()
    {
        AiSortingPageTab.IsChecked = true;
        return EnsureAiSortingPage();
    }

    private AiSortingWindow EnsureAiSortingPage()
    {
        if (aiSortingWindow is not null) { aiSortingWindow.RefreshConfiguration(); return aiSortingWindow; }
        AutomaticSortingNotice.ShowIfNeeded(this, UserStorage.DirectoryPath);
        var window = new AiSortingWindow(ViewModel, UserStorage.DirectoryPath, lifetime.Token, gamePaths: () => paths,
            prepareSave: PrepareAutomaticOrderSave, prepareCatalogue: PrepareAutomaticCatalogueAsync);
        window.OpenReadIssues = () =>
        {
            ModsPageTab.IsChecked = true;
            ScanIssuesExpander.IsExpanded = true;
            ScanIssuesExpander.BringIntoView();
            ScanIssuesExpander.Focus();
        };
        aiSortingWindow = window;
        AiSortingHost.Content = window.Embed(this, () => ModsPageTab.IsChecked = true);
        return window;
    }
}
