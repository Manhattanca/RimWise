using System.IO;
using System.Text.Json;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private readonly ModActivityStore scanActivityStore = new(UserStorage.DirectoryPath);
    private Dictionary<string, ModActivityRecord> lastActivityRecords = new(StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, ModActivityRecord>> ReadActivityAsync(ModScanResult scan, bool observe)
    {
        try
        {
            lastActivityRecords = await Task.Run(() => observe
                ? scanActivityStore.Observe(scan.Catalogue.Installations, DateTimeOffset.UtcNow, scan.ActivityRoots)
                : scanActivityStore.Load(), lifetime.Token);
            return lastActivityRecords;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        { return lastActivityRecords; }
    }
}
