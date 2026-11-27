#if DEBUG
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RimWise.Core;
using RimWise.Desktop.Presentation;
using RimWise.Desktop.Services;
using RimWise.Formats;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

/// <summary>Real PNGs and real recycled list cells; no personal game or network activity.</summary>
internal static class ThumbnailRevisitSmokeCheck
{
    internal static void Attach(MainWindow window, string output)
    {
        window.ContentRendered += async (_, _) =>
        {
            Directory.CreateDirectory(output);
            var checks = new List<string>();
            var failures = new List<string>();
            try { await RunAsync(window, output, checks, failures); }
            catch (Exception ex) { failures.Add(ex.ToString()); }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
            window.ViewModel.MarkExported();
            Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
        };
    }

    private static async Task RunAsync(MainWindow window, string output, List<string> checks, List<string> failures)
    {
        void Check(bool value, string text) { if (value) checks.Add(text); else failures.Add(text); }
        var root = Path.Combine(output, "真实缩略图 [往返]");
        var mods = Enumerable.Range(0, 600).Select(index =>
        {
            var directory = Path.Combine(root, index.ToString());
            Directory.CreateDirectory(Path.Combine(directory, "About"));
            var bytes = Enumerable.Range(0, 64 * 64).SelectMany(_ => new byte[]
                { (byte)(index % 220 + 20), (byte)(index * 7 % 220 + 20), (byte)(index * 13 % 220 + 20), 255 }).ToArray();
            var image = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, bytes, 64 * 4);
            image.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(directory, "About", "Preview.png"))) encoder.Save(file);
            var mod = new ModInstallation(directory, index.ToString(), ModSource.Local, "",
                new("缩略图模组 " + index, "合成作者", "thumb." + index, ["1.7"], "", ModRequirements.Empty, false), [])
                { LastUpdatedUtc = DateTimeOffset.UnixEpoch, CodeKind = ModCodeKind.Xml };
            ModThumbnailDiskCache.Save(ModThumbnailDiskCache.PathFor(UserStorage.DirectoryPath, mod), mod,
                ModThumbnailDiskCache.Stamp(mod), image, default);
            return mod;
        }).ToArray();
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes("<ModsConfigData><version>1.7</version><activeMods>"
            + string.Concat(mods.Take(300).Select(mod => "<li>" + mod.PackageId + "</li>")) + "</activeMods></ModsConfigData>"));
        window.ViewModel.Load(new ModsConfigXmlFormat().Read(xml), Path.Combine(root, "ModsConfig.xml"));
        window.ViewModel.ApplyScan(new(new(GameVersion.Parse("1.7"), "1.7", ""), GameVersion.Parse("1.7"), new(mods), []));
        var order = window.ViewModel.Rows.Select(row => row.PackageId).ToArray();
        await Settle(window);
        var available = window.ScrollForCheck(true)!;
        var enabled = window.ScrollForCheck(false)!;
        async Task Warm(ScrollViewer scroll, DataGrid grid)
        {
            for (var row = 0; row <= 150; row += 6)
            {
                scroll.ScrollToVerticalOffset(Offset(grid, row));
                await Settle(window);
                await Task.WhenAll(Visible(grid).Select(t => t.ImageLoading));
            }
        }
        await Task.WhenAll(Visible(window.AvailableGrid).Concat(Visible(window.OrderGrid)).Select(t => t.ImageLoading));
        await Warm(available, window.AvailableGrid);
        available.ScrollToTop(); await Settle(window);
        var oneSide = Visible(window.AvailableGrid).ToArray();
        var oneBlank = oneSide.Count(t => ((Image)t.Child).Source is null);
        Check(oneSide.Length > 0 && oneBlank == 0, "Return after 150 MODs in one list is immediately populated before awaiting image tasks");
        await Task.WhenAll(oneSide.Select(t => t.ImageLoading));
        await Warm(enabled, window.OrderGrid);
        // The cache is shared by both lists; more than 256 distinct covers is common.
        available.ScrollToVerticalOffset(Offset(window.AvailableGrid, 150)); await Settle(window);
        available.ScrollToTop(); await Settle(window);
        var bothSides = Visible(window.AvailableGrid).ToArray();
        var bothBlank = bothSides.Count(t => ((Image)t.Child).Source is null);
        Check(bothSides.Length > 0 && bothBlank == 0, "Return after 150 MODs in each list reuses earlier covers without a blank frame");
        await Task.WhenAll(bothSides.Select(t => t.ImageLoading));
        var thumb = bothSides.First();
        var before = ((Image)thumb.Child).Source;
        var originalInstallation = thumb.Installation!;
        thumb.SetCurrentValue(ModThumbnail.InstallationProperty, originalInstallation with { LastUpdatedUtc = DateTimeOffset.UtcNow });
        var rebindImmediate = ReferenceEquals(before, ((Image)thumb.Child).Source);
        await thumb.ImageLoading;
        Check(before is not null && rebindImmediate, "Metadata refresh of the same installation preserves its displayed thumbnail while checking its source");
        thumb.SetCurrentValue(ModThumbnail.InstallationProperty, originalInstallation);
        await thumb.ImageLoading;
        for (var row = 0; row <= 165; row += 3)
        {
            available.ScrollToVerticalOffset(Offset(window.AvailableGrid, row));
            await Settle(window);
        }
        available.ScrollToTop(); await Settle(window);
        var rapid = Visible(window.AvailableGrid).ToArray();
        var rapidBlank = rapid.Count(t => ((Image)t.Child).Source is null);
        Check(rapid.Length > 0 && rapidBlank == 0, "Rapid traversal of 165 MODs and immediate return does not clear warm thumbnails");
        await Task.WhenAll(rapid.Select(t => t.ImageLoading));
        Check(rapid.All(t =>
        {
            var index = int.Parse(Path.GetFileName(t.Installation!.Directory));
            return FirstPixel((BitmapSource)((Image)t.Child).Source!)[0] == (byte)(index % 220 + 20);
        }), "Every recycled visible cell shows the cover belonging to its current MOD rather than a retained neighbour");

        // Deterministic reproduction of recycling between display of disk cache and source verification.
        var isolated = new ModThumbnailCache();
        BitmapSource? displayed = null;
        using (var cancel = new CancellationTokenSource())
        {
            try
            {
                await isolated.LoadAsync(mods[^1], cancel.Token, image =>
                { displayed = image; cancel.Cancel(); return Task.CompletedTask; });
            }
            catch (OperationCanceledException) { }
        }
        var revisit = await isolated.LoadAsync(mods[^1], default);
        var survivesCancellation = displayed is not null && ReferenceEquals(displayed, revisit);
        Check(survivesCancellation, "Already displayed disk thumbnail survives cancellation of the recycled cell without decoding it again");
        Check(isolated.Statistics.DiskReads == 1 && isolated.Statistics.SourceDecodes == 0,
            "Returning after display cancellation does not reread the cached PNG or decode an original");
        var clock = new CheckClock();
        var timed = new ModThumbnailCache(clock: clock);
        var timedImage = await timed.LoadAsync(mods[550], default);
        var timedStats = timed.Statistics;
        clock.Advance(TimeSpan.FromMinutes(3));
        Check(timed.TryGet(mods[550], out var expired) && ReferenceEquals(expired, timedImage),
            "Verification age does not remove a decoded thumbnail from memory");
        var checkedImage = await timed.LoadAsync(mods[550], default);
        Check(ReferenceEquals(checkedImage, timedImage) && timed.Statistics.DiskReads == timedStats.DiskReads
            && timed.Statistics.SourceDecodes == timedStats.SourceDecodes,
            "Revalidating unchanged source metadata neither rereads nor decodes the thumbnail");
        var changedSource = Path.Combine(mods[550].Directory, "About", "Preview.png");
        File.Copy(Path.Combine(mods[551].Directory, "About", "Preview.png"), changedSource, true);
        File.SetLastWriteTimeUtc(changedSource, DateTime.UtcNow.AddSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(3));
        Check(timed.TryGet(mods[550], out var oldImage) && ReferenceEquals(oldImage, timedImage),
            "Changed source keeps its previous cover visible until background verification finishes");
        var changedImage = await timed.LoadAsync(mods[550], default);
        Check(changedImage is not null && !FirstPixel(changedImage).SequenceEqual(FirstPixel(timedImage!)),
            "Background verification replaces a genuinely changed source instead of retaining stale pixels indefinitely");
        File.Delete(changedSource); clock.Advance(TimeSpan.FromMinutes(3));
        Check(await timed.LoadAsync(mods[550], default) is null && timed.TryGet(mods[550], out var removedImage) && removedImage is null,
            "Confirmed removal clears its own stale cover without affecting another installation");
        var small = new ModThumbnailCache(maximumMemoryBytes: 60_000, maximumEntries: 3);
        foreach (var mod in mods.Take(3)) await small.LoadAsync(mod, default);
        Check(small.TryGet(mods[0], out _), "LRU lookup touches a recently viewed cover");
        await small.LoadAsync(mods[3], default);
        Check(small.TryGet(mods[0], out _) && !small.TryGet(mods[1], out _) && small.EntryCount <= 3 && small.EstimatedBytes <= 60_000,
            "Memory pressure evicts least recently used entries rather than the oldest insert regardless of access");
        Check(ModThumbnailCache.Shared.EntryCount > 256 && ModThumbnailCache.Shared.EstimatedBytes <= 32_000_000,
            "Shared decoded cache keeps hundreds of small covers within its memory budget");
        Check(order.SequenceEqual(window.ViewModel.Rows.Select(row => row.PackageId)), "Scrolling and thumbnail refresh never modify actual load order");
        File.WriteAllText(Path.Combine(output, "revisit.json"), JsonSerializer.Serialize(new
        {
            Installed = 600, Enabled = 300, RowsPerSide = 150, RapidTraversalRows = 165,
            OneSideVisible = oneSide.Length, OneSideBlankOnReturn = oneBlank,
            BothSidesVisible = bothSides.Length, BothSidesBlankOnReturn = bothBlank,
            MetadataRebindPreserved = rebindImmediate, RapidVisible = rapid.Length,
            RapidBlankOnReturn = rapidBlank, DisplayedImageSurvivesCancellation = survivesCancellation,
            MemoryEntries = ModThumbnailCache.Shared.EntryCount, EstimatedMemoryBytes = ModThumbnailCache.Shared.EstimatedBytes,
            DiskReads = ModThumbnailCache.Shared.Statistics.DiskReads, SourceDecodes = ModThumbnailCache.Shared.Statistics.SourceDecodes,
            Note = "Synthetic PNGs, real DataGrid recycling. Blank measurements are dispatcher-visible sources, not presented GPU FPS or proof of the exact personal-machine cause."
        }, new JsonSerializerOptions { WriteIndented = true }));
        PrototypeSmokeCheck.SavePreview(window, Path.Combine(output, "thumbnail-revisit.png"));
    }

    private sealed class CheckClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private static byte[] FirstPixel(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0); return pixel;
    }

    private static Task Settle(MainWindow window) => window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle).Task;
    private static double Offset(DataGrid grid, int row) => System.Windows.Controls.VirtualizingPanel.GetScrollUnit(grid)
        == ScrollUnit.Pixel ? row * grid.RowHeight : row;
    private static IEnumerable<ModThumbnail> Visible(DataGrid grid) => Descendants(grid).OfType<ModThumbnail>()
        .Where(t => t.IsVisible && t.Installation is not null && t.TransformToAncestor(grid).TransformBounds(new Rect(t.RenderSize))
            .IntersectsWith(new Rect(grid.RenderSize)));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
#endif
