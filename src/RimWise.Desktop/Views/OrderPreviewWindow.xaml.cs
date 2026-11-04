using System.Windows;
using RimWise.Core;

namespace RimWise.Desktop.Views;

public partial class OrderPreviewWindow : Window
{
    public OrderPreviewWindow(OrderComparison comparison, string title, string notice, string confirmLabel, string cancelLabel = "取消")
    {
        InitializeComponent();
        Title = title;
        Summary.Text = $"{comparison.TargetCount} 项 · 新增 {comparison.Added} · 移出 {comparison.Removed} · 位置变化 {comparison.Moved}";
        Notice.Text = notice;
        PreviewGrid.ItemsSource = comparison.Entries;
        ConfirmButton.Content = confirmLabel;
        CancelButton.Content = cancelLabel;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
