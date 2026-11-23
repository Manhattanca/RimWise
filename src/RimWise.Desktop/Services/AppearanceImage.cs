using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimWise.Desktop.Services;

internal sealed record AppearanceImage(BitmapSource Bitmap, byte[] Png, string Name, string Accent)
{
    internal const int MaximumBytes = 20_000_000;
    internal BitmapSource RenderBackground(double radius, CancellationToken cancellationToken = default)
    {
        if (radius <= 0) return Bitmap;
        // Bounded separable box passes approximate a Gaussian, entirely off the UI
        // dispatcher. Premultiplied alpha avoids dark fringes on transparent PNGs.
        var scale = Math.Min(1, 1280d / Math.Max(Bitmap.PixelWidth, Bitmap.PixelHeight));
        BitmapSource source = scale < 1 ? new TransformedBitmap(Bitmap, new ScaleTransform(scale, scale)) : Bitmap;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var width = converted.PixelWidth; var height = converted.PixelHeight; var stride = width * 4;
        var pixels = new byte[stride * height]; var scratch = new byte[pixels.Length]; converted.CopyPixels(pixels, stride, 0);
        var boxRadius = Math.Max(1, (int)Math.Round(radius * scale * .58));
        for (var pass = 0; pass < 3; pass++)
        {
            Box(pixels, scratch, width, height, boxRadius, horizontal: true, cancellationToken);
            Box(scratch, pixels, width, height, boxRadius, horizontal: false, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride); result.Freeze();
        return result;
    }
    private static void Box(byte[] input, byte[] output, int width, int height, int radius, bool horizontal, CancellationToken token)
    {
        var lines = horizontal ? height : width; var length = horizontal ? width : height;
        var step = horizontal ? 4 : width * 4; var diameter = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        {
            token.ThrowIfCancellationRequested();
            var origin = horizontal ? line * width * 4 : line * 4;
            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;
                for (var j = -radius; j <= radius; j++) sum += input[origin + Math.Clamp(j, 0, length - 1) * step + channel];
                for (var pixel = 0; pixel < length; pixel++)
                {
                    output[origin + pixel * step + channel] = (byte)((sum + diameter / 2) / diameter);
                    sum += input[origin + Math.Min(length - 1, pixel + radius + 1) * step + channel]
                        - input[origin + Math.Max(0, pixel - radius) * step + channel];
                }
            }
        }
    }
    internal static AppearanceImage Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("背景图片请使用不超过 20 MB 的 PNG 或 JPG 静态图片。");
        using var bytes = new MemoryStream();
        input.CopyTo(bytes); // FileShare.Read prevents a concurrent writer while copying the bounded file.
        return Decode(bytes.ToArray());
    }

    internal static AppearanceImage Decode(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("背景图片过大或为空。");
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder) || decoder.Frames.Count != 1)
            throw new InvalidDataException("背景仅支持 PNG 或 JPG 静态图片，不支持动图、网页或主题脚本。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 16000 || frame.PixelHeight > 16000
            || (long)frame.PixelWidth * frame.PixelHeight > 40_000_000)
            throw new InvalidDataException("背景图片分辨率过大，请使用不超过 4000 万像素的图片。");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream;
        if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = Math.Min(2560, frame.PixelWidth);
        else bitmap.DecodePixelHeight = Math.Min(2560, frame.PixelHeight);
        bitmap.EndInit(); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        var png = output.ToArray();
        if (png.Length > MaximumBytes) throw new InvalidDataException("背景图片处理后过大，请降低图片分辨率后重试。");
        // A small, deterministic sample is only a suggestion, never an automatically applied color.
        var sample = new TransformedBitmap(bitmap, new ScaleTransform(24d / bitmap.PixelWidth, 24d / bitmap.PixelHeight));
        var rgb = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[rgb.PixelWidth * rgb.PixelHeight * 4]; rgb.CopyPixels(pixels, rgb.PixelWidth * 4, 0);
        long r = 0, g = 0, b = 0, count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 128) continue;
            b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; count++;
        }
        var accent = count == 0 ? "#337F73" : $"#{r / count:X2}{g / count:X2}{b / count:X2}";
        return new(bitmap, png, Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() + ".png", accent);
    }

    internal static string CacheDirectory(string profile) => Path.Combine(profile, "Data", "Appearance");
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("背景缓存不支持符号链接或目录联接。");
    }
    internal void Save(string profile)
    {
        var directory = CacheDirectory(profile); RejectLinks(directory); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Name); RejectLinks(path);
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            if (existing.Length > MaximumBytes || Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant() + ".png" != Name)
                throw new InvalidDataException("背景缓存校验失败，已有文件未被覆盖。");
            return;
        }
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temporary, Png); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static AppearanceImage Load(string profile, string name)
    {
        var path = Path.Combine(CacheDirectory(profile), name); RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("背景缓存大小无效。");
        using var output = new MemoryStream(); stream.CopyTo(output);
        var bytes = output.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".png" != name)
            throw new InvalidDataException("背景缓存校验失败。");
        return Decode(bytes) with { Name = name, Png = bytes };
    }
}
