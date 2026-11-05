using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RimWise.Desktop.Services;

/// <summary>A single traveling indicator shared by the tabs; selection stays immediate.</summary>
public sealed class NavigationTabStrip : Grid
{
    private readonly Border indicator = new() { Height = 2, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Hidden };
    private readonly TranslateTransform offset = new();
    private RadioButton? selected;
    private double targetX = double.NaN, targetWidth;

    public NavigationTabStrip()
    {
        indicator.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        indicator.RenderTransform = offset;
        Loaded += (_, _) =>
        {
            if (!Children.Contains(indicator)) Children.Add(indicator);
            UpdateIndicator();
        };
        LayoutUpdated += (_, _) => { if (IsVisible) UpdateIndicator(); };
        Unloaded += (_, _) => { selected = null; targetX = double.NaN; };
    }

    private void UpdateIndicator()
    {
        var tab = Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<RadioButton>())
            .FirstOrDefault(button => button.IsChecked == true);
        if (tab is null || !tab.IsArrangeValid || tab.ActualWidth <= 0) return;
        var x = tab.TranslatePoint(new Point(), this).X;
        var width = tab.ActualWidth;
        if (ReferenceEquals(selected, tab) && Math.Abs(x - targetX) < .1 && Math.Abs(width - targetWidth) < .1) return;
        var animate = selected is not null && selected != tab && InterfaceMotion.Enabled;
        var oldX = offset.X;
        var oldWidth = indicator.ActualWidth;
        selected = tab; targetX = x; targetWidth = width;
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        indicator.BeginAnimation(WidthProperty, null);
        offset.X = x; indicator.Width = width; indicator.Visibility = Visibility.Visible;
        if (!animate) return;
        offset.BeginAnimation(TranslateTransform.XProperty, Motion(oldX, x));
        indicator.BeginAnimation(WidthProperty, Motion(oldWidth, width));
    }

    private static DoubleAnimation Motion(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(160))
    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };

    internal double IndicatorX => offset.X;
    internal double IndicatorWidth => indicator.ActualWidth;
}
