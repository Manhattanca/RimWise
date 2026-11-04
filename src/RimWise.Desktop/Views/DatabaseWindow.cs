using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using RimWise.Integration.Databases;

namespace RimWise.Desktop.Views;

public sealed class DatabaseWindow : Window
{
    private readonly DatabaseStore store;
    private readonly Func<IProgress<string>?, CancellationToken, Task<DatabaseSnapshot>>? updateDatabase;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? updating;
    private DatabaseSettings active = DatabaseSettings.Default;
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Button update = new() { Content = "保存并应用" };
    private readonly Button cancel = new() { Content = "取消操作", IsEnabled = false };
    internal DatabaseSourceEditor CommunityEditor { get; } = new();
    internal DatabaseSourceEditor WorkshopEditor { get; } = new();
    public DatabaseWindow(string applicationDirectory) : this(applicationDirectory, null) { }
    internal DatabaseWindow(string applicationDirectory,
        Func<IProgress<string>?, CancellationToken, Task<DatabaseSnapshot>>? updateDatabase)
    {
        store = new(applicationDirectory); this.updateDatabase = updateDatabase;
        Title = "数据库来源"; Width = 940; Height = 740; MinWidth = 720; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(StyleProperty, typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(20) };
        var footer = new StackPanel(); footer.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "关闭", IsCancel = true };
        close.Click += (_, _) => Close(); update.Click += async (_, _) => await UpdateAsync();
        cancel.Click += (_, _) => updating?.Cancel();
        foreach (var button in new[] { update, cancel, close }) buttons.Children.Add(button);
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var content = new StackPanel(); content.Children.Add(details);
        content.Children.Add(CommunityEditor); content.Children.Add(WorkshopEditor);
        panel.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = panel;
        CommunityEditor.ReadRequested += async () => await ApplyAsync(true);
        WorkshopEditor.ReadRequested += async () => await ApplyAsync(false);
        SetEditors(active); SetBusy(true);
        Loaded += async (_, _) =>
        {
            try
            {
                var loaded = await Task.Run(() => (Settings: store.LoadSettings(), Data: store.Load()), lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                active = loaded.Settings; SetEditors(active); ShowDatabase(loaded.Data);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (DatabaseStore.Recoverable(ex)) { status.Text = "数据库无法载入，请重新配置：" + ex.Message; }
            finally { if (!lifetime.IsCancellationRequested) SetBusy(false); }
        };
    }
    internal Button UpdateButton => update;
    internal Button CancelButton => cancel;
    internal TextBlock StatusText => status;
    internal TextBlock DetailsText => details;
    private void SetEditors(DatabaseSettings settings)
    {
        CommunityEditor.SetSource("社区规则数据库", settings.Community);
        WorkshopEditor.SetSource("Steam 创意工坊数据库", settings.Workshop);
    }
    private void SetBusy(bool busy)
    {
        update.IsEnabled = CommunityEditor.IsEnabled = WorkshopEditor.IsEnabled = !busy;
        cancel.IsEnabled = busy && updating is not null;
    }
    private void ShowDatabase(DatabaseLoad loaded)
    {
        if (lifetime.IsCancellationRequested) return;
        var snapshot = loaded.Snapshot;
        details.Text = $"社区规则：{snapshot.RulePackages:N0} 个模组 · {snapshot.CommunityOrigin}\n"
            + $"工坊包名索引：{snapshot.IndexedItems:N0} 项 · {snapshot.WorkshopOrigin}";
        status.Text = loaded.Status;
    }
    internal Task UpdateAsync() => ApplyAsync(null);
    private async Task ApplyAsync(bool? communityOnly)
    {
        if (updating is not null || !update.IsEnabled) return;
        // A section's Download button must not apply edits made in the other section.
        var settings = communityOnly switch
        {
            true => active with { Community = CommunityEditor.Value },
            false => active with { Workshop = WorkshopEditor.Value },
            _ => new DatabaseSettings(CommunityEditor.Value, WorkshopEditor.Value)
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updating = cancellation; SetBusy(true);
        try
        {
            var progress = new Progress<string>(message =>
            { if (!lifetime.IsCancellationRequested && ReferenceEquals(updating, cancellation)) status.Text = message; });
            var snapshot = await Task.Run(() => updateDatabase is not null ? updateDatabase(progress, cancellation.Token)
                : store.ApplyAsync(settings, communityOnly == true, communityOnly == false, progress, cancellation.Token), cancellation.Token);
            active = settings;
            ShowDatabase(new(snapshot, "数据库已应用；进行中的任务继续使用原数据库"));
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) status.Text = "已取消，保留原配置和数据库"; }
        catch (Exception ex) when (DatabaseStore.Recoverable(ex) || ex is HttpRequestException or InvalidOperationException or KeyNotFoundException)
        { if (!lifetime.IsCancellationRequested) status.Text = "应用失败，保留原配置和数据库：" + ex.Message; }
        finally { updating = null; if (!lifetime.IsCancellationRequested) SetBusy(false); }
    }
    protected override void OnClosed(EventArgs e) { lifetime.Cancel(); base.OnClosed(e); }
}
