using System.IO;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RimWise.Integration.AI;

namespace RimWise.Desktop.Views;

/// <summary>Visible-only, read-only polling. Provider snapshots are never added to the local ledger.</summary>
public sealed class AiUsageMonitorWindow : Window
{
    private readonly AiSettingsStore settings;
    private readonly ISecretStore secrets;
    private readonly AiTaskRecoveryStore tasks;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly TextBlock local = Text();
    private readonly TextBlock provider = Text();
    private readonly TextBlock wallet = Text();
    private readonly TextBlock status = Text();
    private readonly Button refresh = new() { Content = "刷新", Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button cancel = new() { Content = "取消查询", IsEnabled = false };
    private CancellationTokenSource? request;
    private bool closed;
    private AiProviderTokenSnapshot? lastTokens;
    private AiWalletSnapshot? lastWallet;
    private DateTimeOffset? tokensUtc, walletUtc;
    private string? lastEndpoint, lastKeyHash;

    public AiUsageMonitorWindow(AiSettingsStore settings, ISecretStore secrets, AiTaskRecoveryStore tasks)
    {
        this.settings = settings; this.secrets = secrets; this.tasks = tasks;
        Title = "AI 用量与余额"; Width = 700; Height = 520; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var panel = new StackPanel { Margin = new Thickness(24) };
        AddHeading(panel, "本软件已记录用量"); panel.Children.Add(local);
        AddHeading(panel, "任务期间服务端用量核对"); panel.Children.Add(provider);
        AddHeading(panel, "钱包与密钥额度"); panel.Children.Add(wallet);
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(refresh); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        refresh.Click += async (_, _) => await RefreshAsync();
        cancel.Click += (_, _) => { request?.Cancel(); cancel.IsEnabled = false; };
        timer.Tick += async (_, _) => { if (IsVisible) await RefreshAsync(); };
        Loaded += async (_, _) => { timer.Start(); await RefreshAsync(); };
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); lastKeyHash = null; lifetime.Dispose(); };
    }

    private static TextBlock Text() => new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private static void AddHeading(Panel panel, string value) => panel.Children.Add(new TextBlock
        { Text = value, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });

    private async Task RefreshAsync()
    {
        if (closed || request is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request = cancellation; refresh.IsEnabled = false; cancel.IsEnabled = true;
        status.Text = "正在查询… 此窗口每 60 秒刷新，不调用模型生成。";
        try
        {
            // Disk inspection and DPAPI decryption must not block the WPF dispatcher.
            var context = await Task.Run(() =>
            {
                var current = settings.Load();
                return (Settings: current, Key: secrets.Read(current.SecretName), Totals: AiUsageTotals.Read(tasks));
            }, cancellation.Token);
            if (closed) return;
            local.Text = context.Totals.Description + "\n仅本软件此数据目录的记录；更换电脑或数据目录不会合并历史。";
            var keyHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(context.Key ?? "")));
            if (lastEndpoint != context.Settings.Endpoint || lastKeyHash != keyHash)
            {
                lastTokens = null; lastWallet = null; tokensUtc = null; walletUtc = null;
                lastEndpoint = context.Settings.Endpoint; lastKeyHash = keyHash;
                provider.Text = "正在读取…"; wallet.Text = "正在读取…";
            }
            using var client = new AiProviderUsageClient();
#if DEBUG
            var result = await (QueryForCheck ?? client.QueryAsync)(context.Settings.Endpoint, context.Key, cancellation.Token);
#else
            var result = await client.QueryAsync(context.Settings.Endpoint, context.Key, cancellation.Token);
#endif
            if (closed) return;
            var evidence = await Task.Run(() => AiUsageEvidence.Read(tasks), cancellation.Token);
            // Settings may have changed in another window while the request was in flight.
            var stillCurrent = await Task.Run(() => settings.Load() == context.Settings
                && secrets.Read(context.Settings.SecretName) == context.Key, cancellation.Token);
            if (closed) return;
            if (!stillCurrent)
            {
                lastTokens = null; lastWallet = null; tokensUtc = null; walletUtc = null;
                provider.Text = "AI 配置已变化，请刷新。"; wallet.Text = "AI 配置已变化，请刷新。";
                status.Text = "本次查询结果已丢弃。"; return;
            }
            if (result.Tokens is { } tokens) { lastTokens = tokens; tokensUtc = result.QueriedUtc; }
            if (result.Wallet is { } funds) { lastWallet = funds; walletUtc = result.QueriedUtc; }
            var scope = AiUsageRequest.ScopeFor(context.Settings.Normalize().Endpoint, context.Key);
            if (closed) return;
            provider.Text = lastTokens is { } usage
                ? DescribeAttribution(usage, evidence, scope) + $"\n查询时间：{tokensUtc!.Value.LocalDateTime:G}"
                : "尚无有效查询结果。";
            provider.Text += "\n" + evidence.Describe(scope);
            if (result.TokenProblem is { } tokenProblem) provider.Text += "\n" + tokenProblem + (lastTokens is null ? "" : "（保留上次结果）");
            wallet.Text = lastWallet is { } balance
                ? $"钱包可用：{balance.Describe(balance.WalletQuota)}\n"
                    + (balance.UnlimitedKey ? "密钥自身不限额度；仍受钱包余额限制。" : $"密钥可用：{balance.Describe(balance.AvailableQuota)}")
                    + $"\n查询时间：{walletUtc!.Value.LocalDateTime:G}"
                : "尚无有效查询结果。";
            if (result.WalletProblem is { } walletProblem) wallet.Text += "\n" + walletProblem + (lastWallet is null ? "" : "（保留上次结果）");
            status.Text = "仅时间重合不能确认归属；待确认记录不计入本软件累计。服务端核对与本机记录不重复相加。";
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "已取消查询；未调用模型生成。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or InvalidOperationException or CryptographicException or OverflowException or Win32Exception or PlatformNotSupportedException)
        { if (!closed) status.Text = "无法读取用量或密钥，请检查本地设置；已有记录已保留。"; }
        finally
        {
            request = null;
            if (!closed) { refresh.IsEnabled = true; cancel.IsEnabled = false; }
        }
    }

    private static string DescribeAttribution(AiProviderTokenSnapshot usage, AiUsageEvidence evidence, string scope)
    {
        var match = AiUsageAttribution.Match(usage.Rows, evidence.Requests, evidence.Periods, scope);
        return $"本软件已匹配：{match.Confirmed:N0} token（{match.ConfirmedCount} 次请求）\n"
            + $"任务期间待确认：{match.Pending:N0} token（{match.PendingCount} 条，可能来自其他软件，不计入累计）\n"
            + $"未归属：{match.UnattributedCount} 条；证据冲突：{match.ConflictingCount} 条\n"
            + $"仅核对服务端近期返回的 {usage.Count:N0} 条，非全部历史；不匹配的记录不计入本软件。";
    }

#if DEBUG
    internal Func<string, string?, CancellationToken, Task<AiProviderUsageResult>>? QueryForCheck { get; set; }
    internal string ProviderTextForCheck => provider.Text;
    internal string WalletTextForCheck => wallet.Text;
    internal string StatusForCheck => status.Text;
    internal bool BusyForCheck => request is not null;
    internal void CancelForCheck() => request?.Cancel();
#endif
}
