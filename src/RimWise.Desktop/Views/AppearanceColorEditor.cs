using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RimWise.Desktop.Services;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Views;

/// <summary>Inline visual selection: no required color-code input or nested modal.</summary>
public sealed class AppearanceColorEditor : Expander
{
    private readonly Border swatch = new() { Width = 24, Height = 20, CornerRadius = new(3), Margin = new(0, 0, 10, 0) };
    private readonly TextBlock description = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider hue = new() { Minimum = 0, Maximum = 360, Margin = new(0, 6, 0, 8) };
    private readonly Grid plane = new() { Height = 112, Focusable = true, Cursor = Cursors.Cross, ClipToBounds = true };
    private readonly Border hueSurface = new();
    private readonly Ellipse marker = new() { Width = 10, Height = 10, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    private bool updating;
    private double saturation = .7, brightness = .8;
    private string? selectedColor;
    public string ResourceKey { get; set; } = "AccentBrush";
    public string ColorLabel { get; set; } = "颜色";
    public event EventHandler? ColorChanged;
    public string? SelectedColor
    {
        get => selectedColor;
        set => SetColor(value);
    }
    public AppearanceColorEditor()
    {
        hue.SetResourceReference(StyleProperty, "AppearanceSlider");
        hue.SetResourceReference(BackgroundProperty, "AppearanceHueTrack");
        IsEnabledChanged += (_, _) => RefreshColor();
        MinHeight = 34;
        SetResourceReference(ForegroundProperty, "TextBrush");
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        swatch.BorderThickness = new(1); swatch.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 3, 0, 3) };
        header.Children.Add(swatch); header.Children.Add(description); Header = header;
        var body = new StackPanel { Margin = new(4, 10, 4, 4) }; Content = body;
        var colors = new WrapPanel { Margin = new(-3, 0, 0, 8) };
        foreach (var (label, color) in new[] { ("青绿", "#337F73"), ("蓝色", "#4398DB"), ("紫色", "#A385DA"),
            ("粉色", "#D86A9A"), ("红色", "#D95858"), ("橙色", "#DDA044"), ("白色", "#FFFFFF"), ("灰色", "#777777"), ("黑色", "#101214") })
        {
            var button = new Button { Width = 25, Height = 25, MinWidth = 0, Padding = new(0), Margin = new(3), ToolTip = label };
            button.Background = ThemeService.Brush((Color)ColorConverter.ConvertFromString(color));
            AutomationProperties.SetName(button, "选择" + label);
            button.Click += (_, _) => SetColor(color); colors.Children.Add(button);
        }
        body.Children.Add(colors);
        plane.Children.Add(hueSurface);
        plane.Children.Add(new Border { Background = new LinearGradientBrush(Colors.White, Colors.Transparent, 0) });
        plane.Children.Add(new Border { Background = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) });
        overlay.Children.Add(marker); plane.Children.Add(overlay); body.Children.Add(plane);
        AutomationProperties.SetName(plane, "颜色面板：左右调整饱和度，上下调整明暗");
        plane.ToolTip = "点击或拖动选色；方向键调整饱和度与明暗";
        plane.MouseLeftButtonDown += (_, e) => { plane.Focus(); plane.CaptureMouse(); PickPoint(e.GetPosition(plane)); e.Handled = true; };
        plane.MouseMove += (_, e) => { if (plane.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) PickPoint(e.GetPosition(plane)); };
        plane.MouseLeftButtonUp += (_, _) => plane.ReleaseMouseCapture();
        plane.SizeChanged += (_, _) => MoveMarker();
        plane.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left) saturation -= .02; else if (e.Key == Key.Right) saturation += .02;
            else if (e.Key == Key.Up) brightness += .02; else if (e.Key == Key.Down) brightness -= .02; else return;
            saturation = Math.Clamp(saturation, 0, 1); brightness = Math.Clamp(brightness, 0, 1); FromPlane(); e.Handled = true;
        };
        body.Children.Add(new TextBlock { Text = "色相", Margin = new(0, 8, 0, 0) });
        AutomationProperties.SetName(hue, "色相"); body.Children.Add(hue);
        hue.ValueChanged += (_, _) => { if (!updating) FromPlane(); };
        var footer = new DockPanel();
        var reset = new Button { Content = "恢复默认", Margin = new(0), Padding = new(8, 4, 8, 4), MinWidth = 0 };
        reset.Click += (_, _) => SetColor(null); DockPanel.SetDock(reset, Dock.Left); footer.Children.Add(reset);
        var done = new Button { Content = "完成选色", HorizontalAlignment = HorizontalAlignment.Right, Padding = new(8, 4, 8, 4), MinWidth = 0 };
        done.Click += (_, _) => IsExpanded = false; footer.Children.Add(done); body.Children.Add(footer);
        Expanded += (_, _) => SynchronizePlane(); Loaded += (_, _) => RefreshColor();
    }
    internal void SetColor(string? color, bool notify = true)
    {
        if (color is not null && !AppearanceSettings.IsColor(color)) throw new InvalidDataException("颜色无效。");
        color = color?.ToUpperInvariant(); var changed = selectedColor != color; selectedColor = color;
        RefreshColor(); SynchronizePlane(); if (notify && changed) ColorChanged?.Invoke(this, EventArgs.Empty);
    }
    internal void RefreshColor()
    {
        var actual = TryFindResource(ResourceKey) is SolidColorBrush brush ? brush.Color : Colors.Teal;
        var chosen = selectedColor is null ? actual : (Color)ColorConverter.ConvertFromString(selectedColor);
        swatch.Background = ThemeService.Brush(chosen);
        description.Text = selectedColor is null ? "跟随预设 · 点击选色" : selectedColor + " · 点击选色";
        description.SetResourceReference(TextBlock.ForegroundProperty, IsEnabled ? "TextBrush" : "DisabledTextBrush");
        description.TextDecorations = IsEnabled ? null : TextDecorations.Strikethrough;
        swatch.Opacity = IsEnabled ? 1 : .35;
        if (!IsEnabled) description.Text = "使用自定义图片 · 点击选色已禁用";
        AutomationProperties.SetName(this, ColorLabel + "，" + description.Text);
    }
    private void SynchronizePlane()
    {
        var color = selectedColor is not null ? (Color)ColorConverter.ConvertFromString(selectedColor)
            : swatch.Background is SolidColorBrush b ? b.Color : Colors.Teal;
        double r = color.R / 255d, g = color.G / 255d, b0 = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b0)); var min = Math.Min(r, Math.Min(g, b0)); var delta = max - min;
        var h = delta == 0 ? hue.Value : 60 * (max == r ? ((g - b0) / delta + 6) % 6 : max == g ? (b0 - r) / delta + 2 : (r - g) / delta + 4);
        updating = true; hue.Value = h; saturation = max == 0 ? 0 : delta / max; brightness = max; updating = false;
        hueSurface.Background = ThemeService.Brush(Hsv(hue.Value, 1, 1)); MoveMarker();
    }
    private void PickPoint(Point point)
    {
        saturation = Math.Clamp(point.X / Math.Max(1, plane.ActualWidth), 0, 1);
        brightness = 1 - Math.Clamp(point.Y / Math.Max(1, plane.ActualHeight), 0, 1); FromPlane();
    }
    private void FromPlane()
    {
        var color = Hsv(hue.Value, saturation, brightness);
        var next = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; var changed = next != selectedColor; selectedColor = next;
        hueSurface.Background = ThemeService.Brush(Hsv(hue.Value, 1, 1)); RefreshColor(); MoveMarker();
        if (changed) ColorChanged?.Invoke(this, EventArgs.Empty);
    }
    private void MoveMarker() { Canvas.SetLeft(marker, saturation * plane.ActualWidth - 5); Canvas.SetTop(marker, (1 - brightness) * plane.ActualHeight - 5); }
    private static Color Hsv(double h, double s, double v)
    {
        var c = v * s; var x = c * (1 - Math.Abs(h / 60 % 2 - 1)); var m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x)
            : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
