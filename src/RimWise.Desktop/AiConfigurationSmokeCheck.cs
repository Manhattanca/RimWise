#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using RimWise.Integration.Configuration;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;

namespace RimWise.Desktop;

internal static class AiConfigurationSmokeCheck
{
    internal static async Task Run(Window owner, string output)
    {
        var root = Path.Combine(output, "连接配置 中文");
        await CheckFirstSetup(owner, output, Path.Combine(root, "首次配置"));
        var store = new AiSettingsStore(root);
        var original = AiSettings.Default with { Endpoint = "https://first.invalid/v1/chat/completions", Model = "old-model", SecretName = "synthetic-legacy-name" };
        store.Save(original);
        var secrets = new LocalEncryptedSecretStore(root);
        secrets.Write(original.SecretName, "synthetic-saved-secret");
        var client = new Client();
        var window = new AiSettingsWindow(original, store, secrets, client) { Owner = owner };
        window.Show();
        try
        {
            await window.LoadKeyStatusAsync();
            Assert(window.ServiceSuggestion.Visibility == Visibility.Collapsed, "Existing provider received suggestion");
            Assert(window.ApiKeyStatus.Text.Contains("已保存") && window.ApiKeyBox.Visibility == Visibility.Collapsed, "Saved key status missing");
            window.EndpointBox.Clear();
            Assert(window.ServiceSuggestion.Visibility == Visibility.Visible, "Unrelated saved key hid blank-address suggestion");
            Assert(secrets.Read(original.SecretName) == "synthetic-saved-secret" && store.Load().Endpoint == original.Endpoint,
                "Clearing draft address changed saved credentials");
            window.EndpointBox.Text = original.Endpoint;
            Assert(window.ServiceSuggestion.Visibility == Visibility.Collapsed && window.ApiKeyStatus.Text.Contains("已保存"),
                "Restoring address lost saved status or retained suggestion");
            Assert(!window.AdvancedOptions.IsExpanded, "Advanced options not folded");
            await window.TestConnectionAsync(fetchModels: true);
            Assert(client.Key == "synthetic-saved-secret" && window.ModelListBox.Items.Count == 2, "Saved key/model discovery failed");
            window.ModelListBox.SelectedIndex = 1;
            Assert(window.ModelBox.Text == "z-model", "Model selection did not reach settings");
            window.ProviderBox.SelectedIndex = 2;
            Assert(window.EndpointBox.Text == AiServicePresets.All[2].Endpoint && window.ApiKeyBox.Password.Length == 0
                && window.ModelListBox.Items.Count == 0, "Service switch retained old draft");
            await window.TestConnectionAsync(fetchModels: true);
            Assert(client.Key is null, "Saved secret leaked to another service");
            try { await window.PersistSettingsAsync(); throw new InvalidOperationException("Changed service accepted old key"); }
            catch (ArgumentException) { }
            Assert(secrets.Read(original.SecretName) == "synthetic-saved-secret" && store.Load().Endpoint == original.Endpoint, "Rejected save mutated configuration");
            window.EndpointBox.Text = original.Endpoint;
            await window.LoadKeyStatusAsync();
            Assert(window.ApiKeyStatus.Text.Contains("已保存"), "Returning to saved endpoint lost key");
            window.ReplaceKeyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(window.ApiKeyBox.Visibility == Visibility.Visible && window.ApiKeyBox.Password.Length == 0, "Replacement exposed old secret");
            window.ApiKeyBox.Password = "synthetic-replacement-secret";
            window.ModelBox.Text = "a-model";
            await window.PersistSettingsAsync();
            Assert(store.Load().SecretName == original.SecretName && secrets.Read(original.SecretName) == "synthetic-replacement-secret", "Key replacement or legacy identity lost");
            Assert(!File.ReadAllText(store.SettingsPath).Contains("synthetic-replacement-secret"), "Plain settings contain key");
        }
        finally { window.Close(); }
        var reopened = new AiSettingsWindow(store.Load(), store, secrets, client) { Owner = owner };
        reopened.Show();
        try
        {
            await reopened.LoadKeyStatusAsync();
            Assert(reopened.ApiKeyStatus.Text.Contains("已保存"), "Saved status lost after reopening");
            await reopened.TestConnectionAsync();
            Assert(client.Key == "synthetic-replacement-secret", "Reopening lost key");
            client.Result = new(false, "HTTP 404：此地址不提供模型列表。");
            await reopened.TestConnectionAsync(fetchModels: true);
            Assert(reopened.ManualModelBox.IsChecked == true && reopened.ModelBox.Visibility == Visibility.Visible
                && reopened.ModelListBox.Items.Count == 0, "Manual fallback missing");
        }
        finally { reopened.Close(); }
    }

