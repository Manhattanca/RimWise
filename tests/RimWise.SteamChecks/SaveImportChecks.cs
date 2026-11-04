using System.Security.Cryptography;
using System.Text;
using System.Xml;
using RimWise.Formats;
using ZstdSharp;

internal static class SaveImportChecks
{
    private const string Header = "<savegame><meta><gameVersion>1.7.4998</gameVersion><modIds><li>ludeon.rimworld</li><li>example.mod</li><li>example.mod</li></modIds><modNames><li>Core</li><li>中文 模组</li><li>中文 模组</li></modNames><modSteamIds><li>0</li><li>123456789</li><li>123456789</li></modSteamIds></meta>";
    private const string Save = Header + "<game><maps>Private world data</maps></game></savegame>";
    private static readonly string[] Ids = ["ludeon.rimworld", "example.mod", "example.mod"];
    private static byte[] Compress(byte[] bytes)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(bytes).ToArray();
    }
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Save import check failed."); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or XmlException) { return; }
        throw new InvalidOperationException("Invalid save was accepted.");
    }

    internal static void Run(Action<string, Action> check)
    {
        var codec = new OrderFileCodec();
        check("Zstandard save imports version, ordered duplicate IDs and workshop metadata without world data", () =>
        {
            using var stream = new MemoryStream(Compress(Encoding.UTF8.GetBytes(Save)));
            var result = codec.Read(stream);
            Assert(stream.CanRead && result.SourceFormat == OrderFileFormat.SavegameRws);
            Assert(result.Configuration.GameVersion == "1.7.4998" && result.Configuration.Order.PackageIds.SequenceEqual(Ids));
            var xml = Encoding.UTF8.GetString(codec.Write(result, OrderFileFormat.RmlXml));
            Assert(xml.Contains("中文 模组") && xml.Contains("123456789") && !xml.Contains("Private world data"));
            foreach (var format in OrderFileCodec.ExportFormats)
                Assert(codec.Read(new MemoryStream(codec.Write(result, format))).Configuration.Order.PackageIds.SequenceEqual(Ids));
        });
        check("Compressed large world reads only a bounded prefix and never materializes the map", () =>
        {
            var bytes = Encoding.UTF8.GetBytes(Header + "<game>" + new string('x', 40 * 1024 * 1024) + "</game></savegame>");
            using var stream = new CountingStream(new MemoryStream(Compress(bytes)));
            Assert(codec.Read(stream).Configuration.Order.PackageIds.SequenceEqual(Ids));
            Assert(stream.BytesRead <= 16384);
        });
        check("Uncompressed XML, UTF16 and JSON order imports remain compatible", () =>
        {
            foreach (var bytes in new[] { Encoding.UTF8.GetBytes(Save), Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Save)).ToArray() })
                Assert(codec.Read(new MemoryStream(bytes)).Configuration.Order.PackageIds.SequenceEqual(Ids));
            var json = Encoding.UTF8.GetBytes("{\"version\":\"1.7\",\"activeMods\":[\"ludeon.rimworld\"]}");
            Assert(codec.Read(new MemoryStream(json)).SourceFormat == OrderFileFormat.RimSortJson);
        });
        check("Compressed save detects content at the current stream position and supports nonseekable input", () =>
        {
            var bytes = Compress(Encoding.UTF8.GetBytes(Save));
            using var prefixed = new MemoryStream(new byte[] { 1, 2, 3 }.Concat(bytes).ToArray());
            prefixed.Position = 3;
            Assert(codec.Read(prefixed).Configuration.Order.PackageIds.SequenceEqual(Ids));
            using var nonseekable = new CountingStream(new MemoryStream(bytes), seekable: false);
            Assert(codec.Read(nonseekable).Configuration.Order.PackageIds.SequenceEqual(Ids));
        });
        check("Compressed malformed headers and external entities fail without accepting partial lists", () =>
        {
            foreach (var text in new[] { "not xml", "<savegame><meta><modIds>", "<savegame><game/></savegame>",
                "<!DOCTYPE savegame [<!ENTITY x SYSTEM 'file:///must-not-read'>]>" + Save,
                Save.Replace("<modIds>", "<oversized>" + new string('x', 17 * 1024 * 1024) + "</oversized><modIds>") })
                Reject(() => codec.Read(new MemoryStream(Compress(Encoding.UTF8.GetBytes(text)))));
        });
        check("Damaged Zstandard data reports a readable compressed-save error", () =>
        {
            foreach (var bytes in new[] { new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0xFF, 0xFF, 0xFF } })
            {
                try { codec.Read(new MemoryStream(bytes)); }
                catch (InvalidDataException ex) when (ex.Message.Contains("压缩存档")) { continue; }
                throw new InvalidOperationException("Missing compressed-save diagnosis.");
            }
        });
        check("File service imports a Chinese compressed filename without modifying the source", () =>
        {
            var root = Path.Combine(AppContext.BaseDirectory, "save-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "中文 存档.rws");
            try
            {
                File.WriteAllBytes(path, Compress(Encoding.UTF8.GetBytes(Save)));
                var result = ReadUnchanged(path);
                Assert(result.Configuration.Order.PackageIds.SequenceEqual(Ids));
            }
            finally { File.Delete(path); Directory.Delete(root); }
        });
    }

    internal static void Replay(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = ReadUnchanged(path);
        Assert(result.SourceFormat == OrderFileFormat.SavegameRws && result.Configuration.Order.PackageIds.Count > 0);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { SaveImport = "passed", Version = result.Configuration.GameVersion,
            Mods = result.Configuration.Order.PackageIds.Count, ElapsedMilliseconds = watch.ElapsedMilliseconds, SourceUnchanged = true }));
    }

    private static OrderFileDocument ReadUnchanged(string path)
    {
        byte[] Hash() { using var input = File.OpenRead(path); return SHA256.HashData(input); }
        var before = Hash();
        var modified = File.GetLastWriteTimeUtc(path);
        var result = new OrderFileService().Read(path);
        Assert(before.SequenceEqual(Hash()) && modified == File.GetLastWriteTimeUtc(path));
        return result;
    }

    private sealed class CountingStream(Stream inner, bool seekable = true) : Stream
    {
        internal long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); BytesRead += n; return n; }
        public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); BytesRead += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
