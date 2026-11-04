using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using RimWise.Core;
using RimWise.Formats;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel : ObservableObject
{
    private ModsConfigDocument? document;
    private string searchText = string.Empty;
    private string sourceLabel = string.Empty;
    private string status = string.Empty;
    private LoadOrderRow? selectedRow;
    private bool hasChanges;
    private HashSet<string> savedOrderIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly RetainedModStore retainedStore = new(UserStorage.DirectoryPath);
    private HashSet<string> retainedIdentifiers = new(StringComparer.OrdinalIgnoreCase);

    public SnapshotCollection<LoadOrderRow> Rows { get; } = [];
    public ICollectionView VisibleRows { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand AddToOrderCommand { get; }
    public RelayCommand RemoveFromOrderCommand { get; }
    public RelayCommand ClearOrderCommand { get; }
    public bool HasDocument => document is not null;
    public bool HasChanges => hasChanges;
    private long contentRevision;
    // Catalogue/rule updates do not replace the document being imported.
    internal long DocumentRevision { get; private set; }
    internal long OrderRevision { get; private set; }
    public long ContentRevision
    {
        get => contentRevision;
        private set { contentRevision = value; OnPropertyChanged(); }
    }
    public string SourceLabel => sourceLabel;
    public string SourceFileName => document is null ? "—" : Path.GetFileName(sourceLabel);
    public string VersionLabel => document is null ? "—" :
        string.IsNullOrWhiteSpace(document.GameVersion) ? "文件未注明" : document.GameVersion;
    public string Status { get => status; set { status = value; OnPropertyChanged(); } }
    public string AvailableCountLabel => $"{VisibleAvailableMods.Cast<object>().Count()} / {AvailableMods.Count}";
    public string OrderCountLabel => $"{VisibleRows.Cast<object>().Count()} / {Rows.Count}";
    public string ChangeLabel => hasChanges ? "顺序已调整 · 尚未保存" : "无待保存调整";
    public int RetainedCount => RetainedPackageIds.Count;
    public bool HasRetainedMods => RetainedCount > 0;
    public string RetainedLabel => $"必须保留（{RetainedCount}）";
    public string SelectedPackage => selectedAvailable?.PackageId ?? selectedRow?.PackageId ?? "—";

    public string SearchText
    {
        get => searchText;
        set
        {
            searchText = value;
            VisibleRows.Refresh();
            VisibleAvailableMods.Refresh();
            OnPropertyChanged();
            RefreshCounts();
            RefreshCommands();
        }
    }

    public LoadOrderRow? SelectedRow
    {
        get => selectedRow;
        set
        {
            selectedRow = value;
            selectedOrderRows = value is null ? [] : [value];
            if (value is not null)
            {
                selectedAvailable = null;
                selectedAvailableRows = [];
                OnPropertyChanged(nameof(SelectedAvailable));
                OnPropertyChanged(nameof(SelectedAvailableRows));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedOrderRows));
            RefreshSelection();
            RefreshCommands();
        }
    }

    public MainViewModel()
    {
        VisibleRows = CollectionViewSource.GetDefaultView(Rows);
        VisibleRows.Filter = item => item is LoadOrderRow row
            && MatchesSource(row)
            && MatchesRecent(row)
            && (ContainsSearch(row.PackageId) || ContainsSearch(row.Name));
        InitializeCatalogueViews();
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanMove(1));
        AddToOrderCommand = new RelayCommand(AddSelectedToOrder, CanAddToOrder);
        RemoveFromOrderCommand = new RelayCommand(RemoveSelectedFromOrder, CanRemoveSelected);
        ClearOrderCommand = new RelayCommand(ClearOrder, () => HasDocument && Rows.Any(row => !KeepWhenClearing(row)));
        InitializeMarkers();
        InitializeHistory();
        InitializeSorting();
    }

    public void Load(ModsConfigDocument loaded, string label, bool resetHistory = true)
    {
        DocumentRevision++;
        OrderRevision++;
        ContentRevision++;
        document = loaded;
        if (resetHistory) savedOrderIds = loaded.Order.PackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        sourceDocument = null;
        sourceLabel = label;
        retainedIdentifiers = retainedStore.Load(label);
        SearchText = string.Empty;
        hasChanges = false;
        if (resetHistory) ResetHistory();
        SelectedRow = null;
        Rows.ReplaceWith(loaded.Order.PackageIds.Select((id, index) => new LoadOrderRow(id, index + 1)));
        ApplyRetainedState();
        RefreshCatalogueMatches();
        VisibleRows.Refresh();
        foreach (var property in new[] { nameof(HasDocument), nameof(SourceLabel), nameof(VersionLabel),
                     nameof(SearchText), nameof(ChangeLabel), nameof(HasChanges), nameof(SourceFileName) })
            OnPropertyChanged(property);
        Status = $"已导入 {Rows.Count} 项";
        RefreshCommands();
    }

    public ModsConfigDocument CreateExportDocument() =>
        (document ?? throw new InvalidOperationException("请先导入文件或载入演示。"))
        .WithOrder(new LoadOrder(Rows.Select(row => row.PackageId)));

    public void MarkExported(string? actionDescription = null)
    {
        savedOrderIds = Rows.Select(row => row.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        cleanState = editorState;
        UpdateDirtyState();
        if (actionDescription is not null) DescribeAction(actionDescription);
    }

    private bool CanMove(int offset)
    {
        if (!string.IsNullOrWhiteSpace(searchText)) return false;
        var selection = CurrentOrderSelection().ToHashSet();
        return selection.Any(row => Rows.IndexOf(row) + offset is var next
            && next >= 0 && next < Rows.Count && !selection.Contains(Rows[next]));
    }

    private void MoveSelected(int offset)
    {
        if (!CanMove(offset)) return;
        var before = CaptureEditor();
        var selection = CurrentOrderSelection();
        var primary = SelectedRow;
        var selected = selection.ToHashSet();
        var originalIndex = selection[0].Index;
        var result = Rows.ToList();
        var indices = offset < 0 ? Enumerable.Range(0, result.Count) : Enumerable.Range(0, result.Count).Reverse();
        foreach (var index in indices)
        {
            var next = index + offset;
            if (selected.Contains(result[index]) && next >= 0 && next < result.Count && !selected.Contains(result[next]))
                (result[index], result[next]) = (result[next], result[index]);
        }
        Rows.ReplaceWith(result);
        RenumberRows();
        SelectOrderRows(selection, primary);
        OrderEdited();
        RecordEdit(before, selection.Length == 1 ? $"{selection[0].Name} 位置修改到 {selection[0].Index}"
            : $"{selection.Length} 个模组{(offset < 0 ? "上移" : "下移")}一位",
            selection.Length == 1 ? $"撤销位置修改：{selection[0].Name} 位置修改到 {originalIndex}"
                : $"撤销位置修改：恢复 {selection.Length} 个模组的位置");
    }

    private void RefreshCommands()
    {
        MoveUpCommand?.Refresh();
        MoveDownCommand?.Refresh();
        AddToOrderCommand?.Refresh();
        RemoveFromOrderCommand?.Refresh();
        ClearOrderCommand?.Refresh();
        RefreshSortingCommand();
        UndoCommand?.Refresh();
        RedoCommand?.Refresh();
        BatchMarkCommand?.Refresh();
    }

    private bool CanRemoveSelected() => CurrentOrderSelection().Length > 0
        && CurrentOrderSelection().All(row => !row.IsRetained);
}
