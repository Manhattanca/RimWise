using System.Windows;
using System.Windows.Controls;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    private Window? embeddedOwner;
    private Action? returnToEditor;
    private Window DialogOwner => embeddedOwner ?? this;

    // Keep the established sorting controller and its recovery/save transaction intact.
    // Embedded mode never calls Show/EnsureHandle: only the view belongs to the main panel.
    // Modal ownership, shutdown and editor subscriptions are explicitly managed by the host.
    internal FrameworkElement Embed(Window owner, Action navigateBack)
    {
        if (embeddedOwner is not null || IsVisible || closed)
            throw new InvalidOperationException("自动排序界面已被使用。");
        var view = (Border)Content;
        view.Background = System.Windows.Media.Brushes.Transparent;
        Content = null;
        embeddedOwner = owner;
        returnToEditor = navigateBack;
        view.Padding = new Thickness(0);
        // Wide embedded settings need two rows, not a dialog-sized empty region.
        // Keep a bounded scroll viewport at smaller sizes so actions stay on screen.
        SettingsRow.MaxHeight = SettingsScroll.MaxHeight = 158;
        StatusScroll.MaxHeight = 60;
        SettingsLayout.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        SettingsLayout.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumnSpan(ConfigurationText, 2);
        Grid.SetColumn(AiActionsPanel, 1); Grid.SetRow(AiActionsPanel, 1);
        Grid.SetColumn(LimitPanel, 1); Grid.SetRow(LimitPanel, 2);
        AiActionsPanel.Margin = new Thickness(16, 12, 0, 0);
        LimitPanel.Margin = new Thickness(16, 4, 0, 0);
        ModePanel.VerticalAlignment = VerticalAlignment.Top;
        CloseButton.Content = "返回模组排序";
        return view;
    }

    internal bool PrepareHostClose()
    {
        if (!IsWorking) return true;
        requestCancellation?.Cancel();
        StatusText.Text = "正在停止任务或完成日志保存；清理完成后请再次关闭主窗口。";
        RefreshButtons();
        return false;
    }

    internal void DisposeEmbedded()
    {
        if (embeddedOwner is null || closed) return;
        EndLifetime();
        usageWindow?.Close();
        embeddedOwner = null;
        returnToEditor = null;
        Close();
    }

    private void EndLifetime()
    {
        if (closed) return;
        closed = true;
        SortingProgress.IsIndeterminate = false;
        requestCancellation?.Cancel();
        editor.PropertyChanged -= Editor_Changed;
    }
}
