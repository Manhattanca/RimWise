using System.IO;
using RimWise.Integration.AI;

namespace RimWise.Desktop.Services;

/// <summary>A tutorial marker is separate from execution preferences and recovery records.</summary>
internal static class SortingGuidePreferences
{
    private static string Marker(string directory, string state) => Path.Combine(directory, "sorting-guide-v2-" + state + ".seen");
    private static string LegacyMarker(string directory) => Path.Combine(directory, "sorting-guide-v1.seen");

    internal static bool ShouldAsk(string directory)
    {
        if (File.Exists(Marker(directory, "completed")) || File.Exists(Marker(directory, "declined"))
            || File.Exists(Marker(directory, "used")) || File.Exists(LegacyMarker(directory))) return false;

        // Existing task records let users who have already run the feature skip this new prompt after upgrading.
        try
        {
            var tasks = new AiSettingsStore(directory).Tasks;
            var kinds = new[] { AiTaskKind.Sorting, AiTaskKind.Maintenance };
            var hasTask = tasks.Inspect().Any(task => task.Checkpoint is { StartedUtc: var started } checkpoint
                && started != default && kinds.Contains(checkpoint.Kind));
            var hasDeletedTask = tasks.InspectDeletedUsage().Any(receipt =>
                receipt.UsageRequests.Any(request => kinds.Contains(request.Kind))
                || receipt.UsagePeriods.Any(period => kinds.Contains(period.Kind)));
            return !hasTask && !hasDeletedTask;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            // A damaged old task ledger should not block entry to automatic sorting.
            return true;
        }
    }

    internal static bool WasCompleted(string directory) => File.Exists(Marker(directory, "completed"));
    internal static void MarkCompleted(string directory) => WriteMarker(directory, "completed");
    internal static void MarkDeclined(string directory) => WriteMarker(directory, "declined");
    internal static void MarkAutoSortUsed(string directory) => WriteMarker(directory, "used");

    private static void WriteMarker(string directory, string state)
    {
        Directory.CreateDirectory(directory);
        try
        {
            using var stream = new FileStream(Marker(directory, state), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.WriteByte(1);
        }
        catch (IOException) when (File.Exists(Marker(directory, state))) { /* Another entry point recorded the same state. */ }
    }
}
