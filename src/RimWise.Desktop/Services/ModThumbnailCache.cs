using System.IO;
using System.Windows.Media.Imaging;
using RimWise.Core;

namespace RimWise.Desktop.Services;

/// <summary>Frozen, decoded covers outlive recycled cells; bounded LRU memory and two background decoders.</summary>
internal sealed class ModThumbnailCache
{
    internal static ModThumbnailCache Shared { get; } = new();
    private readonly SemaphoreSlim decoding = new(2);
    private readonly ModPreviewService previews = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> recent = new();
    private readonly long maximumMemoryBytes;
    private readonly int maximumEntries;
    private readonly TimeProvider clock;
    private long estimatedBytes;
    private sealed record Snapshot(BitmapSource? Image, string? Stamp, long? Revision, DateTimeOffset VerifiedAt);
    private sealed record Entry(Snapshot Value, long Bytes, LinkedListNode<string> Node);

    internal ModThumbnailCache(long maximumMemoryBytes = 32_000_000, int maximumEntries = 4096, TimeProvider? clock = null)
    {
        if (maximumMemoryBytes < 1024 || maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumMemoryBytes));
        this.maximumMemoryBytes = maximumMemoryBytes;
        this.maximumEntries = maximumEntries;
        this.clock = clock ?? TimeProvider.System;
    }

    // A scanner refresh can replace metadata without changing the cover. Its revision
    // triggers background verification, not another cache identity or an empty cell.
    internal static string KeyFor(ModInstallation mod) => KeyFor(UserStorage.DirectoryPath, mod);
    private static string KeyFor(string storage, ModInstallation mod) => storage + "\0" + mod.Directory;
    internal bool TryGet(ModInstallation mod, out BitmapSource? image)
    {
        var snapshot = Find(KeyFor(mod));
        image = snapshot?.Image;
        return snapshot is not null;
    }

    private Snapshot? Find(string key)
    {
        lock (entries)
        {
            if (!entries.TryGetValue(key, out var entry)) return null;
            recent.Remove(entry.Node); recent.AddLast(entry.Node);
            return entry.Value;
        }
    }

    private bool Fresh(Snapshot? value, ModInstallation mod) => value is not null
        && value.Revision == mod.LastUpdatedUtc?.UtcTicks && value.VerifiedAt != default
        && clock.GetUtcNow() - value.VerifiedAt < TimeSpan.FromMinutes(2);

    private void Remember(string key, Snapshot value)
    {
        // The budget estimates decoded pixels plus entry/image overhead, not total
        // process or graphics-driver memory. Null entries also have a bounded cost.
        var bytes = value.Image is { } image
            ? 1024L + (long)((image.PixelWidth * Math.Max(1, image.Format.BitsPerPixel) + 31) / 32 * 4) * image.PixelHeight
            : 256L;
        lock (entries)
        {
            if (entries.Remove(key, out var old)) { recent.Remove(old.Node); estimatedBytes -= old.Bytes; }
            if (bytes > maximumMemoryBytes) return;
            var node = recent.AddLast(key);
            entries.Add(key, new(value, bytes, node)); estimatedBytes += bytes;
            while (estimatedBytes > maximumMemoryBytes || entries.Count > maximumEntries)
            {
                var oldest = recent.First!;
                estimatedBytes -= entries[oldest.Value].Bytes;
                entries.Remove(oldest.Value); recent.RemoveFirst();
#if DEBUG
                Interlocked.Increment(ref evictions);
#endif
            }
        }
    }

    internal async Task<BitmapSource?> LoadAsync(ModInstallation mod, CancellationToken token,
        Func<BitmapSource?, Task>? showCached = null)
    {
        var storage = UserStorage.DirectoryPath;
        var key = KeyFor(storage, mod);
        token.ThrowIfCancellationRequested();
        var memory = Find(key);
        if (Fresh(memory, mod)) return memory!.Image;
        // An expired verification stamp is not an expired bitmap. Keep it visible
        // while checking metadata; a return to a warm row never waits 35 ms.
        if (memory is not null && showCached is not null) await showCached(memory.Image).ConfigureAwait(false);
        if (memory is null) await Task.Delay(35, token).ConfigureAwait(false);
        await decoding.WaitAsync(token).ConfigureAwait(false);
        try
        {
            memory = Find(key);
            if (Fresh(memory, mod)) return memory!.Image;
            try
            {
                var path = ModThumbnailDiskCache.PathFor(storage, mod);
                if (memory is null)
                {
#if DEBUG
                    Interlocked.Increment(ref diskReads);
#endif
                    var disk = await Task.Run(() => ModThumbnailDiskCache.Read(path, mod), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (disk.HasValue)
                    {
                        memory = new(disk.Value.Image, disk.Value.Data.Stamp, mod.LastUpdatedUtc?.UtcTicks, default);
                        // Publish BEFORE the control callback/verification. A row can
                        // disappear immediately after display; cancellation must not
                        // throw away the decoded bitmap it has already shown.
                        Remember(key, memory);
                        if (showCached is not null) await showCached(memory.Image).ConfigureAwait(false);
                    }
                }
                var stamp = await Task.Run(() => ModThumbnailDiskCache.Stamp(mod), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (memory is not null && memory.Stamp == stamp)
                {
                    Remember(key, memory with { Revision = mod.LastUpdatedUtc?.UtcTicks, VerifiedAt = clock.GetUtcNow() });
                    return memory.Image;
                }
#if DEBUG
                Interlocked.Increment(ref sourceDecodes);
#endif
                var image = await previews.LoadAsync(mod, token, 64, 64).ConfigureAwait(false);
                var stable = await Task.Run(() => ModThumbnailDiskCache.Stamp(mod) == stamp, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (stable)
                {
                    Remember(key, new(image, stamp, mod.LastUpdatedUtc?.UtcTicks, clock.GetUtcNow()));
                    await Task.Run(() => ModThumbnailDiskCache.Save(path, mod, stamp, image, token), token).ConfigureAwait(false);
                }
                return image;
            }
            catch (Exception ex) when (ModThumbnailDiskCache.Failure(ex))
            {
                // An optional preview cannot break the list; transient I/O must not
                // discard an already usable frozen bitmap. Failed verification retries.
                token.ThrowIfCancellationRequested();
                if (memory is not null) return memory.Image;
                Remember(key, new(null, null, mod.LastUpdatedUtc?.UtcTicks, clock.GetUtcNow()));
                return null;
            }
        }
        finally { decoding.Release(); }
    }

#if DEBUG
    private long diskReads, sourceDecodes, evictions;
    internal int EntryCount { get { lock (entries) return entries.Count; } }
    internal long EstimatedBytes { get { lock (entries) return estimatedBytes; } }
    internal (long DiskReads, long SourceDecodes, long Evictions) Statistics =>
        (Interlocked.Read(ref diskReads), Interlocked.Read(ref sourceDecodes), Interlocked.Read(ref evictions));
#endif
}
