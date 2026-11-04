#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Core;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

internal static class AutomaticSortingPreferencesSmokeCheck
{
    internal static async Task Run(MainWindow owner, string output)
    {
        owner.LoadSample();
        owner.ViewModel.MarkExported();
        var root = Path.Combine(output, "首次提醒与限制 中文");
        var watch = Stopwatch.StartNew();
        var earlyChecked = false;
        var dialogs = 0;
        string? failure = null;
        MessageDialogWindow? observed = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var dialog = owner.OwnedWindows.OfType<MessageDialogWindow>().FirstOrDefault();
            if (dialog is null) return;
            if (!ReferenceEquals(observed, dialog)) { observed = dialog; dialogs++; }
            var button = dialog.ButtonsPanel.Children.OfType<Button>().Single();
            if (!earlyChecked)
            {
                earlyChecked = true;
                if (button.IsEnabled || dialog.CloseButton.IsEnabled) failure = "Countdown was bypassable";
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                dialog.Close();
                if (!dialog.IsVisible) failure = "Early click or close dismissed notice";
            }
            if (button.IsEnabled)
            {
                if (watch.Elapsed < TimeSpan.FromSeconds(3)) failure = "Notice enabled too early";
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
        };
        timer.Start();
        try
        {
            AutomaticSortingNotice.ShowIfNeeded(owner, root);
            Assert(earlyChecked && failure is null && dialogs == 1, failure ?? "Notice not shown");
            AutomaticSortingNotice.ShowIfNeeded(owner, root);
            Assert(dialogs == 1 && new AutomaticSortingPreferencesStore(root).Load().NoticeAcknowledged,
                "Acknowledgement did not persist");
        }
        finally { timer.Stop(); }

        var sorting = new AiSortingWindow(owner.ViewModel, root) { Owner = owner };
        try
        {
            sorting.Show();
            Assert(((ComboBoxItem)sorting.PermissionBox.Items[0]).Tag as string == "insert"
                && sorting.PermissionBox.SelectedIndex == 0 && sorting.ConfirmRemovalToggle.IsChecked == true,
                "Insertion must be the first default choice with removal confirmation");
            Assert(((ComboBoxItem)sorting.PermissionBox.Items[0]).Content.ToString() == "尽量保留现有排序，为新增 Mod 寻找位置"
                && ((ComboBoxItem)sorting.PermissionBox.Items[1]).Content.ToString() == "AI 全部重排序",
                "Sorting mode labels do not explain the scope");
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            foreach (var compact in new[] { false, true })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                sorting.Width = compact ? sorting.MinWidth : 820;
                sorting.Height = compact ? sorting.MinHeight : 820;
                sorting.SettingsScroll.ScrollToTop();
                await sorting.Dispatcher.InvokeAsync(sorting.UpdateLayout, DispatcherPriority.ApplicationIdle);
                if (!compact)
                    Assert(sorting.SettingsScroll.ScrollableHeight < 1, "Default settings view clips controls");
                sorting.SettingsScroll.ScrollToBottom();
                await sorting.Dispatcher.InvokeAsync(sorting.UpdateLayout, DispatcherPriority.ApplicationIdle);
                foreach (var input in new FrameworkElement[] { sorting.AttemptsInput, sorting.BudgetInput })
                {
                    var bounds = input.TransformToAncestor(sorting.SettingsScroll).TransformBounds(new Rect(input.RenderSize));
                    Assert(bounds.Top >= -0.5 && bounds.Bottom <= sorting.SettingsScroll.ViewportHeight + 0.5,
                        "Limit input is only partially visible: " + input.Name);
                }
                var settingsBounds = sorting.SettingsScroll.TransformToAncestor(sorting).TransformBounds(new Rect(sorting.SettingsScroll.RenderSize));
                var logBounds = sorting.LogSettingsPanel.TransformToAncestor(sorting).TransformBounds(new Rect(sorting.LogSettingsPanel.RenderSize));
                Assert(settingsBounds.Bottom <= logBounds.Top && sorting.SettingsScroll.ClipToBounds,
                    "Settings overlap log actions");
                PrototypeSmokeCheck.SavePreview(sorting, Path.Combine(output, $"{theme}-排序模式-{(compact ? "最小" : "默认")}.png"));
            }
            sorting.PermissionBox.SelectedIndex = 1;
            sorting.ConfirmRemovalToggle.IsChecked = false;
            sorting.ConfirmRemovalToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            sorting.RefreshConfiguration();
            Assert(sorting.PermissionBox.SelectedIndex == 1 && !sorting.ReadMaintenanceLimits().ConfirmModRemoval,
                "Mode/removal choice lost on activation");
            sorting.PermissionBox.SelectedIndex = 0;
            Assert(!sorting.ReadMaintenanceLimits().AttemptLimitEnabled && !sorting.ReadMaintenanceLimits().TokenLimitEnabled,
                "New task limits must default off");
            sorting.AttemptsInput.Text = "ignored while disabled";
            sorting.BudgetInput.Text = "ignored while disabled";
            sorting.ReadMaintenanceLimits();
            sorting.AttemptLimitToggle.IsChecked = true;
            sorting.AttemptsInput.Text = "27";
            var limits = sorting.ReadMaintenanceLimits();
            Assert(limits.AttemptLimitEnabled && limits.MaximumAttempts == 27 && !limits.TokenLimitEnabled, "Independent attempt switch");
            new AutomaticSortingPreferencesStore(root).SaveLimits(limits);
            var saved = new AutomaticSortingPreferencesStore(root).Load();
            Assert(saved.NoticeAcknowledged && saved.AttemptLimitEnabled && !saved.TokenLimitEnabled, "Preferences lost acknowledgement");
        }
        finally { sorting.Close(); }

