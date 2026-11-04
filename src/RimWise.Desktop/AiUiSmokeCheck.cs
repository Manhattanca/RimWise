#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

/// <summary>Uses synthetic settings and an in-memory client; never contacts a real AI service.</summary>
internal static class AiUiSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            try
            {
                await RunAsync(window, output);
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                window.ViewModel.MarkExported();
                Application.Current.Shutdown(1);
            }
        };
    }

    private static async Task RunAsync(MainWindow window, string output)
    {
        window.LoadSample();
        OrderImportDialogSmokeCheck.Run(window, output);
        await AutomaticSortingPreferencesSmokeCheck.Run(window, output);
        await AutomaticLogSmokeCheck.Run(window, output);
        await AiExecutionSmokeCheck.RunAsync(window, output);
        await AvailableSortingSmokeCheck.RunAsync(window, output);
        await ListDisplaySmokeCheck.RunAsync(window, output);
        HistoryAndSaveSmokeCheck.CheckDirectSaveWithoutChanges(window, output);
        await VersionCompatibilitySmokeCheck.RunAsync(window, output);
        await AiConfigurationSmokeCheck.Run(window, output);
        var client = new FakeClient();
        var store = new AiSettingsStore(Path.Combine(output, "配置 中文 & [test]"));
        var settings = new AiSettingsWindow(AiSettings.Default with { Model = "sample-model" }, store, new EmptySecrets(), client) { Owner = window };
        settings.Show();
        try
        {
            Assert(settings.FindName("NetworkBox") is null, "Network checkbox still exists.");
            Assert((string)settings.TestConnectionButton.Content == "测试 AI 连接", "Connection test button missing.");
            Assert(settings.ImportConnection("{\"url\":\"https://example.invalid\",\"key\":\"synthetic-import-secret\",\"model\":\"sample-model\"}"), "Connection import failed.");
            Assert(settings.ApiKeyBox.Password == "synthetic-import-secret" && settings.EndpointBox.Text == "https://example.invalid/v1/chat/completions"
                && client.Count == 0 && !File.Exists(store.SettingsPath), "Import sent request or saved prematurely.");
            var importedAddress = settings.EndpointBox.Text;
            Assert(!settings.ImportConnection("{\"url\":\"https://other.invalid\"}") && settings.EndpointBox.Text == importedAddress,
                "Address-only import inherited an unrelated saved key.");
            Assert(!settings.ImportConnection("{\"key\":\"secret-one\",\"api_key\":\"secret-two\"}")
                && settings.ApiKeyBox.Password == "synthetic-import-secret", "Failed import partially updated fields.");
            await settings.TestConnectionAsync();
            Assert(client.Count == 1 && settings.StatusText.Text == "连接与鉴权检查通过。未调用模型生成。" && !File.Exists(store.SettingsPath),
                "Unsaved/disabled connection check failed or saved settings unexpectedly.");
            Assert(store.Tasks.Inspect().Count == 0, "Connection check created a paid/recoverable task.");
            string oldId;
            using (var old = store.Tasks.Create(new(AiTaskKind.Connection, AiTaskPhase.RequestPending,
                new string('A', 64), [], [], DateTimeOffset.UtcNow) { Requests = 1, UnknownUsage = true })) oldId = old.Id;
            await settings.TestConnectionAsync();
            Assert(client.Count == 2 && settings.StatusText.Text.Contains("检查通过"), "Old unknown billing blocked connection check.");
            store.Tasks.Discard(oldId);
            await settings.TestConnectionAsync();
            Assert(client.Count == 3 && store.Tasks.Inspect().Count == 0 && store.Tasks.InspectDeletedUsage().Single().UnknownUsage,
                "Connection check changed billing evidence or was blocked by deleted task.");

            // Deliberately synchronous HTTP setup: the dispatcher must still process a heartbeat.
            using var workerStarted = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            client.Response = token => { workerStarted.Set(); releaseWorker.Wait(token); return Task.FromResult(GoodConnection()); };
            var running = settings.TestConnectionAsync();
            await Task.Run(() => Assert(workerStarted.Wait(TimeSpan.FromSeconds(5)), "Worker did not start."));
            var heartbeat = false;
            await settings.Dispatcher.InvokeAsync(() => heartbeat = true, DispatcherPriority.Background);
            await settings.TestConnectionAsync();
            Assert(heartbeat && !running.IsCompleted && client.Count == 4 && settings.TestConnectionButton.IsEnabled
                && (string)settings.TestConnectionButton.Content == "取消测试", "Connection test blocked UI or duplicate click sent again.");
            settings.TestConnectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await running;
            releaseWorker.Set();
            Assert(settings.StatusText.Text == "已取消连接测试。" && (string)settings.TestConnectionButton.Content == "测试 AI 连接", "Cancel button failed.");

            var late = new TaskCompletionSource<AiConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Response = _ => late.Task;
            Assert(settings.WaitNoticeDelay == TimeSpan.FromSeconds(30), "Connection reminder default is not 30 seconds.");
            settings.WaitNoticeDelay = TimeSpan.FromMilliseconds(50);
            var waiting = settings.TestConnectionAsync();
            await Task.Delay(250);
            Assert(!waiting.IsCompleted && settings.StatusText.Text.Contains("可能超时")
                && (string)settings.TestConnectionButton.Content == "取消测试", "Reminder cancelled the request or did not appear.");
            late.SetResult(GoodConnection());
            await waiting;
            Assert(settings.StatusText.Text.Contains("检查通过"), "Response after reminder was lost.");
            late = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Response = _ => late.Task;
            waiting = settings.TestConnectionAsync();
            settings.CancelConnectionTest();
            await waiting;
            late.SetResult(GoodConnection());
            await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(settings.StatusText.Text.Contains("取消"), "Late result overwrote cancellation.");
            settings.WaitNoticeDelay = TimeSpan.FromSeconds(30);
            client.Response = _ => Task.FromResult(GoodConnection());
            await settings.TestConnectionAsync();
            Assert(settings.StatusText.Text.Contains("检查通过"), "Connection check did not recover after cancellation.");

            var closeClient = new FakeClient { Response = async token => { await Task.Delay(Timeout.Infinite, token); return GoodConnection(); } };
            var closing = new AiSettingsWindow(AiSettings.Default, store, new EmptySecrets(), closeClient) { Owner = window };
            closing.Show();
            var closingCheck = closing.TestConnectionAsync();
            closing.Close();
            await closingCheck;
            foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                await Layout(settings);
                AssertBrush(settings.ProviderBox.Foreground, "TextBrush");
                AssertBrush(settings.ApiKeyBox.Background, "InputBrush");
                foreach (var combo in new[] { settings.ProviderBox, window.AvailableSortBox, window.SourceFilterBox, window.OrderSourceFilterBox })
                {
                    combo.BringIntoView();
                    await Layout(settings);
                    combo.IsDropDownOpen = true;
                    await Layout(settings);
                    var toggle = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
                    Assert(toggle.ActualWidth >= combo.ActualWidth - 1, "Dropdown chrome collapsed.");
                    var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                    Assert(popup.IsOpen && popup.Child is FrameworkElement child && child.ActualWidth >= combo.ActualWidth - 1,
                        "Dropdown popup failed to lay out.");
                    var border = (FrameworkElement)popup.Child;
                    AssertBrush(((Border)border).Background, "PanelBrush");
                    foreach (var item in combo.Items.OfType<ComboBoxItem>())
                    {
                        Assert(item.ActualHeight > 15, "Dropdown item is not visible.");
                        AssertBrush(item.Foreground, "TextBrush");
                    }
                    PrototypeSmokeCheck.SavePreview(border, Path.Combine(output, $"{theme}-{combo.Name}-展开.png"));
                    combo.IsDropDownOpen = false;
                }
                settings.ProviderBox.BringIntoView();
                await Layout(settings);
                PrototypeSmokeCheck.SavePreview(settings, Path.Combine(output, $"{theme}-AI设置.png"));
                window.Width = 1360;
                window.Height = 820;
                await Layout(window);
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"{theme}-主窗口.png"));
                window.Width = 960;
                window.Height = 640;
                await Layout(window);
                Assert(window.SearchBox.ActualWidth > 150, "Search input collapsed.");
                var titleBounds = window.AvailableTitle.TransformToAncestor(window).TransformBounds(new Rect(window.AvailableTitle.RenderSize));
                var headerBounds = window.AvailableHeader.TransformToAncestor(window).TransformBounds(new Rect(window.AvailableHeader.RenderSize));
                var filterBounds = window.AvailableSortBox.TransformToAncestor(window).TransformBounds(new Rect(window.AvailableSortBox.RenderSize));
                var labelBounds = window.AvailableSortLabel.TransformToAncestor(window).TransformBounds(new Rect(window.AvailableSortLabel.RenderSize));
                Assert(!titleBounds.IntersectsWith(labelBounds) && labelBounds.Right <= filterBounds.Left, "Sort label is missing or overlaps controls.");
                Assert(Math.Abs(labelBounds.Top + labelBounds.Height / 2 - filterBounds.Top - filterBounds.Height / 2) < 2,
                    "Sort label and picker were separated when wrapping.");
                var directionBounds = window.AvailableSortDirectionButton.TransformToAncestor(window)
                    .TransformBounds(new Rect(window.AvailableSortDirectionButton.RenderSize));
                Assert(filterBounds.Right <= directionBounds.Left && directionBounds.Right <= headerBounds.Right + 1,
                    "Sort controls exceed the inactive pane.");
                var countBounds = window.AvailableCountText.TransformToAncestor(window).TransformBounds(new Rect(window.AvailableCountText.RenderSize));
                Assert(!countBounds.IntersectsWith(directionBounds), "Inactive count overlaps sort controls.");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"{theme}-主窗口最小尺寸.png"));
                window.AvailableCountText.SetCurrentValue(TextBlock.TextProperty, "20000 / 20000");
                await Layout(window);
                countBounds = window.AvailableCountText.TransformToAncestor(window).TransformBounds(new Rect(window.AvailableCountText.RenderSize));
                Assert(countBounds.Right <= headerBounds.Right + 1 && countBounds.Top >= directionBounds.Bottom,
                    "Large inactive count failed to wrap within the narrow pane.");
                window.AvailableCountText.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
            }
            // Keep a dropdown open during a live palette change, not only between openings.
            settings.ProviderBox.IsDropDownOpen = true;
            ((App)Application.Current).Themes.Select(ThemePreference.Dark, save: false);
            await Layout(settings);
            AssertBrush(((ComboBoxItem)settings.ProviderBox.Items[0]).Foreground, "TextBrush");
            settings.ProviderBox.IsDropDownOpen = false;
            Assert(settings.FindName("PermissionBox") is null, "Mode selection remained in API settings.");
            window.AvailableSortBox.SelectedIndex = 1;
            Assert(window.ViewModel.AvailableSortField == Presentation.AvailableModSortField.PackageId, "Available sort selection stopped working.");
            window.AvailableSortBox.SelectedIndex = 0;
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                Passed = true,
                AutomaticLogs = "light/dark minimum-size layout, persistent settings, manual export opens destination, unique archives, export failure leaves sorting unaffected",
                Checks = new[] { "direct save label and no-dialog unchanged save; stale file rejection", "light/dark combo and popup contrast", "popup hot switching", "password styling", "minimum-size search/sort/direction layout",
                    "six sort choices and both directions", "mixed Chinese pinyin/Latin sorting", "wrapped inactive controls stay together", "selection/search/refresh preservation", "enabled order/history unchanged", "batch append and undo",
                    "recent addition/update ordering and icons", "source and type before both list names", "badge icons/text hot switching and persistence", "local/workshop filtering on both lists without edits",
                    "both pane splitters resize and restore widths", "mode selection moved out of API settings", "connection check before saving/enabling; old billing does not block GET", "dispatcher heartbeat, duplicate suppression, cancel, thirty-second reminder without abort, late result, close cancellation", "no network checkbox", "no real network or credentials" }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { settings.Close(); window.ViewModel.MarkExported(); }
    }

    private static async Task Layout(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static void AssertBrush(Brush actual, string key)
    {
        Assert(actual is SolidColorBrush color && Application.Current.FindResource(key) is SolidColorBrush expected
            && color.Color == expected.Color, "Theme brush mismatch: " + key);
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class EmptySecrets : ISecretStore
    {
        public string? Read(string name) => null;
        public void Write(string name, string value) => throw new InvalidOperationException("Smoke check must not save a secret.");
        public void Delete(string name) => throw new InvalidOperationException("Smoke check must not delete a secret.");
    }

    private static AiConnectionResult GoodConnection() => new(true, "连接与鉴权检查通过。未调用模型生成。");

    private sealed class FakeClient : IAiClient
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        internal Func<CancellationToken, Task<AiConnectionResult>> Response = _ => Task.FromResult(GoodConnection());
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token)
        {
            Interlocked.Increment(ref count);
            return Response(token);
        }
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Connection test must not request a sorting suggestion.");
    }
}
#endif
