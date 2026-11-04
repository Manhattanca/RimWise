using System.Globalization;
using RimWise.Core;

namespace RimWise.Integration.Scanning;

/// <summary>Reads an optional local preview and constructs links only from numeric workshop IDs.</summary>
public static class ModPresentationFiles
{
    public const int MaximumPreviewBytes = 8 * 1024 * 1024;

    public static byte[]? ReadPreview(ModInstallation mod, CancellationToken cancellation = default)
    {
        foreach (var name in new[] { "Preview.png", "Preview.jpg", "Preview.jpeg" })
        {
            cancellation.ThrowIfCancellationRequested();
            var file = Path.Combine(mod.Directory, "About", name);
            if (!File.Exists(file)) continue;
            var boundary = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(mod.Directory))
                ?? throw new IOException("无法识别模组目录。");
            ReadOnlyMetadata.CheckPath(file, boundary);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaximumPreviewBytes) throw new InvalidDataException("预览图文件过大。");
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (output.Length + count > MaximumPreviewBytes) throw new InvalidDataException("预览图文件过大。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        return null;
    }

    public static Uri? WorkshopUri(ModInstallation? mod) => mod is not null
        && mod.WorkshopId.Length is > 0 and <= 20
        && ulong.TryParse(mod.WorkshopId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
        ? new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id.ToString(CultureInfo.InvariantCulture)) : null;
}
