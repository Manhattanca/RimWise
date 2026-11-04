using System.IO;
using System.Windows;
using System.Windows.Controls;
using RimWise.Integration.Safety;

namespace RimWise.Desktop.Views;

public sealed class ModRestoreWindow : Window
{
    public ModRestoreWindow(LocalModArchive archive, Action ensureClosed, Action restored)
    {
        Title = "恢复移除的模组"; Width = 700; Height = 420; MinWidth = 520; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(20) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var restore = new Button { Content = "恢复到本地 Mods", IsEnabled = false };
        var close = new Button { Content = "关闭", IsCancel = true };
        actions.Children.Add(restore); actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        var list = new ListBox { DisplayMemberPath = nameof(ArchivedMod.Name), ItemsSource = archive.Read() };
        panel.Children.Add(list);
        list.SelectionChanged += (_, _) =>
        {
            var item = list.SelectedItem as ArchivedMod;
            restore.IsEnabled = item?.CanRestore == true;
            status.Text = item?.Status ?? "选择需要恢复的本地模组";
        };
        restore.Click += (_, _) =>
        {
            if (list.SelectedItem is not ArchivedMod { CanRestore: true } item) return;
            try
            {
                ensureClosed(); archive.Restore(item.Id); restored();
                list.ItemsSource = archive.Read(); status.Text = "已恢复模组，排序保持不变";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { status.Text = ex.Message; }
        };
        close.Click += (_, _) => Close();
        Content = panel;
    }
}
