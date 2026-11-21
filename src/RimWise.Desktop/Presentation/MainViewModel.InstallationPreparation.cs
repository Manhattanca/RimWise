using System.IO;
using RimWise.Core;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    // This is trusted preparation explicitly requested by starting sorting, not AI deletion authority.
    // Imports and ordinary manual editing never call it. Missing-item marks stay stored for undo/reinstall.
    internal void ApplyInstallationPreparation(SortingInstallationOrder prepared, long expectedRevision)
    {
        if (expectedRevision != ContentRevision || IsScanning || IsShowingCache || !HasDocument
            || CurrentCatalogue is null || !ColorRetentionAvailable)
            throw new InvalidOperationException("排序或安装资料已变化，未应用模组清理结果。");
        if (Rows.Select(r => r.PackageId).SequenceEqual(prepared.Order, StringComparer.OrdinalIgnoreCase)) return;
        if (prepared.Order.Length == 0 || prepared.Order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != prepared.Order.Length)
            throw new InvalidDataException("清理后没有可用游戏内容，未替换当前排序。");
        var before = CaptureEditor();
        // Preserve protection and colors when collapsing a former _steam alias to its actual package.
        foreach (var (oldId, nextId) in prepared.IdentifierMap.Where(p => !p.Key.Equals(p.Value, StringComparison.OrdinalIgnoreCase)))
        {
            if (IsRetained(oldId)) retainedIdentifiers.Add(nextId);
            if (markerColors.TryGetValue(oldId, out var color) && !markerColors.ContainsKey(nextId)) markerColors[nextId] = color;
        }
        try { retainedStore.Save(sourceLabel, retainedIdentifiers); colorStore.Save(markerColors); }
        catch
        {
            retainedIdentifiers = before.RetainedIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            markerColors = new(before.Colors, StringComparer.OrdinalIgnoreCase);
            try { retainedStore.Save(sourceLabel, retainedIdentifiers); colorStore.Save(markerColors); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { Status = "标记回滚未能写入，请核对保留记录：" + ex.Message; }
            throw;
        }
        Rows.ReplaceWith(prepared.Order.Select((id, index) => new LoadOrderRow(id, index + 1)));
        RefreshCatalogueMatches();
        OrderEdited();
        RecordEdit(before, "自动处理缺失模组与重复副本（未删除文件）", "撤销模组清理，恢复原排序项");
    }
}
