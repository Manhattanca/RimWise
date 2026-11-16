using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using RimWise.Integration.Marketplace;

internal static class ImageChecks
{
    internal static IEnumerable<(string Name, Func<Task> Check)> All(string output)
    {
        yield return ("PNG optimization preserves exact filtered pixels and metadata", () => Run(() =>
        {
            var source = Png(); var smaller = MarketplaceImages.Optimize(source);
            Need(smaller.Length < source.Length); Need(Pixels(source).SequenceEqual(Pixels(smaller)));
            Need(Encoding.ASCII.GetString(smaller).Contains("Synthetic metadata"));
            var info = MarketplaceImages.Inspect(smaller); Need(info.Width == 64 && info.Height == 64);
            Directory.CreateDirectory(Path.Combine(output, "fixtures")); File.WriteAllBytes(Path.Combine(output, "fixtures", "image.png"), source);
        }));
        foreach (var mode in new[] { "four", "foreign", "hash", "length", "dimension", "traversal", "crc", "truncated", "oversized", "svg" })
        {
            yield return ("image submission rejects " + mode, () => Run(() =>
            {
                var bytes = Png(); var image = MarketplaceImages.Prepare("contributor", "synthetic-order", "1.0.0", 0, bytes).Image;
                Reject(() =>
                {
                    switch (mode)
                    {
                        case "four": MarketplaceImages.Validate("contributor", "synthetic-order", "1.0.0", [image,image,image,image]); break;
                        case "foreign": MarketplaceImages.Validate("another", "synthetic-order", "1.0.0", [image]); break;
                        case "hash": bytes[^1] ^= 1; MarketplaceImages.Verify(image, bytes); break;
                        case "length": MarketplaceImages.Verify(image with { Bytes = 1 }, bytes); break;
                        case "dimension": MarketplaceImages.Verify(image with { Width = 5 }, bytes); break;
                        case "traversal": MarketplaceImages.Validate("contributor", "synthetic-order", "1.0.0", [image with { Path = "../x.png" }]); break;
                        case "crc": bytes[30] ^= 1; MarketplaceImages.Inspect(bytes); break;
                        case "truncated": MarketplaceImages.Inspect(bytes.AsSpan(0, bytes.Length - 4)); break;
                        case "oversized": MarketplaceImages.Inspect(new byte[MarketplaceImages.MaximumBytes + 1]); break;
                        case "svg": MarketplaceImages.Inspect(Encoding.UTF8.GetBytes("<svg onload='alert(1)'/>")); break;
                    }
                });
            }));
        }
        yield return ("images roundtrip and old manifests keep image field absent", () => Run(() =>
        {
            var original = Checks.Sample(); Need(!Encoding.UTF8.GetString(MarketplaceJson.SerializeManifest(original)).Contains("\"images\""));
            var image = MarketplaceImages.Prepare(original.Author, original.Id, original.Version, 0, Png()).Image;
            var manifest = original with { Images = [image] };
            Need(MarketplaceJson.ParseManifest(MarketplaceJson.SerializeManifest(manifest)).Images.Single() == image);
            Need(MarketplaceJson.ParseCatalogue(MarketplaceJson.SerializeCatalogue(MarketplaceJson.CreateCatalogue([manifest]))).Entries.Single().Images.Single() == image);
        }));
        yield return ("anonymous images cache verified bytes without account credentials", async () =>
        {
            var bytes = Png(); var image = MarketplaceImages.Prepare("contributor", "synthetic-order", "1.0.0", 0, bytes).Image;
            var calls = 0;
            using var handler = new FakeHandler(request => { calls++; Need(request.Headers.Authorization is null); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; });
            using var client = new MarketplaceClient(Path.Combine(output, "image-cache"), handler);
            Need((await client.GetImageAsync(image)).SequenceEqual(bytes)); Need((await client.GetImageAsync(image)).SequenceEqual(bytes)); Need(calls == 1);
        });
        foreach (bool fail in new[] { false, true })
            yield return ("image uploads finish before manifest and PR, failure prevents PR: " + fail, async () =>
            {
                var manifest = Checks.Sample("Manhattanca"); var upload = MarketplaceImages.Prepare(manifest.Author, manifest.Id, manifest.Version, 0, Png());
                manifest = manifest with { Images = [upload.Image] }; bool imageDone = false, manifestDone = false, pullDone = false;
                const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; string branch = "";
                using var handler = new FakeHandler(request =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    object response;
                    if (path == "/user") response = new { login = "Manhattanca" };
                    else if (request.Method == HttpMethod.Get && path.Contains("/contents/")) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    else if (path.EndsWith("/git/ref/heads/main")) response = new { @object = new { type = "commit", sha } };
                    else
                    {
                        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()); var root = body.RootElement;
                        if (path.EndsWith("/git/refs")) { branch = root.GetProperty("ref").GetString()![11..]; response = new { @ref = "refs/heads/" + branch, @object = new { sha } }; }
                        else if (path.Contains("/contents/"))
                        {
                            var file = path.Split("/contents/")[1];
                            if (file.Contains("/images/")) { if (fail) return new HttpResponseMessage(HttpStatusCode.Conflict); Need(Convert.FromBase64String(root.GetProperty("content").GetString()!).SequenceEqual(upload.Content)); imageDone = true; }
                            else { Need(imageDone); manifestDone = true; }
                            response = new { content = new { path = file } };
                        }
                        else { Need(imageDone && manifestDone); pullDone = true; response = new { number = 12, html_url = "https://github.com/Manhattanca/RimWise-Marketplace/pull/12", state = "open", merged_at = (string?)null, @base = new { @ref = "main", repo = new { full_name = MarketplaceRepository.FullName } }, head = new { @ref = branch, repo = new { full_name = MarketplaceRepository.FullName } } }; }
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
                });
                using var publisher = new GitHubMarketplacePublisher(handler);
                try { await publisher.SubmitWithImagesAsync(manifest, [upload], "synthetic-token"); Need(!fail && pullDone); }
                catch (MarketplaceException) when (fail) { Need(!manifestDone && !pullDone); }
            });
    }
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Need(bool value) { if (!value) throw new InvalidOperationException("Image check failed."); }
    private static void Reject(Action action) { try { action(); } catch (MarketplaceException) { return; } throw new InvalidOperationException("Unsafe image accepted."); }
    internal static byte[] Png()
    {
        using var stream = new MemoryStream(); stream.Write([137,80,78,71,13,10,26,10]);
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header,64); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),64); header[8]=8; header[9]=6;
        Chunk(stream,"IHDR",header); Chunk(stream,"tEXt",Encoding.ASCII.GetBytes("Comment\0Synthetic metadata"));
        using var packed = new MemoryStream(); using(var z = new ZLibStream(packed,CompressionLevel.NoCompression,true)) z.Write(new byte[(64*4+1)*64]);
        Chunk(stream,"IDAT",packed.ToArray()); Chunk(stream,"IEND",[]); return stream.ToArray();
    }
    private static void Chunk(Stream stream,string name,byte[] data)
    {
        byte[] count=new byte[4]; BinaryPrimitives.WriteInt32BigEndian(count,data.Length); stream.Write(count); var body=Encoding.ASCII.GetBytes(name).Concat(data).ToArray(); stream.Write(body);
        uint crc=uint.MaxValue; foreach(var b in body) {crc^=b;for(int i=0;i<8;i++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);} BinaryPrimitives.WriteUInt32BigEndian(count,~crc);stream.Write(count);
    }
    private static byte[] Pixels(byte[] png)
    {
        using var packed=new MemoryStream(); for(int p=8;p<png.Length;) {int n=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));if(Encoding.ASCII.GetString(png,p+4,4)=="IDAT")packed.Write(png,p+8,n);p+=12+n;}
        packed.Position=0;using var z=new ZLibStream(packed,CompressionMode.Decompress);using var raw=new MemoryStream();z.CopyTo(raw);return raw.ToArray();
    }
}
