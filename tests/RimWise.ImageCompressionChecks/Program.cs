using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimWise.Desktop.Services;
using RimWise.Integration.Marketplace;

var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks.Add(message);
}
byte[] Encode(byte[] pixels, int size, bool jpeg = false, bool highDepth = false)
{
    BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 100 } : new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96,
        highDepth ? PixelFormats.Gray16 : PixelFormats.Bgra32, null, pixels, size * (highDepth ? 2 : 4))));
    using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
}
byte[] Decode(byte[] bytes)
{
    using var stream = new MemoryStream(bytes);
    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
    return pixels;
}
try
{
    Check(MarketplaceImageCompression.Compress([], default).Length == 0, "Image-free submissions need no compression");
    var tiny = Encode([20, 40, 60, 255], 1);
    var small = MarketplaceImageCompression.Compress([tiny, tiny, tiny], default);
    Check(small.All(bytes => Decode(bytes).SequenceEqual(Decode(tiny))), "Under-budget images retain exact pixels");
    var jpeg = Encode([20, 40, 60, 255], 1, jpeg: true);
    Check(MarketplaceImageCompression.Compress([jpeg], default)[0].SequenceEqual(jpeg), "Under-budget JPEG is not re-encoded");
    var random = new Random(1607);
    const int size = 1024;
    var pixels = new byte[size * size * 4]; random.NextBytes(pixels);
    for (int p = 0; p < pixels.Length; p++) pixels[p] &= 127;
    for (int p = 3; p < pixels.Length; p += 4) pixels[p] = 255;
    var source = Encode(pixels, size);
    using (var inspection = new MemoryStream(source))
    {
        var frame = BitmapDecoder.Create(inspection, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Console.WriteLine($"Fixture format: {frame.Format}; profiles: {frame.ColorContexts?.Count}");
    }
    var originals = new[] { source.ToArray(), source.ToArray(), source.ToArray() };
    Check(originals.Sum(bytes => (long)bytes.Length) > MarketplaceImageCompression.TargetTotalBytes, "Opaque fixture exceeds the upload target");
    var compressed = MarketplaceImageCompression.Compress(originals, default);
    Console.WriteLine($"Opaque source total: {source.Length * 3L}; compressed: {compressed.Sum(bytes => (long)bytes.Length)}");
    Check(compressed.Sum(bytes => (long)bytes.Length) <= MarketplaceImageCompression.TargetTotalBytes, "Three opaque images fit the 7 MB combined target");
    Check(originals.All(bytes => bytes.SequenceEqual(source)), "Compression never modifies input buffers");
    var gammaChunk = new byte[16]; BinaryPrimitives.WriteInt32BigEndian(gammaChunk, 4);
    Encoding.ASCII.GetBytes("gAMA").CopyTo(gammaChunk, 4); BinaryPrimitives.WriteInt32BigEndian(gammaChunk.AsSpan(8), 45455);
    uint crc = uint.MaxValue;
    foreach (byte value in gammaChunk.AsSpan(4, 8))
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
    }
    BinaryPrimitives.WriteUInt32BigEndian(gammaChunk.AsSpan(12), ~crc);
    using var gammaStream = new MemoryStream(); gammaStream.Write(source.AsSpan(0, 33)); gammaStream.Write(gammaChunk);
    for (int position = 33; position < source.Length;)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(position));
        if (Encoding.ASCII.GetString(source, position + 4, 4) != "gAMA") gammaStream.Write(source.AsSpan(position, length + 12));
        position += length + 12;
    }
    var gammaSource = gammaStream.ToArray();
    var gammaResult = MarketplaceImageCompression.Compress([gammaSource, gammaSource, gammaSource], default);
    Check(gammaResult.All(bytes => bytes.AsSpan().IndexOf(gammaChunk) >= 0), "PNG gamma metadata is retained after upload compression");
    foreach (var bytes in compressed)
    {
        var info = MarketplaceImages.Inspect(bytes);
        Check(info.Extension == "png", "PNG keeps its color interpretation without conversion to JPEG");
        Check(info.Width == size && info.Height == size, "Upload preserves original dimensions");
        var actual = Decode(bytes);
        long error = 0; int max = 0;
        for (int p = 0; p < actual.Length; p++) { int d = Math.Abs(actual[p] - pixels[p]); error += (long)d * d; max = Math.Max(max, d); }
        Check(max <= 2, "PNG near-lossless pixels differ by no more than two channel levels");
        File.WriteAllBytes(Path.Combine(output, "sample-" + checks.Count + "." + info.Extension), bytes);
    }
    random.NextBytes(pixels);
    var alpha = Encode(pixels, size);
    var transparent = MarketplaceImageCompression.Compress([alpha, alpha, alpha], default);
    foreach (var bytes in transparent)
    {
        var actual = Decode(bytes);
        Check(MarketplaceImages.Inspect(bytes).Extension == "png", "Transparent sources cannot turn into JPEG");
        bool exactAlpha = true, closeRgb = true;
        for (int p = 0; p < actual.Length; p += 4)
        {
            exactAlpha &= actual[p + 3] == pixels[p + 3];
            for (int c = 0; c < 3; c++) closeRgb &= Math.Abs(actual[p + c] - pixels[p + c]) <= 2;
        }
        Check(exactAlpha && closeRgb, "Transparency stays exact while RGB retains near-lossless bounds");
    }
    Check(transparent.Sum(bytes => (long)bytes.Length) > MarketplaceImageCompression.TargetTotalBytes,
        "Unreachable target preserves image quality and does not reject the submission");
    const int photoSize = 1536;
    var photoPixels = new byte[photoSize * photoSize * 4];
    for (int p = 0; p < photoPixels.Length; p += 4)
    {
        byte value = (byte)random.Next(256);
        photoPixels[p] = photoPixels[p + 1] = photoPixels[p + 2] = value; photoPixels[p + 3] = 255;
    }
    var photo = Encode(photoPixels, photoSize, jpeg: true);
    Check(photo.Length <= MarketplaceImages.MaximumBytes && photo.Length * 3L > MarketplaceImageCompression.TargetTotalBytes,
        "High-detail JPEG fixture is valid and exceeds the batch target");
    var photoResult = MarketplaceImageCompression.Compress([photo, photo, photo], default);
    Check(photoResult.Sum(bytes => (long)bytes.Length) < photo.Length * 3L, "High-detail JPEG batches are reduced by high-fidelity encoding");
    var photoReference = Decode(photo);
    foreach (var bytes in photoResult)
    {
        var actual = Decode(bytes); long squared = 0, outliers = 0;
        for (int p = 0; p < actual.Length; p += 4)
        {
            CheckAlpha(actual[p + 3]);
            for (int c = 0; c < 3; c++) { int delta = actual[p + c] - photoReference[p + c]; squared += delta * delta; if (Math.Abs(delta) > 24) outliers++; }
        }
        long channels = (long)photoSize * photoSize * 3;
        Check(squared <= channels * 9 && outliers * 1000 <= channels, "JPEG output satisfies measured pixel fidelity bounds");
        Check(MarketplaceImages.Inspect(bytes) is { Width: photoSize, Height: photoSize }, "JPEG output retains full resolution");
        static void CheckAlpha(byte alphaValue) { if (alphaValue != 255) throw new InvalidOperationException("JPEG alpha changed"); }
    }
    const int graySize = 1200;
    var grayPixels = new byte[graySize * graySize * 2]; random.NextBytes(grayPixels);
    var gray = Encode(grayPixels, graySize, highDepth: true);
    Check(gray.Length * 3L > MarketplaceImageCompression.TargetTotalBytes, "High-depth fixture exceeds the upload target");
    var grayResult = MarketplaceImageCompression.Compress([gray, gray, gray], default);
    Check(grayResult.All(bytes => bytes.SequenceEqual(MarketplaceImages.Optimize(gray))), "High-depth originals never lose precision to reach a target");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    bool cancelled = false;
    try { MarketplaceImageCompression.Compress([source], cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Cancelled compression stops before returning upload bytes");
    bool countRejected = false;
    try { MarketplaceImageCompression.Compress([tiny, tiny, tiny, tiny], default); } catch (InvalidDataException) { countRejected = true; }
    Check(countRejected, "Four-image batches remain rejected");
    bool invalidRejected = false;
    try { MarketplaceImageCompression.Compress([[1, 2, 3]], default); } catch (MarketplaceException) { invalidRejected = true; }
    Check(invalidRejected, "Invalid input cannot enter the upload pipeline");
    var manifest = new MarketplaceManifest(1, "compression-fixture", "1.0.0", "合成图片", "ExampleAuthor", "", "1.7", [], null, [new("ludeon.rimworld", null)]);
    var uploads = compressed.Select((bytes, i) => MarketplaceImages.Prepare(manifest.Author, manifest.Id, manifest.Version, i, bytes)).ToArray();
    manifest = manifest with { Images = uploads.Select(item => item.Image).ToArray() };
    var roundtrip = MarketplaceJson.ParseManifest(MarketplaceJson.SerializeManifest(manifest));
    foreach (var upload in uploads) MarketplaceImages.Verify(upload.Image, upload.Content);
    Check(roundtrip.Images.SequenceEqual(manifest.Images), "Compressed files remain compatible with current manifests and hash verification");
    File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks,
        TargetBytes = MarketplaceImageCompression.TargetTotalBytes, SourceTotal = source.Length * 3L,
        CompressedTotal = compressed.Sum(bytes => (long)bytes.Length), TransparentTotal = transparent.Sum(bytes => (long)bytes.Length),
        JpegSourceTotal = photo.Length * 3L, JpegCompressedTotal = photoResult.Sum(bytes => (long)bytes.Length) }));
    Console.WriteLine($"Passed: {checks.Count}. Results: {output}");
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
    Console.Error.WriteLine(ex); Environment.ExitCode = 1;
}
