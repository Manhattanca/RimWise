using System.IO;
using System.Text.Json;
using RimWise.Formats;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private readonly ModColorStore colorStore = new(UserStorage.DirectoryPath);
    private Dictionary<string, string> markerColors = new(StringComparer.OrdinalIgnoreCase);
    private bool markerColorsLoaded;

    private void InitializeColors()
    {
        try { markerColors = colorStore.Load(); markerColorsLoaded = true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Status = "颜色记录读取失败，原记录保留：" + ex.Message; }
    }

    public void ApplyColor(string? color, IEnumerable<LoadOrderRow> orderSelection, IEnumerable<InstalledModRow> availableSelection)
    {
        if (color is not null && !OrderColors.IsColor(color)) throw new ArgumentException("颜色格式无效。", nameof(color));
        var keys = orderSelection.Select(r => r.PackageId).Concat(availableSelection.Select(r => r.PackageId))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys.Length == 0) return;
        var before = CaptureEditor();
        var next = new Dictionary<string, string>(markerColors, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys) { if (color is null) next.Remove(key); else next[key] = color.ToUpperInvariant(); }
        if (next.Count == markerColors.Count && next.All(p => markerColors.TryGetValue(p.Key, out var previous) && previous == p.Value)) return;
        try { colorStore.Save(next); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Status = "颜色未保存：" + ex.Message; return; }
        markerColors = next;
        ApplyMarkerState();
        var description = color is null ? "清除颜色标记" : "设置颜色标记";
        AppendHistory(new(before, CaptureEditor(), $"{description}（{keys.Length} 个模组）", "撤销" + description));
        Status = $"已{description}（{keys.Length} 个模组）";
    }

    private void ImportColors(OrderFileDocument imported)
    {
        if (imported.Colors.Count == 0) return;
        var next = new Dictionary<string, string>(markerColors, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in imported.Colors) next[pair.Key] = pair.Value;
        colorStore.Save(next);
        markerColors = next;
        ApplyMarkerState();
    }
}
