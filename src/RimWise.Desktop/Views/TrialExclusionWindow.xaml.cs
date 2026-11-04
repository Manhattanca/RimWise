using System.Windows;
using RimWise.Desktop.Presentation;

namespace RimWise.Desktop.Views;

public partial class TrialExclusionWindow : Window
{
    internal TrialExclusionWindow(IReadOnlyList<TrialRemovalRow> rows, bool warnings, bool badgeIcons = true, bool existingErrorsOnly = false)
    {
        InitializeComponent();
        DataContext = new { ShowModBadgeIcons = badgeIcons, ShowModBadgeText = !badgeIcons };
        ModsList.ItemsSource = rows;
        StatusText.Text = (warnings ? "复测未发现红字，仍有黄字警告。" : "复测未发现红字。")
            + "此结果针对整组移除方案，不能证明其中每个模组单独有错。";
        if (rows.Count == 0)
        {
            HeadingText.Text = "排序完成";
            StatusText.Text = warnings ? "分阶段测试已通过，无红字；黄字警告已保留。" : "分阶段测试已通过，无红字。";
            ChoiceText.Text = "排序已保存，无需移除模组。本次测试未覆盖游戏内全部内容。";
            ModsList.Visibility = KeepButton.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = "确定";
            ConfirmButton.IsDefault = true;
            Height = 300;
            MinHeight = 280;
        }
        if (existingErrorsOnly)
        {
            HeadingText.Text = "新增 Mod 定位完成";
            StatusText.Text = "完整对照测试仅重现原排序已有错误，未观察到新增红字。原有红字仍然存在。";
            if (rows.Count > 0) StatusText.Text += "此结果针对移出下列新增项后的组合；保留它们可能重现新增错误。";
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Keep_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>The same presentation model as the editor, paired with the tested dependent closure.</summary>
internal sealed record TrialRemovalRow(LoadOrderRow Mod, string Treatment)
{
    public string? MarkerColor => Mod.MarkerColor;
}
