namespace RimWise.Desktop.Presentation;

public sealed partial class MainViewModel
{
    // Call on the UI thread before handing the immutable projection to the exporter.
    // This is current UI state, deliberately not labelled as historical task input.
    internal object CaptureDiagnosticSnapshot(string? targetVersionOverride = null) => new
    {
        capturedUtc = DateTimeOffset.UtcNow, scope = "current-ui-at-export",
        Status, HasChanges, IsScanning, IsShowingCache, IsEnrichingContent,
        GameVersionLabel, configurationVersion = VersionLabel,
        effectiveVersion = EffectiveGameVersion?.ToString(), targetVersionOverride,
        installedVersion = CurrentDetectedVersion?.ToString(), WarningCount, ErrorCount,
        diagnostics = Diagnostics.Select(row => row.Diagnostic).ToArray(),
        scanIssues = ScanIssues.Select(row => row.Diagnostic).ToArray(),
        order = Rows.Select(row => new { row.PackageId, row.Name }).ToArray(),
        installations = CurrentCatalogue?.Installations.Select(mod => new
        {
            mod.PackageId, mod.Name, mod.Directory, mod.Source, mod.WorkshopId,
            metadataReadable = mod.Metadata is not null, mod.Metadata?.ReleaseVersion,
            mod.Metadata?.SupportedVersions, mod.Metadata?.Requirements, mod.RecoveredIdentity, mod.Diagnostics
        }).ToArray()
    };
}
