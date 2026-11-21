using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using RimWise.Integration.Diagnostics;
using RimWise.Desktop.Services;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop.Views;

internal sealed class LocalMarketAccountWindow : Window
{
    private readonly LocalMarketApiClient api;
    private readonly TextBox username = new() { MaxLength = 32 };
    private readonly TextBox nickname = new() { MaxLength = 40 };
    private readonly TextBox recoveryCode = new() { MaxLength = 64 };
    private readonly PasswordBox password = new() { MaxLength = 256 };
    private readonly PasswordBox confirmation = new() { MaxLength = 256 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel nicknamePanel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel recoveryPanel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel confirmationPanel = new() { Visibility = Visibility.Collapsed };
    private readonly Button submit = new() { Content = "登录", MinWidth = 80, Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button toggle = new() { Content = "创建账号", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Hyperlink forgotPassword = new(new Run("忘记密码"))
    {
        TextDecorations = TextDecorations.Underline,
        Cursor = Cursors.Hand,
        Focusable = true,
    };
    private readonly TextBlock forgotPasswordLine = new() { Margin = new Thickness(0, 4, 0, 0) };
    private bool register;
    private bool recover;

    internal GitHubMarketplaceUser? AuthenticatedUser { get; private set; }

    internal LocalMarketAccountWindow(LocalMarketApiClient api)
    {
        this.api = api;
        Title = "排序市场账号";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        username.ToolTip = "3 至 32 位英文字母、数字、下划线或短横线";
        password.PasswordChar = '\u25cf';
        confirmation.PasswordChar = '\u25cf';
        foreach (var input in new Control[] { username, nickname, password, recoveryCode, confirmation })
            MarketAccountInput.ApplyCompactLayout(input);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "排序市场账号", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        AddField(root, "用户名", username);
        nicknamePanel.Children.Add(new TextBlock { Text = "昵称", Margin = new Thickness(0, 8, 0, 3) });
        nicknamePanel.Children.Add(nickname);
        root.Children.Add(nicknamePanel);
        AddField(root, "密码", password);
        forgotPassword.SetResourceReference(TextElement.ForegroundProperty, "LinkBrush");
        forgotPassword.Click += (_, _) => { SetMode(register: false, recover: true); recoveryCode.Focus(); };
        forgotPasswordLine.Inlines.Add(forgotPassword);
        root.Children.Add(forgotPasswordLine);
        root.Children.Add(recoveryPanel);
        recoveryPanel.Children.Add(new TextBlock { Text = "恢复码", Margin = new Thickness(0, 8, 0, 3) });
        recoveryPanel.Children.Add(recoveryCode);
        confirmationPanel.Children.Add(new TextBlock { Text = "再次输入密码", Margin = new Thickness(0, 8, 0, 3) });
        confirmationPanel.Children.Add(confirmation);
        root.Children.Add(confirmationPanel);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        submit.Click += async (_, _) => await SubmitAsync();
        toggle.Click += (_, _) => SetMode(register: !register && !recover, recover: false);
        actions.Children.Add(submit);
        actions.Children.Add(toggle);
        root.Children.Add(actions);
        root.Children.Add(status);
        Content = root;
        SetMode(false, false);
    }

    private void SetMode(bool register, bool recover)
    {
        this.register = register;
        this.recover = recover;
        nicknamePanel.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        recoveryPanel.Visibility = recover ? Visibility.Visible : Visibility.Collapsed;
        recoveryCode.Visibility = Visibility.Visible;
        confirmationPanel.Visibility = register || recover ? Visibility.Visible : Visibility.Collapsed;
        status.Text = recover ? "输入注册时保存的恢复码并设置新密码。"
            : register ? "密码长度为 7 至 15 个字符。" : string.Empty;
        submit.Content = recover ? "重置密码" : register ? "注册" : "登录";
        toggle.Content = register || recover ? "返回登录" : "创建账号";
        forgotPasswordLine.Visibility = register || recover ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task SubmitAsync()
    {
        var name = username.Text.Trim();
        var secret = password.Password;
        var confirm = confirmation.Password;
        if (Count(secret) is < 7 or > 15)
        {
            status.Text = "密码须为 7 至 15 个字符。";
            return;
        }
        if ((register || recover) && secret != confirm)
        {
            status.Text = "两次输入的密码不一致。";
            return;
        }
        try
        {
            IsEnabled = false;
            if (recover)
            {
                var replacement = await api.RecoverAsync(name, recoveryCode.Text.Trim(), secret, CancellationToken.None);
                new RecoveryCodeWindow(replacement, recovered: true) { Owner = this }.ShowDialog();
                status.Text = "密码已重置，请使用新密码登录。";
                SetMode(false, false);
                password.Clear();
                confirmation.Clear();
                return;
            }
            var result = await api.LoginAsync(name, secret, nickname.Text.Trim(), register, CancellationToken.None);
            AuthenticatedUser = result.User;
            if (result.RecoveryCode is { Length: > 0 } code)
            {
                new RecoveryCodeWindow(code, recovered: false) { Owner = this }.ShowDialog();
            }
            if (api.SessionWarning is { } warning)
                MessageDialog.Show(this, warning, "登录记录", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            var safe = new DiagnosticRedactor().Text(ex.Message);
            status.Text = safe.Any(c => c is >= '\u3400' and <= '\u9fff') ? safe : "账号请求失败，请检查连接后重试。";
        }
        finally { IsEnabled = true; }
    }

    private static int Count(string value) => value.EnumerateRunes().Count();

    private static void AddField(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        panel.Children.Add(control);
    }
}
