using System.IO;
using System.Net.Http;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RimWise.Integration.AI;
using RimWise.Desktop.Services;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Views;

public partial class AiSettingsWindow : Window
{
    private readonly AiSettingsStore store;
    private readonly ISecretStore secrets;
    private readonly AiPermissionLevel permissionLevel;
    private readonly string secretName;
    private readonly string savedEndpoint;
    private bool ready;
    private bool updating;
    private bool hasSavedKey;
    private bool replacingKey;
    private bool saving;
    private bool closed;
    private readonly IAiClient? testClient;
    private CancellationTokenSource? testCancellation;
    public AiSettings? SavedSettings { get; private set; }
    private const string SuggestedServiceUrl = "https://relayrax.com/sign-up?aff=dgCR";
    internal Action<string>? OpenServiceLinkForCheck { get; set; }

    public AiSettingsWindow(AiSettings current, AiSettingsStore store, ISecretStore secrets, IAiClient? testClient = null)
    {
        InitializeComponent();
        this.store = store;
        this.secrets = secrets;
        this.testClient = testClient;
        secretName = current.SecretName;
        savedEndpoint = AiServicePresets.NormalizeAddress(current.Endpoint);
        permissionLevel = current.PermissionLevel;
        EndpointBox.Text = current.Endpoint;
        ModelBox.Text = current.Model;
        StreamingBox.IsChecked = current.StreamResponses;
        foreach (var preset in AiServicePresets.All) ProviderBox.Items.Add(new ComboBoxItem { Content = preset.Name });
        ProviderBox.SelectedIndex = AiServicePresets.Find(current.Endpoint);
        ManualModelBox.IsChecked = true;
        ready = true;
        RefreshService();
        UpdateKeyStatus();
        Loaded += async (_, _) => await LoadKeyStatusAsync();
        DataObject.AddPastingHandler(ApiKeyBox, Connection_Pasting);
        DataObject.AddPastingHandler(EndpointBox, Connection_Pasting);
        Closed += (_, _) => { closed = true; testCancellation?.Cancel(); };
        Closing += (_, e) => { if (saving) { e.Cancel = true; StatusText.Text = "正在保存设置，请稍候。"; } };
    }

    private bool UsesSavedEndpoint => string.Equals(AiServicePresets.NormalizeAddress(EndpointBox.Text), savedEndpoint, StringComparison.Ordinal);

    internal async Task LoadKeyStatusAsync()
    {
        try
        {
            var exists = await Task.Run(() =>
            {
                var value = secrets.Read(secretName);
                DiagnosticRedactor.RegisterSecret(value);
                return !string.IsNullOrEmpty(value);
            });
            if (closed) return;
            hasSavedKey = exists;
            UpdateKeyStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or Win32Exception)
        { if (!closed) { ApiKeyStatus.Text = "无法读取已保存密钥，请重新填写。"; ApiKeyBox.Visibility = Visibility.Visible; } }
    }

    private void UpdateKeyStatus()
    {
        if (!ready) return;
        var saved = UsesSavedEndpoint && hasSavedKey;
        ApiKeyStatus.Text = ApiKeyBox.Password.Length > 0 ? "新密钥待保存（保存后加密存储）"
            : saved ? "已保存 · 本地加密，更新软件无需重填" : "尚未保存此服务的密钥；本机服务可留空";
        ApiKeyBox.Visibility = saved && !replacingKey && ApiKeyBox.Password.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReplaceKeyButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        RefreshServiceSuggestion();
    }

    private void ReplaceKey_Click(object sender, RoutedEventArgs e)
    { replacingKey = true; UpdateKeyStatus(); ApiKeyBox.Focus(); }
    private void Key_Changed(object sender, RoutedEventArgs e) { if (ready) UpdateKeyStatus(); }

    private void RefreshService()
    {
        var preset = AiServicePresets.All[Math.Max(0, ProviderBox.SelectedIndex)];
        ServiceNote.Text = preset.Note.Length > 0 ? preset.Note : "使用 OpenAI 兼容接口；模型以当前账户可用列表为准。";
        EndpointPanel.Visibility = ProviderBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshServiceSuggestion();
    }

    private void RefreshServiceSuggestion()
    {
        ServiceSuggestion.Visibility = ProviderBox.SelectedIndex == 0 && string.IsNullOrWhiteSpace(EndpointBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ServiceSuggestion_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (OpenServiceLinkForCheck is { } open) open(SuggestedServiceUrl);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SuggestedServiceUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { StatusText.Text = "无法打开默认浏览器，请检查系统浏览器设置。"; }
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating) return;
        var preset = AiServicePresets.All[Math.Max(0, ProviderBox.SelectedIndex)];
        if (preset.Endpoint.Length > 0) EndpointBox.Text = preset.Endpoint;
        RefreshService();
    }

