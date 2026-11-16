using System.IO;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Desktop.Services;

internal sealed class MarketplaceNoticeStore(string profileDirectory)
{
    private sealed record NoticeState(int Version, bool Acknowledged);
    internal string FilePath => ScopedPaths.Child(profileDirectory, "Data/Marketplace/notice.json");

    internal bool IsAcknowledged()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path) || new FileInfo(path).Length is 0 or > 1024) return false;
            return JsonSerializer.Deserialize<NoticeState>(File.ReadAllBytes(path)) is { Version: 1, Acknowledged: true };
        }
        catch (Exception ex) when (IsStorageError(ex)) { return false; }
    }

    internal void Acknowledge()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = ScopedPaths.Ordinary(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(new NoticeState(1, true)));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, ScopedPaths.Ordinary(path), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException;
}
