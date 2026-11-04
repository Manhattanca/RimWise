using System.IO;
using RimWise.Core;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    public bool IsRetained(string identifier) => retainedIdentifiers.Contains(identifier) || IsRetainedByColor(identifier);

    public void ToggleRetained(LoadOrderRow row)
    {
        if (!Rows.Contains(row)) return;
        ToggleRetained(row.PackageId, row.Name);
    }

    public void ToggleRetained(InstalledModRow row)
    {
        if (!AvailableMods.Contains(row)) return;
        ToggleRetained(row.PackageId, row.Name);
    }

    private void ToggleRetained(string identifier, string name)
    {
        if (IsRetainedByColor(identifier))
        {
            Status = "该模组按颜色必须保留；请在批量标记中取消该颜色的保留规则";
            return;
        }
        var before = CaptureEditor();
        var wasRetained = retainedIdentifiers.Contains(identifier);
        if (wasRetained) retainedIdentifiers.Remove(identifier);
        else retainedIdentifiers.Add(identifier);
        try
        {
            retainedStore.Save(sourceLabel, retainedIdentifiers);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            if (wasRetained) retainedIdentifiers.Add(identifier);
            else retainedIdentifiers.Remove(identifier);
            Status = "无法保存必须保留标记：" + ex.Message;
            return;
        }

        ApplyRetainedState();
        AppendHistory(new(before, CaptureEditor(), wasRetained ? $"取消必须保留：{name}" : $"标记必须保留：{name}",
            wasRetained ? $"恢复必须保留：{name}" : $"撤销必须保留：{name}"));
        Status = wasRetained ? "已取消必须保留标记" : "已标记为必须保留";
    }

    private void RestoreRetainedState(IEnumerable<string> identifiers)
    {
        retainedIdentifiers = identifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { retainedStore.Save(sourceLabel, retainedIdentifiers); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Status = "无法保存必须保留标记：" + ex.Message; }
        ApplyRetainedState();
    }
}
