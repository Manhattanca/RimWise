using System.Windows;
using System.Windows.Controls;
using RimWise.Desktop.Presentation;
using RimWise.Integration.AI;

namespace RimWise.Desktop.Views;

public partial class ConflictChoiceWindow : Window
{
    private readonly DeclaredConflict conflict;
    private readonly ConflictOptionRow[] rows;
    internal string[]? SelectedIds { get; private set; }
    internal IReadOnlyList<ConflictOptionRow> Rows => rows;

    public ConflictChoiceWindow(DeclaredConflict conflict) : this(conflict, id => new LoadOrderRow(id, 0)) { }

    internal ConflictChoiceWindow(DeclaredConflict conflict, Func<string, LoadOrderRow> resolveRow, bool badgeIcons = true)
    {
        InitializeComponent();
        this.conflict = conflict;
        DataContext = new { ShowModBadgeIcons = badgeIcons, ShowModBadgeText = !badgeIcons };
        var missing = conflict.MissingIds.Length > 0;
        if (missing) Title = "处理缺失模组";
        HeadingText.Text = missing ? "缺失模组无法下载" : conflict.RuntimeEvidence is null ? "模组声明不兼容"
            : $"{conflict.MemberIds.Count} 个模组的组合待排查";
        ReasonText.Text = conflict.Reason;
        ReasonText.ToolTip = conflict.Reason;
        var protectedOptions = conflict.Options.Where(option => option.BlockedReason is not null).ToArray();
        if (protectedOptions.Length > 0)
        {
            ProtectionWarning.Visibility = Visibility.Visible;
            ProtectionText.Text = "必须保留或受保护：" + string.Join("、", protectedOptions.Select(option => option.Name))
                + "。必须保留项可由你主动选择移出，本次选择将覆盖其保留保护；游戏本体及 DLC 仍不可移出。";
        }
        rows = conflict.Options.Select(option => new ConflictOptionRow(option, resolveRow(option.Identifier))).ToArray();
        ModsList.ItemsSource = rows;
        UpdatePreview();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rows is not null) UpdatePreview();
    }

    private void UpdatePreview()
    {
        var preview = ConflictChoicePreview.Create(conflict,
            ModsList.SelectedItems.Cast<ConflictOptionRow>().Select(row => row.Option.Identifier).ToArray(), allowRetainedRemoval: true);
        SelectedIds = preview.CanContinue ? preview.SelectedIds.ToArray() : null;
        ContinueButton.IsEnabled = preview.CanContinue;
        GroupStatusBorder.SetResourceReference(BackgroundProperty, preview.CanContinue ? "ConflictReadyBrush" : "ConflictPendingBrush");
        GroupStatusText.Text = preview.Message;
        if (conflict.MissingIds.Length == 0 && conflict.MemberIds.Count >= 3 && !preview.CanContinue)
            GroupStatusText.Text += "\n这是一个整体组合；不表示其中任意两个模组单独使用也会冲突。";
        foreach (var row in rows)
            row.State = preview.RemovedIds.Contains(row.Option.Identifier, StringComparer.OrdinalIgnoreCase)
                ? row.IsProtected ? "将移出（覆盖保留）" : ModsList.SelectedItems.Contains(row) ? "将移出排序" : "连带移出排序"
                : row.IsProtected ? row.Option.CanOverrideRetention ? "必须保留（可手动移出）" : "官方内容（不可移除）" : "保留";
        ImpactText.Text = preview.CanContinue
            ? "将移出排序：" + string.Join("、", preview.RemovedIds.Select(DisplayName))
                + (conflict.MissingIds.Length > 0 ? "。移除后继续排序。" : "。移除后继续测试；当前提示不代表复测已通过。")
            : "不移除将停止本次处理，保留当前排序。";
        ImpactText.ToolTip = ImpactText.Text;
    }

    private string DisplayName(string id) => conflict.Options.FirstOrDefault(option =>
        option.Identifier.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name is { } name && name != id
        ? $"{name}（{id}）" : id;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (SelectedIds is not null) DialogResult = true;
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

internal sealed class ConflictOptionRow(ConflictRemovalOption option, LoadOrderRow mod) : ObservableObject
{
    public ConflictRemovalOption Option { get; } = option;
    public LoadOrderRow Mod { get; } = mod;
    public string? MarkerColor => Mod.MarkerColor;
    public bool IsProtected => Option.BlockedReason is not null;
    public string StateTooltip => Option.BlockedReason ?? State;
    private string state = "保留";
    public string State
    {
        get => state;
        set { if (state == value) return; state = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateTooltip)); }
    }
}
