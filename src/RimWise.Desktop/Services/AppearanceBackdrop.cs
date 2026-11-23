using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Services;

/// <summary>A single cached decorative image; no row effects, animation loop, downloads or hit testing.</summary>
public sealed class AppearanceBackdrop : Grid
{
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly Border shade = new();
    private readonly SolidColorBrush shadeBrush = new();
    private readonly TranslateTransform translation = new();
    private (Color, Color, double)? shadeKey;
    private ThemeService? themes;
    private ThemeService? source;
    internal ThemeService? Source
    {
        get => source;
        set
        {
            if (ReferenceEquals(source, value)) return;
            source = value;
            if (IsLoaded) AttachSource();
        }
    }
    private AppearanceSettings settings = new();
    public AppearanceBackdrop()
    {
        IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
        var canvas = new Canvas { IsHitTestVisible = false };
        image.RenderTransform = translation; shade.Background = shadeBrush;
        canvas.Children.Add(image); Children.Add(canvas); Children.Add(shade);
        SetResourceReference(BackgroundProperty, "PageBrush");
        Loaded += (_, _) => AttachSource();
        Unloaded += (_, _) => { if (themes is not null) themes.Changed -= Changed; themes = null; };
        SizeChanged += (_, _) => ArrangeImage();
    }
    private void AttachSource()
    {
        if (themes is not null) themes.Changed -= Changed;
        themes = source ?? (Application.Current as App)?.Themes;
        if (themes is not null) { themes.Changed += Changed; Refresh(); }
    }
    private void Changed(object? sender, EventArgs args) => Refresh();
    private void Refresh()
    {
        if (themes is null) return;
        settings = themes.Current;
        var bitmap = SystemParameters.HighContrast ? null : themes.BackgroundBitmap;
        if (!ReferenceEquals(image.Source, bitmap)) image.Source = bitmap;
        var page = ((SolidColorBrush)FindResource("PageBrush")).Color;
        var text = ((SolidColorBrush)FindResource("TextBrush")).Color;
        // Labels outside cards also need protection against an arbitrarily bright/dark image.
        var key = (page, text, settings.Shade);
        if (shadeKey != key)
        {
            var cover = settings.Shade;
            while (cover < .98 && (ThemeService.Contrast(text, ThemeService.Mix(Colors.White, page, cover)) < 4.5
                || ThemeService.Contrast(text, ThemeService.Mix(Colors.Black, page, cover)) < 4.5)) cover += .02;
            shadeBrush.Color = page; shade.Opacity = Math.Min(1, cover); shadeKey = key;
        }
        ArrangeImage();
    }
    private void ArrangeImage()
    {
        if (image.Source is not { Width: > 0, Height: > 0 } source || ActualWidth <= 0 || ActualHeight <= 0)
        { image.Visibility = shade.Visibility = Visibility.Collapsed; return; }
        image.Visibility = shade.Visibility = Visibility.Visible;
        var ratio = settings.Placement == BackgroundPlacement.Fill
            ? Math.Max(ActualWidth / source.Width, ActualHeight / source.Height)
            : Math.Min(ActualWidth / source.Width, ActualHeight / source.Height);
        image.Width = source.Width * ratio; image.Height = source.Height * ratio;
        image.HorizontalAlignment = HorizontalAlignment.Left; image.VerticalAlignment = VerticalAlignment.Top;
        translation.X = (ActualWidth - image.Width) * settings.PositionX;
        translation.Y = (ActualHeight - image.Height) * settings.PositionY;
    }
}
