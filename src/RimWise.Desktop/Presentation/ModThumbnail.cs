using System.Windows;
using System.Windows.Controls;
using RimWise.Core;
using RimWise.Desktop.Services;

namespace RimWise.Desktop.Presentation;

/// <summary>A recycled cell cancels its old image before requesting the next visible installation.</summary>
public sealed class ModThumbnail : Border
{
    public static readonly DependencyProperty InstallationProperty = DependencyProperty.Register(nameof(Installation),
        typeof(ModInstallation), typeof(ModThumbnail), new PropertyMetadata(null, Changed));
    public ModInstallation? Installation
    {
        get => (ModInstallation?)GetValue(InstallationProperty);
        set => SetValue(InstallationProperty, value);
    }
    private readonly Image image = new() { Stretch = System.Windows.Media.Stretch.Uniform, IsHitTestVisible = false };
    private CancellationTokenSource? loading;
    internal Task ImageLoading { get; private set; } = Task.CompletedTask;

    public ModThumbnail()
    {
        Width = Height = 30;
        Margin = new Thickness(0, 0, 6, 0);
        Background = System.Windows.Media.Brushes.Transparent;
        Child = image;
        // Own the hover target so a row/grid tooltip cannot leak onto the cover.
        ToolTip = "";
        ToolTipService.SetIsEnabled(this, false);
        ToolTipOpening += (_, e) => e.Handled = true;
        Loaded += (_, _) => BeginLoad();
        Unloaded += (_, _) => { loading?.Cancel(); image.Source = null; };
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ModThumbnail)sender).BeginLoad();
    private void BeginLoad()
    {
        loading?.Cancel();
        image.Source = null;
        if (!IsLoaded || Installation is not { } mod) return;
        var cancellation = new CancellationTokenSource();
        loading = cancellation;
        ImageLoading = LoadAsync(mod, cancellation);
    }
    private async Task LoadAsync(ModInstallation mod, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await ModThumbnailCache.Shared.LoadAsync(mod, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(loading, cancellation) && IsLoaded) image.Source = result;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(loading, cancellation)) loading = null;
            cancellation.Dispose();
        }
    }
}
