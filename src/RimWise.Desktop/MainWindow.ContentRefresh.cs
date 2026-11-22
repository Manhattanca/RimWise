using RimWise.Core;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? contentCancellation;
    private Task? contentTask;
    private readonly List<Task> contentTasks = [];

    private ModScanResult WithCachedContent(ModScanResult metadata, bool incompleteOnly = false)
    {
        if (ViewModel.CurrentCatalogue is not { } previous) return metadata;
        var cached = previous.Installations.ToDictionary(mod => mod.Directory, StringComparer.OrdinalIgnoreCase);
        // Only presentation fields survive a metadata refresh. Never reuse old requirements,
        // diagnostics, versions or installed membership as fresh scan evidence.
        return metadata with { Catalogue = new(metadata.Catalogue.Installations.Select(mod =>
            (!incompleteOnly || mod.CodeKind == ModCodeKind.Unknown)
                && cached.TryGetValue(mod.Directory, out var old) && mod.Metadata is not null && old.Metadata is not null
                && mod.Source == old.Source && mod.WorkshopId == old.WorkshopId
                && string.Equals(mod.FolderName, old.FolderName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mod.PackageId, old.PackageId, StringComparison.OrdinalIgnoreCase)
                ? mod with { CodeKind = old.CodeKind, ContentKinds = old.ContentKinds | mod.ContentKinds,
                    LastUpdatedUtc = mod.LastUpdatedUtc ?? old.LastUpdatedUtc }
                : mod).ToArray()) };
    }

    private void CancelContentEnrichment()
    {
        var previous = contentCancellation;
        contentCancellation = null; // Invalidate before signalling a possibly synchronous continuation.
        previous?.Cancel();
        ViewModel.IsEnrichingContent = false;
    }

    private void BeginContentEnrichment(int generation, GamePaths selectedPaths,
        ModsConfigDocument? configuration, ModScanResult metadata)
    {
        CancelContentEnrichment();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        contentCancellation = cancellation;
        ViewModel.IsEnrichingContent = true;
        contentTasks.RemoveAll(task => task.IsCompleted);
        contentTask = EnrichContentAsync();
        contentTasks.Add(contentTask);

        bool Current() => ReferenceEquals(contentCancellation, cancellation) && !cancellation.IsCancellationRequested
            && !lifetime.IsCancellationRequested && generation == scanGeneration && paths == selectedPaths
            && ReferenceEquals(ViewModel.CurrentCatalogue, metadata.Catalogue);

        async Task EnrichContentAsync()
        {
            try
            {
                // Required refresh/startup has finished before optional disk traversal begins.
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (!Current()) return;
                // Inspection is read-only. Even a stalled filesystem/inspector must not hold up
                // AI, import, a newer refresh or update handoff. Late results are never published.
                var inspection = InspectStartupContentAsync(metadata, cancellation.Token);
                _ = inspection.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var result = await inspection.WaitAsync(cancellation.Token);
                if (!Current()) return;
                result = WithCachedContent(result, incompleteOnly: true);
                var records = await ReadActivityAsync(result, observe: true, cancellation.Token);
                if (!Current()) return;
                ViewModel.ApplyContentScan(result, records);
                // Do not replace the user's status, focus, selection, order or undo history.
                if (configuration is not null)
                    await SaveStartupSnapshotAsync(selectedPaths, configuration, result, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Optional failures are diagnostic only; the verified metadata/list stays usable.
                DiagnosticEventLog.Current?.Record("scan", "content-refresh-failed", ex.GetType().Name);
            }
            finally
            {
                if (ReferenceEquals(contentCancellation, cancellation))
                {
                    contentCancellation = null;
                    ViewModel.IsEnrichingContent = false;
                }
                cancellation.Dispose();
            }
        }
    }
}