        ModInstallation Fixture(string id, string name, ModSource source = ModSource.Local) =>
            new(Path.Combine(root, "Mods", id), id, source, "", new(name, "Test author", id, ["1.7"], "",
                ModRequirements.Empty, false), []);
        var fixtures = owner.ViewModel.Rows.Select(row => Fixture(row.PackageId, row.Name)).Concat(new[]
        {
            Fixture("insertion.example.one", "新增模组一"), Fixture("insertion.example.two", "新增模组二", ModSource.Workshop),
            Fixture("insertion.example.three", "测试模组三"), Fixture("insertion.example.four", "A long example mod name for clipping checks")
        }).ToArray();
        owner.ViewModel.ApplyScan(new ModScanResult(new(null, "", ""), GameVersion.Parse("1.7"), new(fixtures), []));
        Assert(owner.ViewModel.AvailableMods.Count == 4, "Insertion fixture not available");
        var picker = new InsertionPickerWindow(owner.ViewModel) { Owner = owner };
        try
        {
            picker.Show();
            await owner.Dispatcher.InvokeAsync(() => picker.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            Assert(picker.ModsList.Items.Count == owner.ViewModel.AvailableMods.Count, "Picker lost inactive mods");
            Assert(picker.ConfirmButton.IsEnabled && picker.CountText.Text.Contains("无需添加"),
                "Empty picker still requires adding a mod");
            if (picker.ModsList.Items.Count > 1)
            {
                picker.ModsList.SelectedItems.Add(picker.ModsList.Items[0]);
                picker.ModsList.SelectedItems.Add(picker.ModsList.Items[1]);
                var before = picker.SelectedList.Items.Count;
                picker.MoveSelection(true);
                Assert(picker.ConfirmButton.IsEnabled && picker.SelectedList.Items.Count == before + 2,
                    $"Picker multi-select failed: before={before}, after={picker.SelectedList.Items.Count}, left={picker.ModsList.Items.Count}, selection={picker.ModsList.SelectedItems.Count}");
                picker.SelectedList.SelectedItems.Add(picker.SelectedList.Items[0]);
                picker.MoveSelection(false);
                Assert(picker.SelectedList.Items.Count == before + 1, "Picker remove failed");
            }
            foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                picker.Width = picker.MinWidth;
                await picker.Dispatcher.InvokeAsync(picker.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Assert(picker.ModsList.ActualWidth > 200 && picker.SelectedList.ActualWidth > 200, "Picker columns clipped");
                foreach (var grid in new[] { picker.ModsList, picker.SelectedList })
                {
                    grid.UnselectAll();
                    grid.UpdateLayout();
                    for (var index = 0; index < grid.Items.Count; index++)
                    {
                        var row = grid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
                        Assert(row is not null, "Picker row was not realized");
                        var expected = (SolidColorBrush)picker.FindResource(index % 2 == 0 ? "PanelBrush" : "AlternateBrush");
                        Assert(row!.Background is SolidColorBrush actual && actual.Color == expected.Color,
                            $"{theme} picker row {index} has a system background instead of its theme");
                    }
                    grid.SelectAll();
                }
                picker.SearchBox.Focus();
                await picker.Dispatcher.InvokeAsync(picker.UpdateLayout, DispatcherPriority.ApplicationIdle);
                PrototypeSmokeCheck.SavePreview(picker, Path.Combine(output, $"{theme}-新增模组双栏.png"));
                picker.ModsList.UnselectAll();
                picker.SelectedList.UnselectAll();
                await picker.Dispatcher.InvokeAsync(picker.UpdateLayout, DispatcherPriority.ApplicationIdle);
                PrototypeSmokeCheck.SavePreview(picker, Path.Combine(output, $"{theme}-新增模组双栏未选中.png"));
            }
        }
        finally { picker.Close(); }

        var emptyPicker = new InsertionPickerWindow(owner.ViewModel) { Owner = owner };
        while (emptyPicker.SelectedList.Items.Count > 0)
        {
            emptyPicker.SelectedList.SelectAll();
            emptyPicker.MoveSelection(false);
        }
        _ = emptyPicker.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => emptyPicker.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))));
        Assert(emptyPicker.ShowDialog() == true && emptyPicker.SelectedIdentifiers.Length == 0,
            "Empty selection cannot confirm sorting the current list");

        var original = owner.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        var newRows = owner.ViewModel.AvailableMods.Take(2).ToArray();
        if (newRows.Length == 2)
        {
            owner.ViewModel.MarkExported();
            Assert(owner.ViewModel.AddAvailableRows(newRows, 0), "Could not seed unsaved additions");
            var pending = owner.ViewModel.PendingInsertionMods();
            Assert(pending.Length == 2, "Unsaved additions were treated as baseline");
            var seeded = new InsertionPickerWindow(owner.ViewModel) { Owner = owner };
            Assert(seeded.SelectedList.Items.Count == 2, "Unsaved additions not preselected");
            seeded.Close();
            var baseline = owner.ViewModel.ApplyInsertionSelection(pending.Select(row => row.PackageId));
            Assert(InsertionScope.Read(baseline).SequenceEqual(original), "Existing order changed");
            owner.ViewModel.RecordGameSave(() => { }, () => { });
            Assert(owner.ViewModel.PendingInsertionMods().Length == 0, "Saved additions still pending");
            owner.ViewModel.UndoCommand.Execute(null);
            Assert(owner.ViewModel.PendingInsertionMods().Length == 2, "Undo save lost pending additions");
            owner.ViewModel.RedoCommand.Execute(null);
            Assert(owner.ViewModel.PendingInsertionMods().Length == 0, "Redo save lost saved baseline");
        }

        var client = new WaitingClient();
        var settings = new AiSettingsWindow(AiSettings.Default with { Enabled = true, Model = "fixture" },
            new AiSettingsStore(root), new EmptySecrets(), client) { Owner = owner };
        try
        {
            settings.Show();
            var task = settings.TestConnectionAsync();
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var dispatcherResponded = false;
            await owner.Dispatcher.InvokeAsync(() => dispatcherResponded = true, DispatcherPriority.Background);
            Assert(dispatcherResponded && !task.IsCompleted, "Connection test blocked UI");
            client.Release.TrySetResult(new(true, "Synthetic connection succeeded"));
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(settings.StatusText.Text.Contains("Synthetic connection succeeded"), "Connection result missing");
        }
        finally { client.Release.TrySetResult(new(true, "cleanup")); settings.Close(); }
        File.WriteAllText(Path.Combine(output, "preferences-checks.txt"),
            "Passed: insertion-first selector, persistent mode and removal confirmation, multi-select picker, 3-second notice, early close and click rejection, persistent acknowledgement, independent limits, connection responsiveness. No network calls.");
    }

    private sealed class EmptySecrets : ISecretStore
    {
        public string? Read(string name) => null;
        public void Write(string name, string value) => throw new NotSupportedException();
        public void Delete(string name) => throw new NotSupportedException();
    }
    private sealed class WaitingClient : IAiClient
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<AiConnectionResult> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? apiKey, CancellationToken token)
        { Entered.TrySetResult(); return Release.Task.WaitAsync(token); }
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token) => throw new NotSupportedException();
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
#endif
