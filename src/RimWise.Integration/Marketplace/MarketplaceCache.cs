namespace RimWise.Integration.Marketplace;

/// <summary>Only backend-owned JSON files are touched. Last-good files survive failed/cancelled writes.</summary>
internal sealed class MarketplaceCache(string directory)
{
    private const long MaximumTotalBytes = 32L * 1024 * 1024;
    private const int MaximumManifestFiles = 128;
    private readonly string root = Path.GetFullPath(directory);
    internal const string CatalogueFile = "rw-marketplace-index.json";

    internal static string ManifestFile(MarketplaceEntry entry) => "rw-marketplace-manifest-" +
        MarketplaceJson.ComputeSha256(System.Text.Encoding.UTF8.GetBytes(MarketplaceJson.Identity(entry))) + ".json";

    internal async Task<byte[]?> ReadAsync(string name, int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafe(root);
        var path = Path.Combine(root, name);
        EnsureSafe(path);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        MarketplaceJson.Require(stream.Length <= maximumBytes, "Cached marketplace JSON exceeds its byte limit.");
        return await MarketplaceHttp.ReadBoundedAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    internal async Task WriteAsync(string name, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafe(root);
        Directory.CreateDirectory(root);
        EnsureSafe(root);
        var path = Path.Combine(root, name);
        EnsureSafe(path);
        var temporary = Path.Combine(root, "rw-marketplace-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafe(root);
            EnsureSafe(path);
            File.Move(temporary, path, overwrite: true);
            Prune();
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Prune()
    {
        EnsureSafe(root);
        var files = new DirectoryInfo(root).EnumerateFiles("rw-marketplace-*")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, @"^rw-marketplace-(manifest-[a-f0-9]{64}\.json|image-[a-f0-9]{64}\.bin)$"))
            .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long total = 0;
        for (var i = 0; i < files.Length; i++)
        {
            EnsureSafe(files[i].FullName);
            total += files[i].Length;
            if (i >= MaximumManifestFiles || total > MaximumTotalBytes) files[i].Delete();
        }
    }

    private static void EnsureSafe(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                MarketplaceJson.Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                    "Marketplace cache paths must not pass through filesystem links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
