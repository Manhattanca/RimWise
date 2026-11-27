#if DEBUG
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimWise.Core;
using RimWise.Desktop.Services;
using RimWise.Integration.Cache;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;
using RimWise.Formats;

namespace RimWise.Desktop;

internal static class ThumbnailCacheSmokeCheck
{
    internal static async Task RunAsync(string output, List<string> checks)
    {
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        var root = Path.Combine(output, "缓存缩略图 [验证]");
        var directory = Path.Combine(root, "Mods", "sample");
        Directory.CreateDirectory(Path.Combine(directory, "About"));
        var source = Path.Combine(directory, "About", "Preview.png");
        void Picture(byte red, byte blue)
        {
            var bytes = new byte[180 * 120 * 3];
            for (int i = 0; i < bytes.Length; i += 3) { bytes[i] = red; bytes[i + 2] = blue; }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(180, 120, 96, 96, PixelFormats.Rgb24, null, bytes, 180 * 3)));
            using var file = File.Create(source); encoder.Save(file);
        }
        byte[] Pixel(BitmapSource? source)
        {
            if (source is null) return [];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4]; converted.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0); return pixel;
        }
        Picture(220, 10);
        var mod = new ModInstallation(directory, "sample", ModSource.Local, "", null, []);
        var first = await new ModThumbnailCache().LoadAsync(mod, default);
        var cachePath = ModThumbnailDiskCache.PathFor(UserStorage.DirectoryPath, mod);
        Check(first is { IsFrozen:true, PixelWidth: <=64, PixelHeight: <=64 } && File.Exists(cachePath)
            && new FileInfo(cachePath).Length < 64 * 1024, "First visible cover creates a small frozen thumbnail in user data");
        var cachedWasShown = false;
        using (var originalLocked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var warm = await new ModThumbnailCache().LoadAsync(mod, default, image =>
            { cachedWasShown = image is not null; return Task.CompletedTask; });
            Check(warm is not null && cachedWasShown && Pixel(first).SequenceEqual(Pixel(warm)),
                "New cache instance displays disk thumbnail without reading or decoding an unchanged original");
        }
        Picture(10, 230); File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
        BitmapSource? beforeRefresh = null;
        var refreshed = await new ModThumbnailCache().LoadAsync(mod, default, image =>
        { beforeRefresh = image; return Task.CompletedTask; });
        Check(Pixel(beforeRefresh).SequenceEqual(Pixel(first)) && Pixel(refreshed)[0] > Pixel(refreshed)[2],
            "Cached cover is presented before changed source refresh completes");
        File.WriteAllText(cachePath, "broken cache");
        Check(await new ModThumbnailCache().LoadAsync(mod, default) is not null,
            "Corrupt thumbnail cache falls back to its source and is replaced");
        File.Delete(source);
        Check(await new ModThumbnailCache().LoadAsync(mod, default) is null, "Removed source clears a stale cached cover after verification");
        var beforeCancel = File.ReadAllBytes(cachePath);
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            try { await new ModThumbnailCache().LoadAsync(mod, stop.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        Check(beforeCancel.SequenceEqual(File.ReadAllBytes(cachePath)), "Cancelled thumbnail load cannot replace its cache");
        foreach (var size in new[] { (Width: 1, Height: 64), (Width: 64, Height: 1) })
        {
            var tiny = BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Bgra32,
                null, new byte[size.Width * size.Height * 4], size.Width * 4);
            tiny.Freeze();
            ModThumbnailDiskCache.Save(cachePath, mod, "narrow-test", tiny, default);
            var read = ModThumbnailDiskCache.Read(cachePath, mod);
            Check(read?.Image is { IsFrozen: true } image && image.PixelWidth == size.Width && image.PixelHeight == size.Height,
                $"Disk cache preserves {size.Width}x{size.Height} dimensions without upscaling narrow images");
        }
        var bounded = new ModThumbnailCache();
        for (var i = 0; i < 270; i++)
        {
            var folder = Path.Combine(root, "many", i.ToString()); Directory.CreateDirectory(folder);
            await bounded.LoadAsync(mod with { Directory = folder }, default);
        }
        Check(bounded.EntryCount == 270 && bounded.EstimatedBytes <= 32_000_000,
            "Empty-cover entries remain bounded without prematurely evicting recently displayed real thumbnails");

        File.WriteAllText(Path.Combine(directory, "About", "About.xml"), "<ModMetaData><name>缓存类型</name><packageId>cache.type</packageId></ModMetaData>");
        Directory.CreateDirectory(Path.Combine(directory, "Defs"));
        var paths = new GamePaths {LocalModsDirectory=Path.Combine(root,"Mods"),ConfigDirectory=root,TargetVersionOverride="1.7"};
        var scan = await Task.Run(() => new ModScanner().Scan(paths));
        var configPath = Path.Combine(root,"ModsConfig.xml");
        File.WriteAllText(configPath,"<ModsConfigData><version>1.7</version><activeMods><li>cache.type</li></activeMods></ModsConfigData>");
        using var input = File.OpenRead(configPath); var config = new ModsConfigXmlFormat().Read(input);
        var store = new StartupCacheStore(Path.Combine(root,"profile"));
        Check(store.TrySave(paths,config,scan) && store.TryLoad(paths)?.Scan.Catalogue.Installations.Single().ContentKinds == ModContentKinds.Definitions,
            "Startup cache restores mod type and content badges before a fresh scan");
        Directory.Delete(Path.Combine(directory,"Defs")); Directory.CreateDirectory(Path.Combine(directory,"Textures"));
        var updated = await Task.Run(() => new ModScanner().InspectContent(scan));
        Check(updated.Catalogue.Installations.Single().ContentKinds == ModContentKinds.Textures,
            "Background content verification detects changed types and removes obsolete badges");
    }
}
#endif