    private void Endpoint_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ready || updating) return;
        ApiKeyBox.Clear();
        replacingKey = false;
        ModelListBox.ItemsSource = null;
        ModelBox.Clear();
        ManualModelBox.IsChecked = true;
        updating = true;
        ProviderBox.SelectedIndex = AiServicePresets.Find(EndpointBox.Text);
        updating = false;
        RefreshService();
        UpdateKeyStatus();
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    { if (ready && ModelListBox.SelectedItem is string model) ModelBox.Text = model; }
    private void ManualModel_Changed(object sender, RoutedEventArgs e)
    {
        if (ModelBox is null || ModelListBox is null) return;
        ModelBox.Visibility = ManualModelBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ModelListBox.Visibility = ManualModelBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    { await TestConnectionAsync(fetchModels: true); }

    private void PasteConnection_Click(object sender, RoutedEventArgs e)
    {
        try { ImportConnection(Clipboard.ContainsText() ? Clipboard.GetText() : ""); }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "暂时无法读取剪贴板，请重新复制后再试。"; }
    }

    private void Connection_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = (e.DataObject.GetData(DataFormats.UnicodeText) as string)?.Trim() ?? "";
        // Ordinary key/address pasting keeps the original editing behavior.
        if (!text.StartsWith('{') && !text.StartsWith('[') && !text.StartsWith("```") && !text.Contains('\n')) return;
        e.CancelCommand();
        ImportConnection(text);
    }

    internal bool ImportConnection(string text)
    {
        if (testCancellation is not null) { StatusText.Text = "请先取消正在进行的连接测试，再导入配置。"; return false; }
        try
        {
            var imported = AiConnectionImport.Parse(text);
            // A new host must never silently inherit the previous provider's saved secret.
            var previousAddress = EndpointBox.Text.Trim();
            try { previousAddress = (AiSettings.Default with { Endpoint = previousAddress }).Normalize().Endpoint; }
            catch (ArgumentException) { /* A valid import may replace an invalid draft address. */ }
            var changedAddress = imported.Endpoint is not null && !string.Equals(imported.Endpoint,
                previousAddress, StringComparison.OrdinalIgnoreCase);
            if (changedAddress && imported.ApiKey is null)
            {
                StatusText.Text = "连接信息更换了 API 地址但未包含密钥。请一并复制地址与密钥，或手动修改配置。";
                return false;
            }
            updating = true;
            if (imported.Endpoint is not null)
            {
                EndpointBox.Text = imported.Endpoint;
                ProviderBox.SelectedIndex = AiServicePresets.Find(imported.Endpoint);
            }
            if (changedAddress) ModelListBox.ItemsSource = null;
            if (imported.ApiKey is not null)
            {
                DiagnosticRedactor.RegisterSecret(imported.ApiKey);
                ApiKeyBox.Password = imported.ApiKey;
                replacingKey = true;
            }
            if (imported.Model is not null) ModelBox.Text = imported.Model;
            else if (changedAddress) ModelBox.Clear();
            ManualModelBox.IsChecked = true;
            updating = false;
            RefreshService();
            UpdateKeyStatus();
            StatusText.Text = ModelBox.Text.Length == 0
                ? "已填入连接信息。未提供模型名称，请填写后保存；尚未发送请求。"
                : "已填入识别到的连接信息。请核对地址和模型后保存；尚未发送请求。";
            return true;
        }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return false; }
    }

    private AiSettings ReadSettings()
    {
        if (string.IsNullOrWhiteSpace(EndpointBox.Text)) throw new ArgumentException("请填写 API 地址或选择服务预设。");
        if (ApiKeyBox.Password.Length > 8192 || ApiKeyBox.Password.Any(char.IsWhiteSpace))
            throw new ArgumentException("密钥不能包含空白字符或超过长度限制，请重新复制密钥。");
        return new AiSettings(
            1,
            true,
            AiProviderKind.OpenAiCompatible,
            EndpointBox.Text,
            ModelBox.Text,
            permissionLevel,
            secretName) { StreamResponses = StreamingBox.IsChecked == true }.Normalize();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (testCancellation is not null) { CancelConnectionTest(); return; }
        DiagnosticEventLog.Current?.Record("ai-ui", "connection-test-start");
        await TestConnectionAsync();
    }

    internal TimeSpan WaitNoticeDelay { get; set; } = AiWaitNotice.DefaultDelay;
    internal void CancelConnectionTest() => testCancellation?.Cancel();

    internal async Task TestConnectionAsync(bool fetchModels = false)
    {
        if (testCancellation is not null || closed) return;
        using var cancellation = new CancellationTokenSource();
        testCancellation = cancellation;
        TestConnectionButton.Content = "取消测试";
        SetEditingEnabled(false);
        var token = cancellation.Token;
        using var notice = new AiWaitNotice(WaitNoticeDelay, (elapsed, slow) =>
        {
            if (!closed && !token.IsCancellationRequested)
                StatusText.Text = slow ? $"已等待 {elapsed.TotalSeconds:F0} 秒，连接可能超时；仍在等待，可取消。"
                    : $"正在检查连接与鉴权… 已等待 {elapsed.TotalSeconds:F0} 秒";
        });
        try
        {
            var settings = ReadSettings();
            var enteredKey = ApiKeyBox.Password;
            var mayReadSavedKey = UsesSavedEndpoint;
            StatusText.Text = "正在检查连接与鉴权…";
            // Capture controls first. Disk access, decryption and HTTP setup run off the dispatcher.
            var worker = Task.Run(async () =>
            {
                var secret = enteredKey.Length > 0 ? enteredKey : mayReadSavedKey ? secrets.Read(settings.SecretName) : null;
                DiagnosticRedactor.RegisterSecret(secret);
                token.ThrowIfCancellationRequested();
                using var client = testClient is null ? new AiClient() : null;
                return await (testClient ?? client!).TestConnectionAsync(settings, secret, token).ConfigureAwait(false);
            }, token);
            // A provider or secret store may finish after cancellation. Observe a late failure
            // without letting it update this window or keep the dispatcher waiting.
            _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await worker.WaitAsync(token);
            if (!closed)
            {
                StatusText.Text = result.Message;
                if (result.Succeeded && result.Models.Count > 0)
                {
                    ModelListBox.ItemsSource = result.Models;
                    ModelListBox.SelectedItem = result.Models.FirstOrDefault(id => id == settings.Model);
                    if (fetchModels)
                    {
                        ManualModelBox.IsChecked = settings.Model.Length > 0 && ModelListBox.SelectedItem is null;
                        StatusText.Text += $" 已获取 {result.Models.Count} 个模型，请从列表选择。";
                    }
                }
                else if (fetchModels)
                {
                    ModelListBox.ItemsSource = null;
                    ManualModelBox.IsChecked = true;
                    StatusText.Text += " 未获得可选模型，可按服务商说明手动填写。";
                }
                StatusText.BringIntoView();
            }
            DiagnosticEventLog.Current?.Record("ai-ui", "connection-test-result", result.Message);
        }
        catch (OperationCanceledException)
        {
            DiagnosticEventLog.Current?.Record("ai-ui", "connection-test-cancelled");
            if (!closed) StatusText.Text = "已取消连接测试。";
        }
        catch (InvalidOperationException ex) { if (!closed) StatusText.Text = ex.Message; DiagnosticEventLog.Current?.Record("ai-ui", "connection-test-failed", ex.ToString()); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or PlatformNotSupportedException or Win32Exception or HttpRequestException)
        { if (!closed) StatusText.Text = "连接测试失败，请检查配置和密钥。"; DiagnosticEventLog.Current?.Record("ai-ui", "connection-test-failed", ex.GetType().Name); }
        finally
        {
            testCancellation = null;
            if (!closed) { TestConnectionButton.Content = "测试 AI 连接"; SetEditingEnabled(true); }
        }
    }

    private void SetEditingEnabled(bool enabled)
    {
        foreach (var control in new Control[] { ProviderBox, EndpointBox, ModelBox, ModelListBox,
                     ApiKeyBox, ReplaceKeyButton, FetchModelsButton, PasteConnectionButton, SaveButton, ManualModelBox })
            control.IsEnabled = enabled;
    }

    internal async Task PersistSettingsAsync()
    {
        var settings = ReadSettings();
        var enteredKey = ApiKeyBox.Password;
        var sameEndpoint = UsesSavedEndpoint;
        if (enteredKey.Length > 8192 || enteredKey.Any(char.IsWhiteSpace))
            throw new ArgumentException("密钥不能包含空白字符或超过长度限制，请重新复制密钥。");
        if (!sameEndpoint && enteredKey.Length == 0 && !new Uri(settings.Endpoint).IsLoopback)
            throw new ArgumentException("服务地址已更换，请填写此服务的密钥；不会复用其他服务的密钥。");
        DiagnosticRedactor.RegisterSecret(enteredKey);
        var saved = await Task.Run(() =>
        {
            // Preserve changes made by the sorting window while settings was open.
            var latest = settings with { PermissionLevel = store.Load().PermissionLevel };
            var changesKey = enteredKey.Length > 0 || !sameEndpoint;
            var previousKey = changesKey ? secrets.Read(latest.SecretName) : null;
            try
            {
                if (enteredKey.Length > 0) secrets.Write(latest.SecretName, enteredKey);
                else if (!sameEndpoint) secrets.Delete(latest.SecretName);
                store.Save(latest);
            }
            catch
            {
                if (changesKey)
                {
                    if (previousKey is not null) secrets.Write(latest.SecretName, previousKey);
                    else secrets.Delete(latest.SecretName);
                }
                throw;
            }
            return latest;
        });
        SavedSettings = saved;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (testCancellation is not null) return;
        saving = true;
        SetEditingEnabled(false);
        TestConnectionButton.IsEnabled = false;
        try
        {
            await PersistSettingsAsync();
            saving = false;
            if (!closed) DialogResult = true;
        }
        catch (ArgumentException ex)
        { if (!closed) StatusText.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or PlatformNotSupportedException or Win32Exception)
        { if (!closed) MessageDialog.Show(this, "无法保存 AI 设置，请检查地址、密钥和用户数据目录的写入权限。", "无法保存 AI 设置", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { saving = false; if (!closed) { SetEditingEnabled(true); TestConnectionButton.IsEnabled = true; } }
    }
}
