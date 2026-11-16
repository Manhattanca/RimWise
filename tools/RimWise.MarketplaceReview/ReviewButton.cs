using System.Drawing;
using System.Drawing.Drawing2D;

namespace RimWise.MarketplaceReview;

internal sealed class ReviewButton : Button
{
    internal ReviewButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var fill = new SolidBrush(BackColor); e.Graphics.FillRectangle(fill, ClientRectangle);
        using var border = new Pen(Enabled ? Color.FromArgb(80,128,130) : Color.FromArgb(49,69,73));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.DrawRectangle(border, 0, 0, Width-1, Height-1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.FromArgb(111,138,143), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
