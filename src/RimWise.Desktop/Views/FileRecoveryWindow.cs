using System.IO;
using System.Windows;
using System.Windows.Controls;
using RimWise.Formats;

namespace RimWise.Desktop.Views;

public sealed class FileRecoveryWindow : Window
{
    public FileRecoveryWindow(GameOrderStore store, Action ensureClosed, Action<GameOrderPlan> restore)
    {
        Title = "恢复中断写入"; Width = 720; Height = 440; MinWidth = 540; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(20) };
        var description = new TextBlock { Text = "选择中断的配置写入。恢复前会显示顺序对比；存在外部改动时禁止覆盖。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(description, Dock.Top); panel.Children.Add(description);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var list = new ListBox { DisplayMemberPath = nameof(GameRecoveryItem.Description), ItemsSource = store.InspectInterrupted() };
        panel.Children.Add(list);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Insert(2, status);
        var keep = new Button { Content = "保留当前文件" };
        var recover = new Button { Content = "恢复原配置…" };
        var close = new Button { Content = "关闭", IsCancel = true };
        buttons.Children.Add(keep); buttons.Children.Add(recover); buttons.Children.Add(close);
        void Refresh() => list.ItemsSource = store.InspectInterrupted();
        void Run(Action action)
        {
            try { ensureClosed(); action(); Refresh(); status.Text = "处理完成，原备份已保留"; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { status.Text = ex.Message; Refresh(); }
        }
        keep.Click += (_, _) => { if (list.SelectedItem is GameRecoveryItem item) Run(() => store.ResolveInterrupted(item.Id)); };
        recover.Click += (_, _) => { if (list.SelectedItem is GameRecoveryItem item) Run(() =>
        {
            restore(store.PrepareInterruptedRestore(item.Id));
            // A cancelled preview leaves the journal pending.
            if (store.InspectInterrupted().Any(x => x.Id == item.Id && x.State == GameRecoveryState.Unchanged)) store.ResolveInterrupted(item.Id);
        }); };
        close.Click += (_, _) => Close();
        list.SelectionChanged += (_, _) =>
        {
            var item = list.SelectedItem as GameRecoveryItem;
            keep.IsEnabled = item?.State is GameRecoveryState.Unchanged or GameRecoveryState.Applied;
            recover.IsEnabled = item?.State == GameRecoveryState.Applied;
            status.Text = item is null ? "" : $"{item.TimeUtc.ToLocalTime():g}\n{item.Description}";
        };
        keep.IsEnabled = recover.IsEnabled = false;
        Content = panel;
    }
}
