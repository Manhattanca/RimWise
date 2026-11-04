#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Desktop.Views;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class DiagnosticsSmokeCheck
{
    internal static async Task RunAsync(MainWindow window, string output)
    {
        var vm = window.ViewModel;
        var themes = ((App)Application.Current).Themes;
        var originalTheme = themes.Preference;
        var order = vm.CreateExportDocument();
        var originalRules = vm.CustomRules;
        try
        {
            window.UpdateLayout();
            var description = window.ModDescription.TranslatePoint(new Point(), window.ModDetails).Y;
            var location = window.ModLocationSection.TranslatePoint(new Point(), window.ModDetails).Y;
            var compatibility = window.ModCompatibilitySection.TranslatePoint(new Point(), window.ModDetails).Y;
            if (!(description < location && location < compatibility))
                throw new InvalidOperationException("Mod description/location/compatibility sections are out of order.");

            var first = vm.Rows[0];
            var second = vm.Rows[1];
            vm.SetCustomRules([new(second.PackageId, first.PackageId)]);
            var shared = vm.Diagnostics.Single(row => row.Diagnostic.Code == "CustomRuleViolated");
            if (shared.Diagnostic.AffectedIdentifiers.Count() != 2
                || !first.DiagnosticTooltip.Contains("自定义规则") || !second.DiagnosticTooltip.Contains("自定义规则"))
                throw new InvalidOperationException("Shared rule must count once and mark both affected mods.");
            var sharedDialog = window.OpenIssues(DiagnosticSeverity.Warning);
            sharedDialog.IssuesGrid.SelectedItem = shared;
            vm.SelectedRow = first;
            sharedDialog.LocateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (vm.SelectedRow != second) throw new InvalidOperationException("Shared issue did not locate its second endpoint.");
            sharedDialog.LocateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (vm.SelectedRow != first) throw new InvalidOperationException("Shared issue did not cycle through its endpoints.");
            sharedDialog.Close();
            vm.SetCustomRules(originalRules);

            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                themes.Select(theme, save: false);
                await CheckCompactIcons(window, output, theme);
                vm.SearchText = "此搜索隐藏所有模组";
                // Raise the actual summary button click: checks its event wiring too.
                window.WarningsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Idle(window);
                var dialog = window.OwnedWindows.OfType<OrderIssuesWindow>().Single();
                if (dialog.IssuesGrid.Items.Count != vm.WarningCount
                    || dialog.IssuesGrid.Items.Cast<DiagnosticRow>().Any(row => row.Diagnostic.Severity != DiagnosticSeverity.Warning))
                    throw new InvalidOperationException("Warning summary did not show the warning mods.");
                var warning = dialog.IssuesGrid.Items.Cast<DiagnosticRow>().First(row => row.Identifier.Length > 0);
                dialog.IssuesGrid.SelectedItem = warning;
                dialog.LocateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Idle(window);
                if (vm.SearchText.Length != 0 || !warning.Diagnostic.AffectedIdentifiers.Contains(vm.SelectedRow?.PackageId))
                    throw new InvalidOperationException("Issue location did not clear search and select the current mod.");

                var area = Descendants<Border>(window.OrderGrid).Single(item => item.Name == "DiagnosticHitArea" && item.DataContext == vm.SelectedRow);
                if (area.Background is null || area.ActualWidth < 18 || area.ActualHeight < 18
                    || area.InputHitTest(new Point(area.ActualWidth / 2, area.ActualHeight / 2)) != area)
                    throw new InvalidOperationException("Diagnostic icon has no reliable hit target.");
                if (area.ToolTip is not ToolTip tooltip) throw new InvalidOperationException("Diagnostic tooltip missing.");
                tooltip.PlacementTarget = area;
                tooltip.IsOpen = true;
                await Idle(window);
                if (tooltip.Content is not TextBlock text || text.Text != vm.SelectedRow!.DiagnosticTooltip
                    || tooltip.ActualWidth <= 0 || text.Text.Length == 0)
                    throw new InvalidOperationException("Diagnostic tooltip did not render the current reasons.");
                PrototypeSmokeCheck.SavePreview(tooltip, Path.Combine(output, theme + "警告悬停.png"));
                tooltip.IsOpen = false;
                PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, theme + "警告清单.png"));
                window.ErrorsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Idle(window);
                if (dialog.IssuesGrid.Items.Count != vm.ErrorCount
                    || dialog.IssuesGrid.Items.Cast<DiagnosticRow>().Any(row => row.Diagnostic.Severity != DiagnosticSeverity.Error))
                    throw new InvalidOperationException("Error summary did not show the error mods.");
                PrototypeSmokeCheck.SavePreview(dialog, Path.Combine(output, theme + "错误清单.png"));
                vm.Load(order.WithOrder(new LoadOrder([])), "空列表.xml");
                await Idle(window);
                if (dialog.IssuesGrid.Items.Count != 0 || dialog.EmptyNotice.Visibility != Visibility.Visible || dialog.LocateButton.IsEnabled)
                    throw new InvalidOperationException("Open issue window retained obsolete entries after import.");
                vm.Load(order, "ModsConfig.xml");
                await Idle(window);
                if (dialog.IssuesGrid.Items.Count != vm.ErrorCount)
                    throw new InvalidOperationException("Open issue window did not follow refreshed diagnostics.");
                dialog.Close();
            }
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.OfType<OrderIssuesWindow>().ToArray()) dialog.Close();
            themes.Select(originalTheme, save: false);
            vm.SetCustomRules(originalRules);
            vm.Load(order, "ModsConfig.xml");
            vm.SelectedRow = vm.Rows.FirstOrDefault(row => row.PackageId == "example.buildings");
        }
    }

    private static async Task CheckCompactIcons(MainWindow window, string output, ThemePreference theme)
    {
        var originalHeight = window.OrderGrid.RowHeight;
        var rows = window.ViewModel.Rows.Where(row => row.HasError || row.HasWarning).Take(3).ToArray();
        if (!rows.Any(row => row.HasError) || !rows.Any(row => row.HasWarning))
            throw new InvalidOperationException("Icon check requires both severities.");
        var result = new TrialExclusionWindow(rows.Select(row => new TrialRemovalRow(row, "合成移出项")).ToArray(), true) { Owner = window };
        result.Show();
        try
        {
            foreach (var height in new[] { 28d, 29d, 32d, 35.5d, 38d, 64d })
            {
                window.SetModRowHeight(height, save: false);
                await Idle(window);
                await Idle(result);
                foreach (var grid in new[] { window.OrderGrid, result.ModsList })
                {
                    var icons = Descendants<ContentControl>(grid).Where(control => control.IsVisible
                        && (control.ContentTemplate == window.FindResource("ErrorIcon")
                            || control.ContentTemplate == window.FindResource("WarningIcon"))).ToArray();
                    if (icons.Length == 0) throw new InvalidOperationException("No visible diagnostic icons.");
                    foreach (var icon in icons)
                    {
                        var cell = Ancestor<DataGridCell>(icon) ?? throw new InvalidOperationException("Icon cell missing.");
                        var area = Ancestor<Border>(icon) ?? throw new InvalidOperationException("Icon hit area missing.");
                        var bounds = area.TransformToAncestor(cell).TransformBounds(new Rect(area.RenderSize));
                        if (bounds.Left < -0.1 || bounds.Top < -0.1 || bounds.Right > cell.ActualWidth + 0.1 || bounds.Bottom > cell.ActualHeight + 0.1)
                            throw new InvalidOperationException($"Diagnostic hit area clipped at height {height}: {bounds}, cell {cell.RenderSize}.");
                    }
                }
                if (height is 28 or 38)
                {
                    PrototypeSmokeCheck.SavePreview(window.OrderGrid, Path.Combine(output, $"{theme}-row-icons-{height}.png"));
                    PrototypeSmokeCheck.SavePreview(result, Path.Combine(output, $"{theme}-result-icons-{height}.png"));
                }
            }
        }
        finally { result.Close(); window.SetModRowHeight(originalHeight, save: false); }
    }

    private static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T found) return found;
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
#endif
