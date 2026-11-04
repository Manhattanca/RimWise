using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Updates;

/// <summary>Exactly one compressed backup for the latest cleanup version; atomically replaced.
/// The caller holds the installation-state lock and keeps the source file exclusively open.</summary>
internal static class LatestCleanupBackup
{
    private sealed record Inventory(string Version, PackageFile[] Files);

    internal static void Append(Stream source, PackageFile file, string root, string version)
    {
        using var parent = PinnedDirectory.Open(root, create: true);
        var target = ScopedPaths.Child(root, "latest-cleanup.zip");
        var pending = ScopedPaths.Child(root, "latest-cleanup.pending");
        var retained = new List<PackageFile>();
        using (var output = new FileStream(pending, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using (var next = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (File.Exists(target))
                {
                    using var previous = ZipFile.OpenRead(target);
                    var index = previous.GetEntry("inventory.json") ?? throw new InvalidDataException("清理备份缺少清单。");
                    if (index.Length > 1024 * 1024) throw new InvalidDataException("清理备份清单过大。");
                    using var indexStream = index.Open();
                    var inventory = JsonSerializer.Deserialize<Inventory>(indexStream) ?? throw new InvalidDataException("清理备份清单无效。");
                    if (inventory.Files is null || inventory.Files.Length > 4096) throw new InvalidDataException("清理备份条目无效。");
                    if (inventory.Version == version)
                    {
                        foreach (var old in inventory.Files)
                        {
                            PackageManifest.ValidateFile(old);
                            if (old.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)) continue;
                            var archived = previous.GetEntry("files/" + old.Path) ?? throw new InvalidDataException("清理备份文件缺失。");
                            using var input = archived.Open();
                            using var copy = next.CreateEntry("files/" + old.Path, CompressionLevel.Fastest).Open();
                            input.CopyTo(copy);
                            retained.Add(old);
                        }
                    }
                }
                using (var content = next.CreateEntry("files/" + file.Path, CompressionLevel.Fastest).Open()) source.CopyTo(content);
                retained.Add(file);
                using var manifest = next.CreateEntry("inventory.json", CompressionLevel.Fastest).Open();
                JsonSerializer.Serialize(manifest, new Inventory(version, retained.ToArray()));
            }
            output.Flush(true);
        }
        // Verify the complete backup before replacing the last usable copy or deleting a source.
        using (var archive = ZipFile.OpenRead(pending))
        {
            foreach (var entry in retained)
            {
                using var content = archive.GetEntry("files/" + entry.Path)!.Open();
                if (!Convert.ToHexString(SHA256.HashData(content)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("升级清理备份校验失败。");
            }
        }
        File.Move(pending, target, overwrite: true);
    }
}
