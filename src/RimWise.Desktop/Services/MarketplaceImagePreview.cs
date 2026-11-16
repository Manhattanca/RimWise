using System.IO;
using System.Windows.Media.Imaging;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop.Services;

internal static class MarketplaceImagePreview
{
    internal static BitmapSource Decode(byte[] bytes, int width = 800)
    {
        _ = MarketplaceImages.Inspect(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = width; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
}
