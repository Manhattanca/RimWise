using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RimWise.Core;
using RimWise.Desktop.Presentation;

namespace RimWise.Desktop.Views;

public partial class OrderIssuesWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly Action<DiagnosticRow> locate;
    private readonly ICollectionView issues;
    private DiagnosticSeverity severity;

    public OrderIssuesWindow(MainViewModel viewModel, Action<DiagnosticRow> locate)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.locate = locate;
        // Own view: filtering this window must not filter the shared diagnostics collection.
        issues = new ListCollectionView(viewModel.Diagnostics);
        issues.Filter = value => value is DiagnosticRow row && row.Diagnostic.Severity == severity;
        IssuesGrid.ItemsSource = issues;
        viewModel.Diagnostics.CollectionChanged += Diagnostics_Changed;
    }

    public void ShowSeverity(DiagnosticSeverity value)
    {
        severity = value;
        issues.Refresh();
        RefreshNotice();
    }

    private void Diagnostics_Changed(object? sender, NotifyCollectionChangedEventArgs e) => RefreshNotice();
    private void RefreshNotice()
    {
        Title = $"{(severity == DiagnosticSeverity.Error ? "错误" : "警告")} · {issues.Cast<object>().Count()} 项";
        EmptyNotice.Visibility = issues.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        RefreshLocate();
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => RefreshLocate();
    private void RefreshLocate() => LocateButton.IsEnabled = IssuesGrid.SelectedItem is DiagnosticRow row
        && row.Identifier.Length > 0 && viewModel.Diagnostics.Contains(row);
    private void Locate_Click(object sender, RoutedEventArgs e) => LocateSelected();
    internal void LocateSelected()
    {
        if (IssuesGrid.SelectedItem is DiagnosticRow row && viewModel.Diagnostics.Contains(row) && row.Identifier.Length > 0)
            locate(row);
    }
    private void Issue_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(IssuesGrid, source) is DataGridRow)
            LocateSelected();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosed(EventArgs e)
    {
        viewModel.Diagnostics.CollectionChanged -= Diagnostics_Changed;
        base.OnClosed(e);
    }
}
