using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Views;

internal sealed class ThemeNameWindow : Window
{
    internal TextBox NameBox { get; } = new() { MaxLength = 60, Margin = new(0, 8, 0, 8) };
    internal Button ConfirmButton { get; } = new() { Content = "保存", IsDefault = true };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) };
    internal string? ThemeName { get; private set; }

    internal ThemeNameWindow(Window owner, string? name)
    {
        AppearanceEditorChrome.Attach(this);
        Owner = owner; Title = "保存主题"; Width = 440; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        NameBox.SetResourceReference(StyleProperty, "SearchInput");
        NameBox.Text = name ?? "自定义主题";
        AutomationProperties.SetName(NameBox, "主题名称");
        var content = new StackPanel { Margin = new(24) };
        content.Children.Add(new TextBlock { Text = "主题名称", FontWeight = FontWeights.SemiBold });
        content.Children.Add(NameBox);
        feedback.Text = "主题和背景图片副本将保存到用户数据文件夹。";
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); content.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true }); buttons.Children.Add(ConfirmButton);
        content.Children.Add(buttons); Content = content;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        ConfirmButton.Click += (_, _) =>
        {
            var chosen = NameBox.Text.Trim();
            try
            {
                AppearanceSettingsStore.ValidateName(chosen);
                var existing = ((App)Application.Current).Themes.SettingsStore.LoadThemes();
                if (existing.Any(theme => string.Equals(theme.Name, chosen, StringComparison.OrdinalIgnoreCase))
                    && MessageDialog.Show(this, "已存在同名主题，是否覆盖？", "保存主题", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                ThemeName = chosen; DialogResult = true;
            }
            catch (InvalidDataException ex) { feedback.Text = ex.Message; }
        };
    }
    internal static string? Prompt(Window owner, string? name)
    {
        var dialog = new ThemeNameWindow(owner, name);
        return dialog.ShowDialog() == true ? dialog.ThemeName : null;
    }
}
