using System.IO;
using System.Windows.Media.Imaging;
using RimWise.Core;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop.Services;

internal sealed class ModPreviewService
{
    public Task<BitmapSource?> LoadAsync(ModInstallation mod, CancellationToken cancellation, int maximumWidth = 720, int maximumHeight = 400) => Task.Run(() =>
    {
        var bytes = ModPresentationFiles.ReadPreview(mod, cancellation);
        if (bytes is null) return null;
        using var input = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("预览图帧数无效。");
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 16_000_000)
            throw new InvalidDataException("预览图尺寸过大。");
        cancellation.ThrowIfCancellationRequested();
        input.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Max(1, (int)(width * Math.Min(1, Math.Min((double)maximumWidth / width, (double)maximumHeight / height))));
        image.StreamSource = input;
        image.EndInit();
        image.Freeze();
        cancellation.ThrowIfCancellationRequested();
        return (BitmapSource?)image;
    }, cancellation);
}
