using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RimWise.Desktop.Services;

/// <summary>A single traveling indicator; selection is immediate, geometry never animates layout.</summary>
public sealed class NavigationTabStrip : Grid
{
    private readonly Border indicator = new() { Width = 1, Height = 2, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Hidden };
    private readonly TranslateTransform offset = new();
    private readonly ScaleTransform span = new();
    private RadioButton[] tabs = [];
    private StackPanel[] panels = [];
    private RadioButton? selected;
    private DispatcherOperation? pending;
    private double targetX = double.NaN, targetWidth;

    public NavigationTabStrip()
    {
        indicator.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        indicator.RenderTransform = new TransformGroup { Children = new TransformCollection { span, offset } };
        AddHandler(RadioButton.CheckedEvent, new RoutedEventHandler((_, _) => QueueUpdate()), handledEventsToo: true);
        SizeChanged += GeometryChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueUpdate(); else Stop(); };
        Loaded += (_, _) =>
        {
            if (!Children.Contains(indicator)) Children.Add(indicator);
            panels = Children.OfType<StackPanel>().ToArray();
            tabs = panels.SelectMany(panel => panel.Children.OfType<RadioButton>()).ToArray();
            foreach (var panel in panels) panel.SizeChanged += GeometryChanged;
            foreach (var tab in tabs) tab.SizeChanged += GeometryChanged;
            UpdateIndicator();
        };
        Unloaded += (_, _) =>
        {
            pending?.Abort(); pending = null;
            foreach (var panel in panels) panel.SizeChanged -= GeometryChanged;
            foreach (var tab in tabs) tab.SizeChanged -= GeometryChanged;
            panels = []; tabs = []; selected = null; targetX = double.NaN;
            Stop();
        };
    }

    private void GeometryChanged(object sender, SizeChangedEventArgs e) => QueueUpdate();

    private void QueueUpdate()
    {
        if (!IsLoaded || !IsVisible || pending is not null) return;
        pending = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            pending = null;
            if (IsLoaded && IsVisible) UpdateIndicator();
        }));
    }

    private void UpdateIndicator()
    {
        var tab = tabs.FirstOrDefault(button => button.IsChecked == true && button.IsVisible);
        if (tab is null || !tab.IsArrangeValid || tab.ActualWidth <= 0) return;
        var x = tab.TranslatePoint(new Point(), this).X;
        var width = tab.ActualWidth;
        if (ReferenceEquals(selected, tab) && Math.Abs(x - targetX) < .1 && Math.Abs(width - targetWidth) < .1) return;
        var animate = selected is not null && selected != tab && InterfaceMotion.Enabled;
        var oldX = offset.X;
        var oldWidth = span.ScaleX;
        selected = tab; targetX = x; targetWidth = width;
        Stop();
        offset.X = x; span.ScaleX = width; indicator.Visibility = Visibility.Visible;
        if (!animate) return;
        offset.BeginAnimation(TranslateTransform.XProperty, InterfaceMotion.Motion(oldX, x, 160), HandoffBehavior.SnapshotAndReplace);
        span.BeginAnimation(ScaleTransform.ScaleXProperty, InterfaceMotion.Motion(oldWidth, width, 160), HandoffBehavior.SnapshotAndReplace);
    }

    private void Stop()
    {
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        span.BeginAnimation(ScaleTransform.ScaleXProperty, null);
    }

    internal double IndicatorX => offset.X;
    internal double IndicatorWidth => span.ScaleX;
    internal bool IndicatorAnimatesLayout => indicator.HasAnimatedProperties;
}