    private static async Task CheckFirstSetup(Window owner, string output, string root)
    {
        var store = new AiSettingsStore(root);
        var current = store.Load();
        Assert(current.Enabled && current.Endpoint.Length == 0, "Fresh setup did not default to custom service");
        var window = new AiSettingsWindow(current, store, new LocalEncryptedSecretStore(root), new Client())
            { Owner = owner, Width = 620, Height = 740 };
        var opened = new List<string>();
        window.OpenServiceLinkForCheck = opened.Add;
        window.Show();
        try
        {
            await window.LoadKeyStatusAsync();
            Assert(window.ProviderBox.SelectedIndex == 0 && window.ServiceSuggestion.Visibility == Visibility.Visible
                && window.FindName("EnabledBox") is null && opened.Count == 0, "Initial suggestion or retired switch incorrect");
            window.ServiceSuggestionLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert(opened.SequenceEqual(["https://relayrax.com/sign-up?aff=dgCR"]), "Suggestion link changed or opened automatically");
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ((App)Application.Current).Themes.Select(theme, save: false);
                window.UpdateLayout();
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"{theme}-首次服务提示.png"));
                window.Height = 520;
                window.UpdateLayout();
                Assert(!window.SettingsScroll.Focusable && window.SettingsScroll.FocusVisualStyle is null,
                    "Settings container can display a focus outline");
                window.SettingsScroll.ScrollToTop();
                window.UpdateLayout();
                window.SettingsScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = Mouse.MouseWheelEvent });
                window.UpdateLayout();
                Assert(window.SettingsScroll.VerticalOffset > 0 && window.SettingsScroll.ComputedVerticalScrollBarVisibility != Visibility.Visible,
                    "Hidden settings scrollbar disabled wheel scrolling or remained visible");
                var header = Descendants(window.AdvancedOptions).OfType<ToggleButton>().First();
                header.Focus();
                var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(header).GetPattern(PatternInterface.Toggle);
                toggle.Toggle();
                await Task.Delay(400);
                window.SettingsScroll.ScrollToBottom();
                window.UpdateLayout();
                Assert(window.AdvancedOptions.IsExpanded && window.StreamingBox.IsVisible,
                    "Advanced settings became inaccessible");
                var focusTemplate = header.FocusVisualStyle?.Setters.OfType<Setter>()
                    .FirstOrDefault(s => s.Property == Control.TemplateProperty)?.Value as ControlTemplate;
                Assert(header.FocusVisualStyle is null || focusTemplate?.LoadContent() is null,
                    "Advanced header retained its separate focus adorner");
                PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, $"{theme}-高级配置聚焦.png"));
                toggle.Toggle();
                window.Height = 740;
                window.SettingsScroll.ScrollToTop();
            }
            window.EndpointBox.Text = "https://other.invalid";
            Assert(window.ServiceSuggestion.Visibility == Visibility.Collapsed, "Typed custom address did not hide suggestion");
            window.EndpointBox.Clear();
            window.ApiKeyBox.Password = "synthetic-draft";
            Assert(window.ServiceSuggestion.Visibility == Visibility.Visible, "Draft key hid blank-address suggestion");
            window.ApiKeyBox.Clear();
            Assert(window.ServiceSuggestion.Visibility == Visibility.Visible, "Empty setup did not restore suggestion");
            window.ProviderBox.SelectedIndex = 2;
            Assert(window.ServiceSuggestion.Visibility == Visibility.Collapsed, "Preset did not hide suggestion");
            window.EndpointBox.Clear();
            Assert(window.ImportConnection("{\"url\":\"https://other.invalid\",\"key\":\"synthetic-import\"}"), "Synthetic import failed");
            Assert(window.ServiceSuggestion.Visibility == Visibility.Collapsed && opened.Count == 1,
                "Imported service did not hide suggestion or unexpectedly opened browser");
            Assert(!File.Exists(store.SettingsPath), "Viewing setup persisted configuration");
        }
        finally { window.Close(); }
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private sealed class Client : IAiClient
    {
        internal string? Key;
        internal AiConnectionResult Result = new(true, "连接与鉴权检查通过。") { Models = ["a-model", "z-model"] };
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token)
        { Key = key; return Task.FromResult(Result); }
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token) => throw new InvalidOperationException("Generation forbidden");
    }
}
#endif
