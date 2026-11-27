using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Desktop.Services;

/// <summary>One bounded, atomically replaced 64px image per installation in the user profile.</summary>
internal static class ModThumbnailDiskCache
{
    private const int MaximumBytes = 64 * 1024;
    internal sealed record Snapshot(int Schema, string Directory, string Stamp, byte[]? Png);

    internal static string PathFor(string storage, ModInstallation mod) => Path.Combine(storage, "Data", "Thumbnails",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(mod.Directory).ToUpperInvariant()))) + ".json");

    internal static (Snapshot Data, BitmapSource? Image)? Read(string path, ModInstallation mod)
    {
        try
        {
            ScopedPaths.Ordinary(path, true);
            using var input = File.OpenRead(path);
            if (input.Length > MaximumBytes) return null;
            var data = JsonSerializer.Deserialize<Snapshot>(input, new JsonSerializerOptions { MaxDepth = 4 });
            if (data is not { Schema: 1, Stamp: not null } || !string.Equals(data.Directory, mod.Directory, StringComparison.OrdinalIgnoreCase)) return null;
            if (data.Png is null) return (data, null);
            var png = data.Png;
            if (png.Length is < 24 or > 32 * 1024 || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
                || !png.AsSpan(12,4).SequenceEqual("IHDR"u8)
                || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) is 0 or > 64
                || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)) is 0 or > 64) return null;
            using var bytes = new MemoryStream(png, false);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            // The cached PNG is already bounded on both axes. Setting a fixed decode
            // width here would upscale a 1x64 cover into a 64x4096 allocation.
            image.StreamSource = bytes; image.EndInit(); image.Freeze();
            return (data, image);
        }
        catch (Exception ex) when (Failure(ex)) { return null; }
    }

    // Timestamp and length checks read only directory metadata, not the original bitmap.
    internal static string Stamp(ModInstallation mod)
    {
        ScopedPaths.Ordinary(mod.Directory, true);
        foreach (var name in new[] { "Preview.png", "Preview.jpg", "Preview.jpeg" })
        {
            var path = ScopedPaths.Ordinary(Path.Combine(mod.Directory, "About", name));
            var info = new FileInfo(path);
            if (info.Exists) return name + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }
        return "none";
    }

    internal static void Save(string path, ModInstallation mod, string stamp, BitmapSource? image, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            byte[]? png = null;
            if (image is not null)
            {
                if (image.PixelWidth > 64 || image.PixelHeight > 64) return;
                using var encoded = new MemoryStream();
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(encoded);
                png = encoded.ToArray();
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, mod.Directory, stamp, png));
            if (bytes.Length > MaximumBytes) return;
            ScopedPaths.Ordinary(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            token.ThrowIfCancellationRequested();
            ScopedPaths.Ordinary(path);
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (Failure(ex)) { /* Cache failure never prevents showing an image. */ }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static bool Failure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException
        or ArgumentException or NotSupportedException or InvalidOperationException or FormatException
        or System.Runtime.InteropServices.COMException or JsonException;
}
