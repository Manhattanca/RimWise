using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RimWise.Desktop.Views;

internal sealed partial class SortingGuideView
{
    private Size positionedSize;
    private Rect positionedTarget;
    private int positionedStep = -1;

    private void PositionCoach()
    {
        if (positioning || !IsVisible || stage.ActualWidth < 1 || stage.ActualHeight < 1) return;
        positioning = true;
        try
        {
            var size = stage.RenderSize;
            var choosing = TargetKey == "decision" && decision != null;
            var noteWidth = Math.Clamp(size.Width * .28, 230, 336);
            if (choosing)
            {
                decision!.Width = Math.Clamp(size.Width - noteWidth - 48, 1, 880); decision.Height = Math.Clamp(size.Height - 24, 1, 560);
                Canvas.SetLeft(decision, (size.Width - decision.Width - 16 - noteWidth) / 2); Canvas.SetTop(decision, (size.Height - decision.Height) / 2);
            }
            var hole = anchor is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } target
                ? target.TransformToAncestor(stage).TransformBounds(new Rect(target.RenderSize)) : Rect.Empty;
            if (modeMenu != null && !hole.IsEmpty)
            {
                modeMenu.Width = hole.Width; modeMenu.Measure(new Size(hole.Width, double.PositiveInfinity));
                Canvas.SetLeft(modeMenu, hole.Left); Canvas.SetTop(modeMenu, hole.Bottom);
                hole.Height += modeMenu.DesiredSize.Height;
            }
            if (positionedStep == StepIndex && positionedSize == size && positionedTarget == hole) return;
            positionedStep = StepIndex; positionedSize = size; positionedTarget = hole;
            TargetBounds = hole;
            if (!hole.IsEmpty && ActiveTarget is { } proxy)
            { Canvas.SetLeft(proxy, hole.Left); Canvas.SetTop(proxy, hole.Top); proxy.Width = hole.Width; proxy.Height = hole.Height; }
            Rect picker = Rect.Empty, coachArea = Rect.Empty;
            if (!hole.IsEmpty && filePicker is { IsVisible: true } && TargetKey is ("pick-file" or "open-file"))
            {
                var surface = Part<Canvas>(filePicker, "PickerSurface");
                var area = Part<Border>(filePicker, "CoachArea");
                picker = surface.TransformToAncestor(stage).TransformBounds(new Rect(surface.RenderSize));
                coachArea = area.TransformToAncestor(stage).TransformBounds(new Rect(area.RenderSize));
            }
            var width = choosing ? noteWidth : Math.Min(360, coachArea.IsEmpty ? size.Width - 32 : coachArea.Width);
            card.Width = width; card.MaxHeight = coachArea.IsEmpty ? Math.Max(100, size.Height - 32) : coachArea.Height;
            bool beside = !hole.IsEmpty && (size.Width - hole.Right >= width + 24 || hole.Left >= width + 24);
            var above = hole.IsEmpty ? 0 : hole.Top - 36;
            var below = hole.IsEmpty ? 0 : size.Height - hole.Bottom - 40;
            if (coachArea.IsEmpty && !hole.IsEmpty && !beside) card.MaxHeight = Math.Max(1, Math.Max(above, below));
            card.Measure(new Size(width, card.MaxHeight));
            var height = Math.Min(card.DesiredSize.Height, card.MaxHeight);
            var x = (size.Width - width) / 2; var y = (size.Height - height) / 2;
            if (choosing) x = Canvas.GetLeft(decision!) + decision!.Width + 16;
            else if (!coachArea.IsEmpty)
            {
                x = Math.Clamp(hole.Left + (hole.Width - width) / 2, coachArea.Left, Math.Max(coachArea.Left, coachArea.Right - width));
                y = TargetKey == "pick-file" ? coachArea.Top : coachArea.Bottom - height;
            }
            else if (!hole.IsEmpty)
            {
                // Try a non-overlapping side first; reserve above/below for wide targets.
                if (size.Width - hole.Right >= width + 24) x = size.Width - width - 16;
                else if (hole.Left >= width + 24) x = 16;
                else if (above >= height - 1) y = Math.Max(16, hole.Top - height - 20);
                else y = Math.Min(size.Height - height - 16, hole.Bottom + 24);
                y = Math.Clamp(y, 16, Math.Max(16, size.Height - height - 16));
            }
            Canvas.SetLeft(card, x); Canvas.SetTop(card, y); CardBounds = new Rect(x, y, width, height);
            ring.Visibility = pointer.Visibility = hole.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            if (ModeLesson) ring.Visibility = Visibility.Collapsed; // Only the explained row is outlined.
            if (ReadAnywhere) pointer.Visibility = Visibility.Collapsed;
            if (!hole.IsEmpty)
            {
                var outline = hole; outline.Inflate(5, 5);
                Canvas.SetLeft(ring, outline.Left); Canvas.SetTop(ring, outline.Top); ring.Width = outline.Width; ring.Height = outline.Height;
                var besideOpen = !picker.IsEmpty && TargetKey == "open-file";
                pointer.Text = besideOpen ? "点这里 ▶" : hole.Top >= 30 ? "▼ 点这里" : "▲ 点这里";
                pointer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var pointerX = besideOpen ? hole.Left - pointer.DesiredSize.Width - 12 : hole.Left + (hole.Width - pointer.DesiredSize.Width) / 2;
                Canvas.SetLeft(pointer, Math.Clamp(pointerX, 8, Math.Max(8, size.Width - pointer.DesiredSize.Width - 8)));
                Canvas.SetTop(pointer, besideOpen ? hole.Top + (hole.Height - pointer.DesiredSize.Height) / 2 : hole.Top >= 30 ? hole.Top - 28 : hole.Bottom + 8);
            }
            var cutout = hole; if (!cutout.IsEmpty) cutout.Inflate(5, 5);
            shade.SetHole(picker.IsEmpty ? cutout : picker);
        }
        finally { positioning = false; }
    }

    /// <summary>One lightweight draw, no blur, image capture, polling or per-frame animation.</summary>
    private sealed class SpotlightShade : FrameworkElement
    {
        private Rect hole = Rect.Empty;
        private static readonly Brush Dim = CreateShade();
        internal SpotlightShade() { IsHitTestVisible = false; }
        internal void SetHole(Rect value) { if (value == hole) return; hole = value; InvalidateVisual(); }
        protected override void OnRender(DrawingContext context)
        {
            var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            geometry.Children.Add(new RectangleGeometry(new Rect(RenderSize)));
            if (!hole.IsEmpty) geometry.Children.Add(new RectangleGeometry(hole, 7, 7));
            geometry.Freeze(); context.DrawGeometry(Dim, null, geometry);
        }
        private static Brush CreateShade() { var brush = new SolidColorBrush(Color.FromArgb(174, 0, 0, 0)); brush.Freeze(); return brush; }
    }
}
