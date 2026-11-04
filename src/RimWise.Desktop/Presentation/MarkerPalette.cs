using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RimWise.Desktop.Presentation;

public sealed record MarkerColor(string Name, string Hex);

public static class MarkerPalette
{
    public static IReadOnlyList<MarkerColor> Colors { get; } = Array.AsReadOnly(new[]
    {
        new MarkerColor("珊瑚", "#EF705F"), new("玫瑰", "#D34E83"), new("樱粉", "#ED85B5"), new("桑葚", "#A04A8E"),
        new("紫藤", "#9B79D0"), new("鸢尾", "#6D68C5"), new("矢车菊", "#4C83D1"), new("晴空", "#49B0DC"),
        new("湖蓝", "#36B7C3"), new("薄荷", "#62BEA0"), new("草绿", "#6FA454"), new("青柠", "#A4BC4A"),
        new("柠檬", "#DEC64D"), new("琥珀", "#DDA047"), new("蜜橙", "#ED8A4D"), new("陶土", "#B77C64")
    });
}

/// <summary>Theme-aware tints keep diagnostics legible, with alternating brightness even in one color group.</summary>
public sealed class MarkerBackgroundConverter : IMultiValueConverter
{
    private readonly Dictionary<(Color Basis, string? Hex, bool Odd), SolidColorBrush> brushes = [];
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[2] is not SolidColorBrush panel) return DependencyProperty.UnsetValue;
        var basis = panel.Color;
        var odd = values[1] is int index && index % 2 == 1;
        var hex = values[0] is string { Length: 7 } text ? text : null;
        var key = (basis, hex, odd);
        if (brushes.TryGetValue(key, out var cached)) return cached;
        var color = hex is null ? (basis.R < 100 ? Colors.White : Colors.Black) : (Color)ColorConverter.ConvertFromString(hex);
        var amount = hex is null ? (odd ? .025 : 0) : basis.R < 100 ? (odd ? .32 : .24) : (odd ? .25 : .18);
        var brush = Brush(Blend(basis, color, amount));
        // Theme colors are part of the key; imported colors cannot grow this cache unbounded.
        if (brushes.Count >= 256) brushes.Clear();
        brushes[key] = brush;
        return brush;
    }
    private static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));
    private static SolidColorBrush Brush(Color value) { var brush = new SolidColorBrush(value); brush.Freeze(); return brush; }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
