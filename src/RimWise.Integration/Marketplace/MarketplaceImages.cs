using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RimWise.Integration.Marketplace;

/// <summary>Bounded PNG/JPEG inspection. PNG optimization rewrites only the lossless IDAT stream.</summary>
public static class MarketplaceImages
{
    public const int MaximumCount = 3;
    public const int MaximumBytes = 5 * 1024 * 1024;
    public const int MaximumDimension = 8192;
    public const long MaximumPixels = 24_000_000;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    public sealed record Information(string Extension, int Width, int Height);

    public static Information Inspect(ReadOnlySpan<byte> bytes)
    {
        Check(bytes.Length is > 0 and <= MaximumBytes, "图片必须小于或等于 5 MiB。");
        if (bytes.StartsWith(Signature)) return Png(bytes.ToArray(), optimize: false).Info;
        Check(bytes.Length > 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217,
            "仅支持完整的 PNG 或 JPEG 图片。");
        var position = 2;
        while (position < bytes.Length - 2)
        {
            Check(bytes[position++] == 255, "JPEG 图片结构无效。");
            while (position < bytes.Length && bytes[position] == 255) position++;
            Check(position < bytes.Length, "JPEG 图片不完整。");
            var marker = bytes[position++];
            Check(marker is not (0 or 216 or 217 or 218), "JPEG 图片缺少有效尺寸。");
            Check(position + 2 <= bytes.Length, "JPEG 图片不完整。");
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            Check(length >= 2 && position + length <= bytes.Length, "JPEG 图片段无效。");
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                Check(length >= 8 && bytes[position + 2] == 8, "不支持此 JPEG 编码。");
                return Dimensions("jpg", BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..]));
            }
            position += length;
        }
        throw new MarketplaceException("JPEG 图片缺少尺寸。");
    }

    public static byte[] Optimize(byte[] bytes)
    {
        _ = Inspect(bytes);
        if (!bytes.AsSpan().StartsWith(Signature)) return bytes.ToArray(); // JPEG is never re-encoded.
        return Png(bytes, optimize: true).Bytes;
    }

    public static MarketplaceImageUpload Prepare(string author, string id, string version, int index, byte[] bytes)
    {
        Check(index is >= 0 and < MaximumCount, "每份排序最多三张图片。");
        var owned = bytes.ToArray();
        var info = Inspect(owned);
        var hash = MarketplaceJson.ComputeSha256(owned);
        var prefix = Prefix(author, id, version);
        return new(new(prefix + (index + 1) + "-" + hash + "." + info.Extension, hash, owned.Length, info.Width, info.Height), owned);
    }

    public static string Prefix(string author, string id, string version)
    {
        _ = MarketplaceJson.GetManifestPath(author, id, version);
        return $"marketplace/images/{author.ToLowerInvariant()}/{id}/{version}/";
    }

    public static void Validate(string author, string id, string version, IReadOnlyList<MarketplaceImage> images)
    {
        Check(images is not null && images.Count <= MaximumCount, "每份排序最多三张图片。");
        var prefix = Prefix(author, id, version);
        for (var i = 0; i < images!.Count; i++)
        {
            var image = images[i];
            Check(image is not null, "图片信息缺失。");
            Check(image!.Sha256 is { Length: 64 } && image.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "图片校验码无效。");
            Check(image.Path == prefix + (i + 1) + "-" + image.Sha256 + ".png" ||
                image.Path == prefix + (i + 1) + "-" + image.Sha256 + ".jpg", "图片位置与排序版本不匹配。");
            Check(image.Bytes is > 0 and <= MaximumBytes, "图片大小无效。");
            _ = Dimensions("", image.Width, image.Height);
        }
    }

    public static void Verify(MarketplaceImage image, ReadOnlySpan<byte> bytes)
    {
        Check(bytes.Length == image.Bytes && MarketplaceJson.ComputeSha256(bytes) == image.Sha256, "图片完整性校验失败。");
        var info = Inspect(bytes);
        Check(image.Width == info.Width && image.Height == info.Height && image.Path.EndsWith("." + info.Extension, StringComparison.Ordinal), "图片格式或尺寸与清单不符。");
    }

    private static Information Dimensions(string extension, int width, int height)
    {
        Check(width is > 0 and <= MaximumDimension && height is > 0 and <= MaximumDimension && (long)width * height <= MaximumPixels,
            "图片边长最多 8192 像素，总像素最多 2400 万；请先裁剪或更换图片。");
        return new(extension, width, height);
    }

    private static (Information Info, byte[] Bytes) Png(byte[] bytes, bool optimize)
    {
        var chunks = new List<(string Name, byte[] Data)>();
        using var compressed = new MemoryStream();
        int position = 8;
        bool ended = false, seenData = false, dataEnded = false;
        Information? info = null;
        long expected = 0;
        while (position < bytes.Length)
        {
            Check(position + 12 <= bytes.Length, "PNG 图片不完整。");
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
            Check(size <= MaximumBytes && position + 12L + size <= bytes.Length, "PNG 图片段超限。");
            int length = (int)size;
            var name = Encoding.ASCII.GetString(bytes, position + 4, 4);
            var data = bytes.AsSpan(position + 8, length).ToArray();
            Check(Crc(bytes.AsSpan(position + 4, length + 4)) == BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8 + length)), "PNG 图片校验失败。");
            Check(name.All(char.IsAsciiLetter) && name is not ("acTL" or "fcTL" or "fdAT"), "暂不支持动画 PNG。");
            if (info is null)
            {
                Check(name == "IHDR" && length == 13, "PNG 图片头无效。");
                info = Dimensions("png", BinaryPrimitives.ReadInt32BigEndian(data), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)));
                int channels = data[9] switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                Check(channels > 0 && (data[8] is 8 or 16 || data[9] is 0 or 3 && data[8] is 1 or 2 or 4)
                    && !(data[9] == 3 && data[8] == 16) && data[10] == 0 && data[11] == 0 && data[12] <= 1, "PNG 编码无效。");
                var bits = channels * data[8];
                if (data[12] == 0) expected = (1 + ((long)info.Width * bits + 7) / 8) * info.Height;
                else
                {
                    int[] xs = [0, 4, 0, 2, 0, 1, 0], ys = [0, 0, 4, 0, 2, 0, 1], dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
                    for (int p = 0; p < 7; p++)
                    {
                        int w = Math.Max(0, (info.Width - xs[p] + dx[p] - 1) / dx[p]), h = Math.Max(0, (info.Height - ys[p] + dy[p] - 1) / dy[p]);
                        if (w > 0 && h > 0) expected += (1 + ((long)w * bits + 7) / 8) * h;
                    }
                }
            }
            else Check(name != "IHDR", "PNG 图片头重复。");
            if (name == "IDAT") { Check(!dataEnded, "PNG 数据段不连续。"); compressed.Write(data); seenData = true; }
            else if (seenData) dataEnded = true;
            chunks.Add((name, data));
            position += length + 12;
            if (name == "IEND") { Check(length == 0 && seenData && position == bytes.Length, "PNG 结束段无效。"); ended = true; break; }
        }
        Check(ended && info is not null, "PNG 图片不完整。");
        compressed.Position = 0;
        using var raw = new MemoryStream();
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            byte[] buffer = new byte[16384]; int count; long total = 0;
            while ((count = zlib.Read(buffer)) > 0) { total += count; Check(total <= expected, "PNG 解压数据超出尺寸范围。"); if (optimize) raw.Write(buffer, 0, count); }
            Check(total == expected, "PNG 像素数据不完整。");
        }
        catch (InvalidDataException) { throw new MarketplaceException("PNG 压缩数据无效。"); }
        if (!optimize) return (info!, bytes);
        using var optimized = new MemoryStream();
        using (var zlib = new ZLibStream(optimized, CompressionLevel.SmallestSize, leaveOpen: true)) raw.WriteTo(zlib);
        if (optimized.Length >= compressed.Length) return (info!, bytes.ToArray());
        using var output = new MemoryStream(); output.Write(Signature); bool wrote = false;
        foreach (var chunk in chunks)
        {
            if (chunk.Name == "IDAT") { if (wrote) continue; WriteChunk(output, "IDAT", optimized.ToArray()); wrote = true; }
            else WriteChunk(output, chunk.Name, chunk.Data);
        }
        return (info!, output.Length < bytes.Length ? output.ToArray() : bytes.ToArray());
    }
    private static void WriteChunk(Stream stream, string name, byte[] data)
    {
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); stream.Write(length);
        var body = Encoding.ASCII.GetBytes(name).Concat(data).ToArray(); stream.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc(body)); stream.Write(length);
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes) { crc ^= value; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u); }
        return ~crc;
    }
    private static void Check(bool value, string message) { if (!value) throw new MarketplaceException(message); }
}
