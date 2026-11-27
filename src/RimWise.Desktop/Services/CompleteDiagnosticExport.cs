using System.IO;
using RimWise.Integration.Diagnostics;

namespace RimWise.Desktop.Services;

/// <summary>Both UI entries and automatic saving export the same retained diagnostic history.</summary>
internal static class CompleteDiagnosticExport
{
    internal static async Task<DiagnosticExportResult> ExportAsync(string profile, object snapshot,
        string? destination = null)
    {
        if (DiagnosticEventLog.Current is { } log) await log.FlushAsync();
        return await Task.Run(() =>
        {
            var store = new AutomaticSortingLogStore(profile);
            using var operation = store.AcquireExportLease();
            // Resolve the automatic destination under the migration/cleanup lease.
            var target = destination ?? Path.Combine(store.ValidateDirectory(store.LoadSettings().Directory),
                $"RimWise-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            // Never pass a run filter: failures before a run was indexed and older sessions matter too.
            return DiagnosticExportService.Export(profile, target, snapshot);
        });
    }
}
