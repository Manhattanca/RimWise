#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RimWise.Desktop.Views;
using RimWise.Integration.AI;

namespace RimWise.Desktop;

internal static class AiConfigurationSmokeCheck
{
    internal static async Task Run(Window owner, string output)
    {
        var root = Path.Combine(output, "连接配置 中文");
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
            Assert(window.ApiKeyStatus.Text.Contains("已保存") && window.ApiKeyBox.Visibility == Visibility.Collapsed, "Saved key status missing");
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

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
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
