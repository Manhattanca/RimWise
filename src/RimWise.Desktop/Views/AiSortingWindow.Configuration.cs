using System.Windows;
using RimWise.Integration.AI;

namespace RimWise.Desktop.Views;

public partial class AiSortingWindow
{
    internal async Task<bool> EnsureAiConfiguredAsync()
    {
        if (closed || IsWorking || ownerLifetime.IsCancellationRequested) return false;
        RefreshConfiguration();
        if (IsOfflineMode) return true;
        var configured = settings.Enabled && AiClient.ConfigurationProblem(settings) is null;
        if (configured && !new Uri(settings.Endpoint).IsLoopback)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime);
            requestCancellation = cancellation;
            RefreshButtons();
            try
            {
                var secretName = settings.SecretName;
                configured = await Task.Run(() => !string.IsNullOrWhiteSpace(session.Secrets.Read(secretName)), cancellation.Token)
                    .WaitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) when (IsExpectedError(ex) || ex is System.Security.Cryptography.CryptographicException)
            { configured = false; }
            finally { requestCancellation = null; if (!closed) RefreshButtons(); }
        }
        if (closed || ownerLifetime.IsCancellationRequested) return false;
        if (configured) return true;
        Settings_Click(this, new RoutedEventArgs());
        if (!closed) StatusText.Text = "请完成 AI 地址、密钥和模型配置后，点击“自动排序并测试”开始。";
        // Saving settings is not consent to launch a game or a paid request.
        return false;
    }
}
