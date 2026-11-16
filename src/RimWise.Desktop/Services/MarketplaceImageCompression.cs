using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop.Services;

/// <summary>Upload-only optimization; drafts and source files retain their original pixels.</summary>
internal static class MarketplaceImageCompression
{
    internal const int TargetTotalBytes = 7_000_000;

    internal static byte[][] Compress(IReadOnlyList<byte[]> images, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (images.Count > MarketplaceImages.MaximumCount) throw new InvalidDataException("每份排序最多三张图片。");
        var result = new byte[images.Count][];
        for (var i = 0; i < images.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            result[i] = MarketplaceImages.Optimize(images[i]);
        }
        if (result.Sum(bytes => (long)bytes.Length) <= TargetTotalBytes) return result;

        // Always compare and encode from the source, never from a previous lossy attempt.
        foreach (var i in Enumerable.Range(0, images.Count).OrderByDescending(i => result[i].Length))
        {
            token.ThrowIfCancellationRequested();
            using var input = new MemoryStream(images[i], writable: false);
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            // Retain high-bit-depth and profiled sources rather than changing their color interpretation.
            if (frame.Format.BitsPerPixel > 32 || frame.Format == PixelFormats.Gray16
                || frame.Format == PixelFormats.Cmyk32 || frame.ColorContexts is { Count: > 0 }) continue;
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);
            bool opaque = true;
            for (int p = 3; p < pixels.Length; p += 4) if (pixels[p] != 255) { opaque = false; break; }

            // PNG keeps alpha exact; RGB rounding changes each channel by at most two levels.
            foreach (int step in new[] { 1, 2, 4 })
            {
                token.ThrowIfCancellationRequested();
                var rounded = pixels.ToArray();
                for (int p = 0; p < rounded.Length; p += 4)
                {
                    if ((p & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                    for (int c = 0; c < 3; c++)
                        rounded[p + c] = (byte)Math.Min(255, (pixels[p + c] + step / 2) / step * step);
                }
                TryKeep(EncodePng(rounded, bitmap.PixelWidth, bitmap.PixelHeight, opaque, images[i], token));
                if (WithinTarget()) break;
            }
            // Keep PNG gamma/primaries intact instead of changing their appearance in an unprofiled JPEG.
            if (!WithinTarget() && opaque && images[i][0] == 0xff)
            {
                foreach (int quality in new[] { 98, 96, 94, 92 })
                {
                    token.ThrowIfCancellationRequested();
                    var candidate = EncodeJpeg(bitmap, quality);
                    if (candidate.Length < result[i].Length && Faithful(pixels, bitmap.PixelWidth,
                        bitmap.PixelHeight, candidate, token)) TryKeep(candidate);
                    if (WithinTarget()) break;
                }
            }
            if (WithinTarget()) break;

            bool WithinTarget() => result.Sum(bytes => (long)bytes.Length) <= TargetTotalBytes;
            void TryKeep(byte[] candidate)
            {
                token.ThrowIfCancellationRequested();
                if (candidate.Length >= result[i].Length || candidate.Length > MarketplaceImages.MaximumBytes) return;
                _ = MarketplaceImages.Inspect(candidate);
                result[i] = candidate;
            }
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] EncodePng(byte[] pixels, int width, int height, bool opaque, byte[] original, CancellationToken token)
    {
        // A neutral filter compresses quantized screenshots/noise better than the WIC predictor.
        // Opaque sources use RGB; transparent sources keep their original alpha channel.
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[1 + width * (opaque ? 3 : 4)];
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0, p = 1; x < width; x++)
                {
                    int source = (y * width + x) * 4;
                    row[p++] = pixels[source + 2]; row[p++] = pixels[source + 1]; row[p++] = pixels[source];
                    if (!opaque) row[p++] = pixels[source + 3];
                }
                zlib.Write(row);
            }
        }
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = opaque ? (byte)2 : (byte)6;
        WriteChunk("IHDR", header);
        // Preserve PNG gamma/primaries and pixel density, which are not always reported as an ICC profile.
        if (original.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            for (int position = 8; position < original.Length;)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(original.AsSpan(position));
                string name = Encoding.ASCII.GetString(original, position + 4, 4);
                if (name is "gAMA" or "cHRM" or "sRGB" or "pHYs")
                    WriteChunk(name, original.AsSpan(position + 8, length).ToArray());
                position += length + 12;
            }
        }
        WriteChunk("IDAT", compressed.ToArray()); WriteChunk("IEND", []);
        return output.ToArray();

        void WriteChunk(string name, byte[] data)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
            var tag = Encoding.ASCII.GetBytes(name); output.Write(tag); output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte b in tag) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
            foreach (byte b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        uint crc = (uint)value;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        return crc;
    }).ToArray();

    private static bool Faithful(byte[] original, int width, int height, byte[] candidate, CancellationToken token)
    {
        using var stream = new MemoryStream(candidate, writable: false);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        if (decoded.PixelWidth != width || decoded.PixelHeight != height) return false;
        var bitmap = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[original.Length]; bitmap.CopyPixels(pixels, width * 4, 0);
        long squaredError = 0, outliers = 0, channels = (long)width * height * 3;
        for (int p = 0; p < pixels.Length; p += 4)
        {
            if ((p & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
            for (int c = 0; c < 3; c++)
            {
                int error = pixels[p + c] - original[p + c];
                squaredError += error * error;
                if (Math.Abs(error) > 24) outliers++;
            }
            if (squaredError > channels * 9 || outliers * 1000 > channels) return false;
        }
        // RMSE <= 3/255, and at least 99.9% of channels differ by <= 24/255.
        return squaredError <= channels * 9 && outliers * 1000 <= channels;
    }
}
