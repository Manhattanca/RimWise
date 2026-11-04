using System.IO;
using RimWise.Formats;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private sealed record EditorSnapshot(OrderFileDocument File, string Label, long State, int SelectedIndex, int[] SelectedIndices,
        string[] AvailableDirectories, string[] RetainedIdentifiers, Dictionary<string, string[]> MarkerTags, Dictionary<string, string> Colors,
        string[] RetainedColors);
    private sealed record HistoryStep(EditorSnapshot Before, EditorSnapshot After, string Description, string UndoDescription, Action? UndoDisk = null,
        Action? RedoDisk = null, long CleanBefore = 0, long CleanAfter = 0,
        string[]? SavedBefore = null, string[]? SavedAfter = null);
    private readonly List<HistoryStep> undoHistory = [];
    private readonly List<HistoryStep> redoHistory = [];
    private long nextState;
    private long editorState;
    private long cleanState;
    private string lastAction = string.Empty;
    internal event Action? EditorChanged;
    internal void MarkRecoveredDirty() { cleanState = -1; UpdateDirtyState(); }
    public string LastAction => HasLastAction ? "上一步：" + lastAction : string.Empty;
    public bool HasLastAction => lastAction.Length > 0;
    public RelayCommand UndoCommand { get; private set; } = null!;
    public RelayCommand RedoCommand { get; private set; } = null!;

    private void InitializeHistory()
    {
        UndoCommand = new RelayCommand(() => ReplayHistory(undo: true), () => undoHistory.Count > 0);
        RedoCommand = new RelayCommand(() => ReplayHistory(undo: false), () => redoHistory.Count > 0);
    }

    private EditorSnapshot CaptureEditor() => new(CreateOrderFile(), sourceLabel, editorState,
        SelectedRow is null ? -1 : Rows.IndexOf(SelectedRow), CurrentOrderSelection().Select(Rows.IndexOf).ToArray(),
        selectedAvailableRows.Select(row => row.Installation.Directory).ToArray(), retainedIdentifiers.ToArray(),
        markerTags.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
        new(markerColors, StringComparer.OrdinalIgnoreCase), retainedColors.ToArray());

    private void RecordEdit(EditorSnapshot before, string description, string undoDescription)
    {
        editorState = ++nextState;
        AppendHistory(new(before, CaptureEditor(), description, undoDescription));
        UpdateDirtyState();
    }

    private void AppendHistory(HistoryStep step)
    {
        undoHistory.Add(step);
        // Bound memory without deleting any on-disk recovery backup.
        if (undoHistory.Count > 100) undoHistory.RemoveAt(0);
        redoHistory.Clear();
        DescribeAction(step.Description);
        RefreshCommands();
        EditorChanged?.Invoke();
    }

    public void RecordGameSave(Action undoDisk, Action redoDisk, OrderFileDocument? replacement = null, string? label = null)
    {
        var snapshot = CaptureEditor();
        var previousClean = cleanState;
        var previousSaved = savedOrderIds.ToArray();
        if (replacement is not null)
        {
            Load(replacement.Configuration, label ?? sourceLabel, resetHistory: false);
            sourceDocument = replacement;
            editorState = ++nextState;
        }
        cleanState = editorState;
        savedOrderIds = Rows.Select(row => row.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operation = replacement is null ? "保存排序" : "恢复游戏配置";
        AppendHistory(new(snapshot, CaptureEditor(), $"{operation}（{Rows.Count} 项）",
            $"撤销{operation}，游戏配置已回滚", undoDisk, redoDisk, previousClean, cleanState, previousSaved, savedOrderIds.ToArray()));
        UpdateDirtyState();
    }

    private void ReplayHistory(bool undo)
    {
        var source = undo ? undoHistory : redoHistory;
        if (source.Count == 0) return;
        var step = source[^1];
        try
        {
            // Commit the disk transition first. If changed externally, leave both UI and history intact.
            (undo ? step.UndoDisk : step.RedoDisk)?.Invoke();
            RestoreEditor(undo ? step.Before : step.After);
            if (step.UndoDisk is not null) cleanState = undo ? step.CleanBefore : step.CleanAfter;
            if ((undo ? step.SavedBefore : step.SavedAfter) is { } saved)
                savedOrderIds = saved.ToHashSet(StringComparer.OrdinalIgnoreCase);
            source.RemoveAt(source.Count - 1);
            (undo ? redoHistory : undoHistory).Add(step);
            UpdateDirtyState();
            RefreshCommands();
            DescribeAction(undo ? step.UndoDescription : "取消撤销：" + step.Description);
            Status = step.UndoDisk is null ? (undo ? "已撤销上一步操作" : "已取消撤销")
                : undo ? "已撤销保存，游戏配置已回滚" : "已取消撤销，游戏配置已更新";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Status = "操作未完成：" + ex.Message;
        }
    }

    private void RestoreEditor(EditorSnapshot snapshot)
    {
        // Persist before changing the view so a failed color restore leaves the editor intact.
        var rulesChanged = !retainedColors.SetEquals(snapshot.RetainedColors);
        var colorsChanged = markerColors.Count != snapshot.Colors.Count
            || markerColors.Any(pair => !snapshot.Colors.TryGetValue(pair.Key, out var value) || value != pair.Value);
        if ((rulesChanged || colorsChanged) && !ColorRetentionAvailable)
            throw new InvalidDataException("颜色或保留规则不可用，无法恢复标记。");
        if (rulesChanged) retainedColorStore.Save(snapshot.RetainedColors);
        try { if (colorsChanged) colorStore.Save(snapshot.Colors); }
        catch
        {
            if (rulesChanged)
            {
                try { retainedColorStore.Save(retainedColors); }
                catch { retainedColorsLoaded = false; }
            }
            throw;
        }
        markerColors = new(snapshot.Colors, StringComparer.OrdinalIgnoreCase);
        retainedColors = snapshot.RetainedColors.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Load(snapshot.File.Configuration, snapshot.Label, resetHistory: false);
        sourceDocument = snapshot.File;
        editorState = snapshot.State;
        RestoreRetainedState(snapshot.RetainedIdentifiers);
        markerTags = snapshot.MarkerTags.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        try { tagStore.Save(markerTags); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Status = "标签记录恢复失败：" + ex.Message; }
        ApplyMarkerState();
        SelectOrderRows(snapshot.SelectedIndices.Where(index => index >= 0 && index < Rows.Count).Select(index => Rows[index]),
            snapshot.SelectedIndex >= 0 && snapshot.SelectedIndex < Rows.Count ? Rows[snapshot.SelectedIndex] : null);
        if (snapshot.AvailableDirectories.Length > 0)
            SelectAvailableRows(AvailableMods.Where(row => snapshot.AvailableDirectories.Contains(row.Installation.Directory, StringComparer.OrdinalIgnoreCase)));
    }

    public void ReplaceFileUndoable(OrderFileDocument loaded, string label, string operation = "导入排序")
    {
        var before = HasDocument ? CaptureEditor() : null;
        ImportColors(loaded);
        Load(loaded.Configuration, label, resetHistory: false);
        sourceDocument = loaded;
        if (before is null)
        {
            editorState = ++nextState;
            cleanState = editorState;
            undoHistory.Clear();
            redoHistory.Clear();
            DescribeAction($"{operation}：{Path.GetFileName(label)}（{Rows.Count} 项）");
        }
        else RecordEdit(before, $"{operation}：{Path.GetFileName(label)}（{Rows.Count} 项）",
            $"撤销{operation}：恢复原清单（{before.File.Configuration.Order.PackageIds.Count} 项）");
        RefreshCommands();
    }

    private void ResetHistory()
    {
        undoHistory.Clear();
        redoHistory.Clear();
        editorState = ++nextState;
        cleanState = editorState;
        DescribeAction(string.Empty);
    }

    public void ReloadCurrentOrder(ModsConfigDocument loaded, string label)
    {
        var previousAction = lastAction;
        Load(loaded, label);
        DescribeAction(previousAction);
    }

    private void DescribeAction(string description)
    {
        lastAction = description;
        OnPropertyChanged(nameof(LastAction));
        OnPropertyChanged(nameof(HasLastAction));
    }

    private void UpdateDirtyState()
    {
        hasChanges = editorState != cleanState;
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangeLabel));
        EditorChanged?.Invoke();
    }
}
