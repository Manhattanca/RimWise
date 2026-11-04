using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using RimWise.Core;

namespace RimWise.Desktop.Services;

/// <summary>Only visible cells request images. Bound decoding concurrency and decoded memory.</summary>
internal sealed class ModThumbnailCache
{
    internal static ModThumbnailCache Shared { get; } = new();
    private readonly SemaphoreSlim decoding = new(2);
    private readonly ModPreviewService previews = new();
    private readonly Dictionary<string, (BitmapSource? Image, DateTimeOffset Time)> entries = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<BitmapSource?> LoadAsync(ModInstallation mod, CancellationToken token)
    {
        var key = mod.Directory + "\0" + mod.LastUpdatedUtc?.UtcTicks;
        token.ThrowIfCancellationRequested();
        // Returning a frozen image never needs to wait for unrelated disk reads.
        lock (entries)
            if (entries.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(2)) return cached.Image;
        await decoding.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (entries)
                if (entries.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.Time < TimeSpan.FromMinutes(2)) return hit.Image;
            BitmapSource? image;
            try { image = await previews.LoadAsync(mod, token, 64, 64).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or InvalidOperationException or FormatException or COMException)
            { image = null; }
            token.ThrowIfCancellationRequested();
            lock (entries)
            {
                if (entries.Count >= 256) entries.Remove(entries.MinBy(entry => entry.Value.Time).Key);
                entries[key] = (image, DateTimeOffset.UtcNow);
            }
            return image;
        }
        finally { decoding.Release(); }
    }
}
