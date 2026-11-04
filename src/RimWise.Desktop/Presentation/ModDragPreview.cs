using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using RimWise.Desktop.Services;

namespace RimWise.Desktop.Presentation;

/// <summary>At most three row previews and one summary, independent of recycled DataGrid rows.</summary>
internal sealed class ModDragPreview : IDisposable
{
    private readonly Canvas overlay;
    private readonly Border card;
    private readonly StackPanel group;
    private readonly RotateTransform rotation;
    private readonly Point grip;
    private Point previous;
    private long lastFrame;
    private bool disposed;
    private readonly bool animate;
    internal double Angle => rotation.Angle;
    internal Border Card => card;
    internal StackPanel Group => group;

    internal ModDragPreview(Canvas overlay, IReadOnlyList<object> rows, double width, Point pointer, Point grip,
        bool? animate = null, bool trackPointer = true, double height = 44)
    {
        if (rows.Count == 0) throw new ArgumentException("A drag preview needs a selected row.", nameof(rows));
        this.overlay = overlay;
        this.grip = grip;
        this.animate = animate ?? InterfaceMotion.Enabled;
        previous = pointer;
        rotation = new RotateTransform(0, grip.X, grip.Y);
        group = new StackPanel { Width = width, IsHitTestVisible = false, RenderTransform = rotation,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 4, Opacity = .22 } };
        for (var i = 0; i < Math.Min(3, rows.Count); i++)
            group.Children.Add(CreateCard(CreateIdentity(rows[i], i == 0 ? rows.Count : 1), width, height, i > 0));
        card = (Border)group.Children[0];
        if (rows.Count > 3)
            group.Children.Add(CreateCard(new TextBlock { Text = $"以及更多 {rows.Count - 3} 个 Mod",
                VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold }, width, height, true));
        Panel.SetZIndex(group, 10);
        overlay.Children.Add(group);
        Move(pointer, 1.0 / 60);
        if (this.animate) InterfaceMotion.Reveal(group);
        lastFrame = Stopwatch.GetTimestamp();
        if (trackPointer) CompositionTarget.Rendering += Render;
    }

    private DockPanel CreateIdentity(object row, int count)
    {
        var content = new DockPanel { LastChildFill = true };
        if (count > 1)
        {
            var badge = new Border { CornerRadius = new(4), Padding = new(7, 2, 7, 2), Margin = new(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = $"{count} 个", FontWeight = FontWeights.SemiBold } };
            badge.SetResourceReference(Border.BackgroundProperty, "SelectionBrush");
            DockPanel.SetDock(badge, Dock.Right);
            content.Children.Add(badge);
        }
        if (row is LoadOrderRow)
        {
            var status = new Grid { Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var (template, property) in new[] { ("WarningIcon", "HasWarning"), ("ErrorIcon", "HasError") })
            {
                var icon = new ContentControl { ContentTemplate = (DataTemplate)overlay.FindResource(template) };
                icon.SetBinding(UIElement.VisibilityProperty, new Binding(property) { Source = row,
                    Converter = (IValueConverter)overlay.FindResource("BooleanVisibility") });
                status.Children.Add(icon);
            }
            DockPanel.SetDock(status, Dock.Right);
            content.Children.Add(status);
        }
        // Reuse the list's cover, source/content badges, activity icons and diagnostic text colors.
        // Only the first three selected rows are rendered, regardless of selection size.
        content.Children.Add(new ContentControl { Content = row,
            ContentTemplate = (DataTemplate)overlay.FindResource("ModIdentity"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center });
        return content;
    }

    private Border CreateCard(UIElement content, double width, double height, bool separated)
    {
        var result = new Border { Width = width, Height = height, Padding = new(8, 0, 8, 0), CornerRadius = new(6),
            Margin = new(0, separated ? 3 : 0, 0, 0), BorderThickness = new(1), Child = content };
        result.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        result.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        TextElement.SetForeground(result, (Brush)overlay.FindResource("TextBrush"));
        return result;
    }

    private void Render(object? sender, EventArgs e)
    {
        if (disposed || PresentationSource.FromVisual(overlay) is null || !GetCursorPos(out var screen)) return;
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - lastFrame) / (double)Stopwatch.Frequency;
        lastFrame = now;
        Move(overlay.PointFromScreen(new Point(screen.X, screen.Y)), elapsed);
    }

    internal void Move(Point pointer, double seconds)
    {
        var dt = Math.Clamp(seconds, .001, .05);
        var target = animate ? Math.Clamp((pointer.X - previous.X) / dt * .003, -4, 4) : 0;
        rotation.Angle += (target - rotation.Angle) * (1 - Math.Exp(-18 * dt));
        // The grip follows immediately; only rotation eases, so the card never trails the cursor.
        Canvas.SetLeft(group, pointer.X - grip.X);
        Canvas.SetTop(group, pointer.Y - grip.Y);
        group.Visibility = pointer.X >= 0 && pointer.Y >= 0 && pointer.X <= overlay.ActualWidth
            && pointer.Y <= overlay.ActualHeight ? Visibility.Visible : Visibility.Hidden;
        previous = pointer;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CompositionTarget.Rendering -= Render;
        overlay.Children.Remove(group);
        group.BeginAnimation(UIElement.OpacityProperty, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);
}
