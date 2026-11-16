using System.Windows;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop.Views;

public partial class MarketplaceDeleteWindow : Window
{
    public MarketplaceDeletionRequest? Selection { get; private set; }

    public MarketplaceDeleteWindow(IReadOnlyList<MarketplaceEntry> versions)
    {
        var groups = MarketplaceVersions.Group(versions);
        if (groups.Count != 1) throw new ArgumentException("请选择同一排序的版本。", nameof(versions));
        InitializeComponent();
        OrderTitle.Text = groups[0][0].Title + $" · 共 {groups[0].Count} 个版本";
        VersionPicker.ItemsSource = groups[0];
        VersionPicker.SelectedIndex = 0;
    }

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (VersionPicker is not null) VersionPicker.IsEnabled = SingleVersion.IsChecked == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (VersionPicker.SelectedItem is not MarketplaceEntry entry) return;
        Selection = new(entry, AllVersions.IsChecked == true ? MarketplaceDeletionScope.AllVersions : MarketplaceDeletionScope.SelectedVersion);
        DialogResult = true;
    }
}
