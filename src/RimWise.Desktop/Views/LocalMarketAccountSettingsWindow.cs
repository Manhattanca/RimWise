using System.Text;
using System.Windows;
using System.Windows.Controls;
using RimWise.Integration.Diagnostics;
using RimWise.Desktop.Services;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop.Views;

internal sealed class LocalMarketAccountSettingsWindow : Window
{
    private readonly LocalMarketApiClient api;
    private readonly TextBox nickname = new() { MaxLength = 40 };
    private readonly PasswordBox currentPassword = new() { MaxLength = 256 };
    private readonly PasswordBox nextPassword = new() { MaxLength = 256 };
    private readonly PasswordBox confirmPassword = new() { MaxLength = 256 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly GitHubMarketplaceUser user;
    private readonly TextBlock heading = new() { FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };

    internal LocalMarketAccountSettingsWindow(LocalMarketApiClient api, GitHubMarketplaceUser user)
    {
        this.api = api;
        this.user = user;
        Title = "账号设置";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        nickname.Text = user.Name ?? "";
        foreach (var box in new[] { currentPassword, nextPassword, confirmPassword }) box.PasswordChar = '\u25cf';
        foreach (var input in new Control[] { nickname, currentPassword, nextPassword, confirmPassword })
            MarketAccountInput.ApplyCompactLayout(input);

        var root = new StackPanel { Margin = new Thickness(22) };
        heading.Text = "账号设置 · " + user.DisplayName;
        root.Children.Add(heading);
        AddField(root, "修改昵称", nickname);
        var saveName = new Button { Content = "保存昵称", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 8, 0, 12) };
        saveName.Click += async (_, _) => await ChangeNicknameAsync();
        root.Children.Add(saveName);
        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        AddField(root, "当前密码", currentPassword);
        AddField(root, "新密码（7 至 15 个字符）", nextPassword);
        AddField(root, "确认新密码", confirmPassword);
        var savePassword = new Button { Content = "修改密码", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 12, 0, 0) };
        savePassword.Click += async (_, _) => await ChangePasswordAsync();
        root.Children.Add(savePassword);
        root.Children.Add(status);
        Content = root;
    }

    private async Task ChangeNicknameAsync()
    {
        try
        {
            IsEnabled = false;
            await api.ChangeNicknameAsync(nickname.Text.Trim(), CancellationToken.None);
            heading.Text = "账号设置 · " + api.User!.DisplayName;
            status.Text = "昵称已更新。";
        }
        catch (Exception ex) { status.Text = SafeError(ex); }
        finally { IsEnabled = true; }
    }

    private async Task ChangePasswordAsync()
    {
        var next = nextPassword.Password;
        if (next.EnumerateRunes().Count() is < 7 or > 15)
        {
            status.Text = "密码须为 7 至 15 个字符。";
            return;
        }
        if (next != confirmPassword.Password)
        {
            status.Text = "两次输入的新密码不一致。";
            return;
        }
        try
        {
            IsEnabled = false;
            await api.ChangePasswordAsync(currentPassword.Password, next, CancellationToken.None);
            currentPassword.Clear();
            nextPassword.Clear();
            confirmPassword.Clear();
            status.Text = "密码已更新，恢复码保持不变。";
        }
        catch (Exception ex) { status.Text = SafeError(ex); }
        finally { IsEnabled = true; }
    }

    private static string SafeError(Exception ex)
    {
        var safe = new DiagnosticRedactor().Text(ex.Message);
        return safe.Any(c => c is >= '\u3400' and <= '\u9fff') ? safe : "账号请求失败，请检查连接后重试。";
    }

    private static void AddField(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(control);
    }
}
