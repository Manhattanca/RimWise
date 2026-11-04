using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using RimWise.Core;
using RimWise.Desktop.Services;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private readonly ModPreviewService previews = new();
    private string? previewDirectory;
    private long previewRevision;
    private CancellationTokenSource? previewCancellation;
    public ImageSource? SelectedPreview { get; private set; }
    public bool HasSelectedPreview => SelectedPreview is not null;
    public Uri? SelectedWorkshopUri => ModPresentationFiles.WorkshopUri(SelectedInstallation);
    public string SelectedWorkshopLink => SelectedWorkshopUri?.AbsoluteUri ?? "";
    public bool HasSelectedWorkshopLink => SelectedWorkshopUri is not null;
    internal Task PreviewLoading { get; private set; } = Task.CompletedTask;

    private void RefreshSelectedPresentation()
    {
        foreach (var property in new[] { nameof(SelectedWorkshopUri), nameof(SelectedWorkshopLink), nameof(HasSelectedWorkshopLink) }) OnPropertyChanged(property);
        var mod = SelectedInstallation;
        if (previewDirectory == mod?.Directory) return;
        previewDirectory = mod?.Directory;
        CancelPreviewLoads();
        SelectedPreview = null;
        NotifyPreview();
        if (mod is null) return;
        var cancellation = new CancellationTokenSource();
        previewCancellation = cancellation;
        PreviewLoading = LoadPreviewAsync(mod, previewRevision, cancellation);
    }

    private async Task LoadPreviewAsync(ModInstallation mod, long revision, CancellationTokenSource cancellation)
    {
        try
        {
            var image = await previews.LoadAsync(mod, cancellation.Token);
            if (revision != previewRevision || cancellation.IsCancellationRequested) return;
            SelectedPreview = image;
            NotifyPreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or FormatException or COMException)
        {
            // Optional, malformed previews cannot prevent selecting a mod or loading its metadata.
        }
        finally
        {
            if (ReferenceEquals(previewCancellation, cancellation)) previewCancellation = null;
            cancellation.Dispose();
        }
    }

    internal void CancelPreviewLoads()
    {
        previewRevision++;
        previewCancellation?.Cancel();
    }

    private void NotifyPreview()
    {
        OnPropertyChanged(nameof(SelectedPreview));
        OnPropertyChanged(nameof(HasSelectedPreview));
    }
}
