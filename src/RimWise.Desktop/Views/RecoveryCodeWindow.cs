using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using RimWise.Desktop.Services;

namespace RimWise.Desktop.Views;

/// <summary>A recovery code is shown only once, but remains selectable and explicitly copyable.</summary>
internal sealed class RecoveryCodeWindow : Window
{
    internal readonly TextBox CodeBox;
    internal readonly Button CopyButton;
    internal readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    internal RecoveryCodeWindow(string code, bool recovered, Action<string>? copy = null)
    {
        Title = "请保存恢复码";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = (recovered ? "密码已重置。" : "账号已创建。") + "请离线保存新的恢复码；关闭后无法再次查看。请勿分享给他人。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 10)
        });
        CodeBox = new TextBox { Text = code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 16,
            Height = double.NaN, MinHeight = 56, Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(CodeBox, "恢复码，可选中复制");
        root.Children.Add(CodeBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        CopyButton = new Button { Content = "复制恢复码", MinWidth = 110 };
        CopyButton.Click += (_, _) =>
        {
            try { (copy ?? Clipboard.SetText)(code); Status.Text = "恢复码已复制，请粘贴到安全位置保存。"; }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            { CodeBox.Focus(); CodeBox.SelectAll(); Status.Text = "暂时无法访问剪贴板，请按 Ctrl+C 复制已选中的恢复码。"; }
        };
        var close = new Button { Content = "已保存", MinWidth = 90, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        actions.Children.Add(CopyButton); actions.Children.Add(close);
        root.Children.Add(actions); root.Children.Add(Status);
        Content = root;
    }
}
