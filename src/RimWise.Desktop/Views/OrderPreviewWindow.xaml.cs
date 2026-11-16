using System.Windows;
using RimWise.Core;

namespace RimWise.Desktop.Views;

public partial class OrderPreviewWindow : Window
{
    public OrderPreviewWindow(OrderComparison comparison, string title, string notice, string confirmLabel, string cancelLabel = "取消",
        ModCatalogue? catalogue = null, GameVersion? version = null)
    {
        InitializeComponent();
        Title = title;
        Summary.Text = $"{comparison.TargetCount} 项 · 新增 {comparison.Added} · 移出 {comparison.Removed} · 位置变化 {comparison.Moved}";
        Notice.Text = notice;
        PreviewGrid.ItemsSource = comparison.Entries.Select(entry => new PreviewRow(entry.IndexLabel,
            entry.Identifier, ModDisplayName.Resolve(catalogue, version, entry.Identifier), entry.Change)).ToArray();
        ConfirmButton.Content = confirmLabel;
        CancelButton.Content = cancelLabel;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    internal sealed record PreviewRow(string IndexLabel, string Identifier, string Name, string Change);
}
