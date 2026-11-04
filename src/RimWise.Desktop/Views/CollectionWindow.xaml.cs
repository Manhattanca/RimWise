using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using RimWise.Formats;

namespace RimWise.Desktop.Views;

public partial class CollectionWindow : Window, INotifyPropertyChanged
{
    private readonly NamedCollectionStore store;
    private readonly OrderFileDocument? currentDocument;
    private readonly string currentSourceName;
    private readonly ObservableCollection<NamedCollectionSummary> collections = [];
    public bool HasCurrentDocument => currentDocument is not null;
    public bool HasSelection => CollectionList.SelectedItem is NamedCollectionSummary;
    public OrderFileDocument? LoadedDocument { get; private set; }
    public string LoadedName { get; private set; } = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;

    public CollectionWindow(NamedCollectionStore store, OrderFileDocument? currentDocument, string currentSourceName)
    {
        InitializeComponent();
        this.store = store;
        this.currentDocument = currentDocument;
        this.currentSourceName = currentSourceName;
        DataContext = this;
        CollectionList.ItemsSource = collections;
        RefreshCollections();
    }

    private void RefreshCollections(string? selectedId = null)
    {
        collections.Clear();
        foreach (var item in store.List()) collections.Add(item);
        CollectionList.SelectedItem = selectedId is null ? null : collections.FirstOrDefault(item => item.Id == selectedId);
        OnPropertyChanged(nameof(HasSelection));
    }

    private void Collection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CollectionList.SelectedItem is NamedCollectionSummary selected) NameBox.Text = selected.Name;
        OnPropertyChanged(nameof(HasSelection));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (currentDocument is null) return;
        try
        {
            var selected = CollectionList.SelectedItem as NamedCollectionSummary;
            store.Save(selected?.Id, NameBox.Text, currentDocument, currentSourceName);
            var saved = store.List().FirstOrDefault(item => string.Equals(item.Name, NameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            RefreshCollections(saved?.Id);
            StatusBar("已保存合集");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        { StatusBar(ex.Message); }
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionList.SelectedItem is not NamedCollectionSummary selected) return;
        try
        {
            var loaded = store.Read(selected.Id);
            LoadedDocument = loaded.Document;
            LoadedName = loaded.Summary.Name;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        { StatusBar(ex.Message); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionList.SelectedItem is not NamedCollectionSummary selected) return;
        if (MessageDialog.Show(this, $"删除合集“{selected.Name}”？不会修改游戏配置或模组文件。", "删除合集",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            store.Delete(selected.Id);
            RefreshCollections();
            NameBox.Clear();
            StatusBar("已删除合集");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        { StatusBar(ex.Message); }
    }

    private void StatusBar(string message) => StatusText.Text = message;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
