using System.IO;
using System.Text.Json;
using RimWise.Formats;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private readonly RetainedColorStore retainedColorStore = new(UserStorage.DirectoryPath);
    private HashSet<string> retainedColors = new(StringComparer.OrdinalIgnoreCase);
    private bool retainedColorsLoaded;
    public bool ColorRetentionAvailable => retainedColorsLoaded && markerColorsLoaded;
    public IReadOnlySet<string> RetainedColors => new HashSet<string>(retainedColors, StringComparer.OrdinalIgnoreCase);

    private void InitializeColorRetention()
    {
        try { retainedColors = retainedColorStore.Load(); retainedColorsLoaded = true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        { Status = "必须保留颜色规则读取失败，原文件保留：" + ex.Message; }
    }

    private bool IsRetainedByColor(string identifier) => markerColors.TryGetValue(identifier, out var color)
        && retainedColors.Contains(color);

    public void SetColorRetention(string color, bool required)
    {
        if (!ColorRetentionAvailable) throw new InvalidOperationException("颜色或保留规则读取失败，请修复记录后重启软件。");
        if (!OrderColors.IsColor(color)) throw new ArgumentException("颜色格式无效。", nameof(color));
        if (retainedColors.Contains(color) == required) return;
        var before = CaptureEditor();
        var next = new HashSet<string>(retainedColors, StringComparer.OrdinalIgnoreCase);
        if (required) next.Add(color.ToUpperInvariant()); else next.Remove(color);
        try { retainedColorStore.Save(next); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        { Status = "必须保留颜色规则未保存：" + ex.Message; return; }
        retainedColors = next;
        ApplyMarkerState();
        var name = MarkerPalette.Colors.FirstOrDefault(c => c.Hex.Equals(color, StringComparison.OrdinalIgnoreCase))?.Name ?? color;
        var operation = required ? $"设为必须保留：{name}色模组" : $"取消颜色保留规则：{name}";
        AppendHistory(new(before, CaptureEditor(), operation, "撤销" + operation));
        Status = operation;
    }
}
