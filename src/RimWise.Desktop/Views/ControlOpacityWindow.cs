using System.Windows;
using System.Windows.Controls;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Views;

/// <summary>A single control inspector. X/Escape cancel only this inspector; the enclosing theme remains a draft.</summary>
internal sealed class ControlOpacityWindow : Window
{
    internal ComboBox ModeBox { get; } = new() { Margin = new(0, 12, 0, 8) };
    internal Slider ValueSlider { get; } = new() { Minimum = 0, Maximum = 100, SmallChange = 1 };
    internal Button ApplyButton { get; } = new() { Content = "应用", IsDefault = true };
    internal SurfaceOpacitySetting Rule => new() { Mode = (SurfaceOpacityMode)ModeBox.SelectedIndex, Opacity = ValueSlider.Value / 100 };
    internal ControlOpacityWindow(Window owner, string label, SurfaceOpacitySetting? rule, Action<SurfaceOpacitySetting> preview)
    {
        AppearanceEditorChrome.Attach(this);
        Owner = owner; Title = "单独调整透明度"; Width = 360; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource(typeof(Window));
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "仅调整所选控件的底色，不影响文字、图标或其他按钮。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        foreach (var text in new[] { "跟随同类 UI 设置", "独立透明度", "保持不透明" }) ModeBox.Items.Add(new ComboBoxItem { Content = text });
        panel.Children.Add(ModeBox);
        ValueSlider.Style = (Style)FindResource("AppearanceSlider"); panel.Children.Add(ValueSlider);
        var amount = new TextBlock { Margin = new(0, 6, 0, 14) }; panel.Children.Add(amount);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true }; footer.Children.Add(cancel); footer.Children.Add(ApplyButton); panel.Children.Add(footer);
        var root = new Border { Child = panel }; root.SetResourceReference(Border.BackgroundProperty, "PageBrush"); Content = root;
        rule ??= new(); ModeBox.SelectedIndex = (int)rule.Mode; ValueSlider.Value = rule.Opacity * 100;
        void Update(bool publish)
        {
            ValueSlider.IsEnabled = ModeBox.SelectedIndex == 1;
            amount.Text = ModeBox.SelectedIndex == 1 ? $"不透明度 {ValueSlider.Value:0}%（0% 完全透明）"
                : ModeBox.SelectedIndex == 2 ? "始终 100% 不透明" : "使用统一设置；不保留此控件的独立值";
            if (publish) preview(Rule);
        }
        ModeBox.SelectionChanged += (_, _) => Update(true); ValueSlider.ValueChanged += (_, _) => Update(true);
        ApplyButton.Click += (_, _) => DialogResult = true;
        System.Windows.Automation.AutomationProperties.SetName(ModeBox, "此控件的透明度模式");
        System.Windows.Automation.AutomationProperties.SetName(ValueSlider, "此控件不透明度");
        Update(false);
    }
}
