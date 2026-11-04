using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RimWise.Integration.AI;

namespace RimWise.Desktop.Views;

/// <summary>Recovery never sends a request. Selecting a saved result returns it for fresh editor validation.</summary>
public sealed class TaskRecoveryWindow : Window
{
    public string? SelectedTaskId { get; private set; }
    public TaskRecoveryWindow(AiTaskRecoveryStore store)
    {
        Title = "恢复任务"; Width = 800; Height = 470; MinWidth = 650; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(20) };
        var description = new TextBlock { Text = "恢复保存的进度与方案。未收到回复的请求不会自动重发；未回报用量按预估记账，可另行核对。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(description, Dock.Top); panel.Children.Add(description);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(list);
        var recover = new Button { Content = "恢复方案", IsEnabled = false };
        var usage = new Button { Content = "核对用量…", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        var discard = new Button { Content = "删除任务", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        var clean = new Button { Content = "清理已结束任务", Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "清理已完成或已取消任务的恢复数据，保留费用记录和待处理任务" };
        var close = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var b in new[] { recover, usage, discard, clean, close }) buttons.Children.Add(b);
        AiRecoverableTask? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as AiRecoverableTask;
        var deletedUsage = new Dictionary<string, AiDeletedTaskUsage>();
        void Refresh()
        {
            deletedUsage = store.InspectDeletedUsage().Where(t => t.UnknownUsage).ToDictionary(t => t.Id);
            list.ItemsSource = store.Inspect().Where(t => t.Problem is not null || t.Active || t.Checkpoint is { } s && (!s.Finished || s.UnknownUsage))
                .Concat(deletedUsage.Values.Select(t => new AiRecoverableTask(t.Id, null,
                    $"任务已删除，仅费用待核对 · {t.DeletedUtc.LocalDateTime:g} · 已报告 {t.ReportedTokens:N0} token")))
                .Select(t => new ListBoxItem { Tag = t, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = t.Checkpoint is { } s ? $"{s.StartedUtc.LocalDateTime:g} · {KindLabel(s.Kind)} · {Label(s)}\n已报告 {s.ReportedTokens:N0} token · 测试 {s.Attempts} 次"
                    : t.Active ? "任务正在其他窗口或进程中运行" : t.Problem } }).ToArray();
        }
        list.SelectionChanged += (_, _) =>
        {
            var task = Selected(); var s = task?.Checkpoint;
            recover.IsEnabled = s is { Finished: false } && (s.Kind == AiTaskKind.Maintenance && !s.HasUnresolvedRequest
                || s.Kind == AiTaskKind.Sorting && (s.Phase is AiTaskPhase.Applying or AiTaskPhase.Applied || s.Phase == AiTaskPhase.ReplySaved && s.MayApply));
            recover.Content = s?.Kind == AiTaskKind.Maintenance ? "继续任务" : "恢复方案";
            usage.IsEnabled = s?.UnknownUsage == true || task is not null && deletedUsage.ContainsKey(task.Id);
            discard.IsEnabled = task is { Active: false } && !deletedUsage.ContainsKey(task.Id);
            status.Text = task?.Problem ?? s?.Message ?? "";
        };
        void Run(Action action)
        {
            try { action(); Refresh(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
            { status.Text = ex.Message; }
        }
        recover.Click += (_, _) => { if (Selected() is { } t) { SelectedTaskId = t.Id; DialogResult = true; } };
        discard.Click += (_, _) => { if (Selected() is { } t) Run(() => store.Discard(t.Id)); };
        usage.Click += (_, _) => { if (Selected() is { } t) Run(() => Reconcile(store, t.Id)); };
        clean.Click += async (_, _) =>
        {
            clean.IsEnabled = list.IsEnabled = recover.IsEnabled = usage.IsEnabled = discard.IsEnabled = false;
            status.Text = "正在清理已结束任务…";
            try
            {
                var count = await System.Threading.Tasks.Task.Run(store.CleanFinishedHistory);
                Refresh();
                status.Text = $"已清理 {count} 项；费用记录已保留。";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
            { status.Text = ex.Message; }
            finally { clean.IsEnabled = list.IsEnabled = true; }
        };
        close.Click += (_, _) => Close();
        Refresh(); Content = panel;
    }

    private void Reconcile(AiTaskRecoveryStore store, string id)
    {
        var dialog = new Window { Owner = this, Title = "核对实际用量", Width = 480, Height = 310,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(StyleProperty, typeof(Window));
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "请填写该任务所有未回报请求的实际用量合计，不含已登记部分。不能确认时可关闭窗口，继续保留预估记录。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "输入 token" }); var input = new TextBox(); panel.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = "输出 token", Margin = new Thickness(0, 8, 0, 0) }); var output = new TextBox(); panel.Children.Add(output);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var save = new Button { Content = "登记已核对用量", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text, out var i) || !int.TryParse(output.Text, out var o)
                || i < 0 || o < 0 || (long)i + o > 10_000_000) { error.Text = "请输入有效的实际 token 数量。"; return; }
            try { store.ReconcileUsage(id, new(i, o, i + o)); dialog.DialogResult = true; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException)
            { error.Text = ex.Message; }
        };
        panel.Children.Add(save); dialog.Content = panel; dialog.ShowDialog();
    }

    private static string Label(AiTaskCheckpoint s) => s.UnknownUsage ? "请求用量待核对"
        : s.Phase switch { AiTaskPhase.ReplySaved => "方案已保存", AiTaskPhase.Applying => "应用时中断，待核对",
            AiTaskPhase.Applied => "已应用，可核对恢复", AiTaskPhase.Testing => "测试时中断", AiTaskPhase.Completed => "已完成",
            AiTaskPhase.Cancelled => "已取消", _ => "已暂停" };

    private static string KindLabel(AiTaskKind kind) => kind switch
    { AiTaskKind.Sorting => "自动排序", AiTaskKind.Maintenance => "自动维护", _ => "AI 连接测试" };
}
