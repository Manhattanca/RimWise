using System.IO.Compression;
using System.Text;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Diagnostics;

/// <summary>Transparent reading of retained evidence; decompression is always bounded.</summary>
public static class StoredEvidence
{
    private static (string Current, string[] Previous)? relocation;

    // Checkpoint bytes and their integrity chain remain unchanged. Only local evidence lookup
    // translates roots that the trusted profile locator has actually migrated.
    internal static void ConfigureRelocation(string current, IEnumerable<string> previous)
        => relocation = (ScopedPaths.Child(current, "Data"), previous.Select(root => ScopedPaths.Child(root, "Data") + Path.DirectorySeparatorChar).ToArray());

    public static string Resolve(string path)
    {
        if (relocation is { } roots && Path.IsPathFullyQualified(path))
            foreach (var old in roots.Previous)
                if (path.StartsWith(old, StringComparison.OrdinalIgnoreCase))
                {
                    var moved = ScopedPaths.Child(roots.Current, path[old.Length..]);
                    if (File.Exists(moved)) return moved;
                    if (File.Exists(moved + ".gz")) return moved + ".gz";
                }
        return File.Exists(path) ? path : File.Exists(path + ".gz") ? path + ".gz" : path;
    }

    public static string ReadText(string path, int maximumBytes)
    {
        path = ScopedPaths.Ordinary(Resolve(path), true);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var gzip = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : null;
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        Stream source = gzip is null ? input : gzip;
        var remaining = gzip is null ? input.Length : long.MaxValue;
        int count;
        while (remaining > 0 && (count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) != 0)
        {
            if (content.Length + count > maximumBytes) throw new InvalidDataException("Evidence exceeds file limit.");
            content.Write(buffer, 0, count);
            remaining -= count;
        }
        content.Position = 0;
        using var reader = new StreamReader(content, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void Compress(string path)
    {
        path = ScopedPaths.Ordinary(path, true);
        using var pins = new PinnedDirectorySet();
        pins.PinParent(path);
        var modified = File.GetLastWriteTimeUtc(path);
        var target = ScopedPaths.Ordinary(path + ".gz");
        var temporary = ScopedPaths.Ordinary(path + ".compressing");
        // Exclude writers until the compressed replacement is durable. If interrupted,
        // the original is authoritative and the next pass replaces the temporary file.
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) input.CopyTo(gzip);
                output.Flush(true);
            }
            File.Move(temporary, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, modified);
        }
        File.Delete(path);
    }
}
