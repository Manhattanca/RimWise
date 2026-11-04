using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RimWise.Core;

namespace RimWise.Desktop.Views;

public sealed class OrderRulesWindow : Window
{
    private readonly ObservableCollection<CustomOrderRule> rules;
    public IReadOnlyList<CustomOrderRule> Result => rules.ToArray();
    public OrderRulesWindow(IReadOnlyList<CustomOrderRule> existing, IEnumerable<(string Id, string Name)> mods)
    {
        rules = new(existing); Title = "自定义排序规则"; Width = 830; Height = 570; MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(20) };
        var choices = mods.DistinctBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new Choice(mod.Id, mod.Name + " · " + mod.Id)).ToArray();
        var before = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", MinWidth = 180, MaxWidth = 300, Margin = new Thickness(0,0,8,0), ToolTip = "选择排在前面的模组" };
        var after = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", MinWidth = 180, MaxWidth = 300, Margin = new Thickness(8,0,8,0), ToolTip = "选择排在后面的模组" };
        var add = new Button { Content = "添加" };
        void UpdateAdd() => add.IsEnabled = before.SelectedItem is Choice a && after.SelectedItem is Choice b
            && !a.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase)
            && !rules.Any(rule => rule.Before.Equals(a.Id, StringComparison.OrdinalIgnoreCase)
                && rule.After.Equals(b.Id, StringComparison.OrdinalIgnoreCase));
        before.SelectionChanged += (_, _) => UpdateAdd(); after.SelectionChanged += (_, _) => UpdateAdd();
        rules.CollectionChanged += (_, _) => UpdateAdd();
        before.SelectedIndex = choices.Length > 0 ? 0 : -1;
        after.SelectedIndex = choices.Length > 1 ? 1 : -1;
        UpdateAdd();
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) }; top.Children.Add(before);
        top.Children.Add(new TextBlock { Text = "在此之前", VerticalAlignment = VerticalAlignment.Center }); top.Children.Add(after); top.Children.Add(add);
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var list = new ListBox { ItemsSource = rules };
        list.ItemTemplate = MakeRuleTemplate();
        add.Click += (_, _) =>
        {
            if (before.SelectedItem is not Choice a || after.SelectedItem is not Choice b || a.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase)) return;
            var rule = new CustomOrderRule(a.Id, b.Id);
            if (!rules.Contains(rule)) rules.Add(rule);
        };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,14,0,0) };
        var remove = new Button { Content = "删除选中规则", IsEnabled = false };
        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is CustomOrderRule;
        remove.Click += (_, _) => { if (list.SelectedItem is CustomOrderRule r) rules.Remove(r); };
        var save = new Button { Content = "保存", IsDefault = true }; save.Click += (_, _) => DialogResult = true;
        var close = new Button { Content = "取消", IsCancel = true };
        bottom.Children.Add(remove); bottom.Children.Add(close); bottom.Children.Add(save); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        panel.Children.Add(list); Content = panel;
    }
    private static DataTemplate MakeRuleTemplate()
    {
        // Two bound fields keep identifiers readable without converting editable records to strings.
        var template = new DataTemplate(typeof(CustomOrderRule));
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        foreach (var field in new[] { "Before", "After" })
        {
            if (field == "After") { var arrow = new FrameworkElementFactory(typeof(TextBlock)); arrow.SetValue(TextBlock.TextProperty, "  →  "); row.AppendChild(arrow); }
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(field)); row.AppendChild(text);
        }
        template.VisualTree = row; return template;
    }
    private sealed record Choice(string Id, string Name);
}
