using RimWise.Integration.Cache;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;
using RimWise.Formats;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private readonly StartupCacheStore startupCache = new(UserStorage.DirectoryPath);
    private StartupCacheStore ActiveCache => checkCache ?? startupCache;
    private StartupCacheStore? checkCache = null;
    private long? startupCacheDocumentRevision;
    internal bool ShouldReadStartupOrder => !ViewModel.HasDocument
        || ViewModel.IsShowingCache && ViewModel.DocumentRevision == startupCacheDocumentRevision;

    private async Task LoadStartupSnapshotAsync()
    {
        if (ViewModel.HasDocument) return;
        var revision = ViewModel.ContentRevision;
        var selectedPaths = paths;
        var snapshot = await Task.Run(() => ActiveCache.TryLoad(selectedPaths), lifetime.Token);
        var records = snapshot is null ? null : await ReadActivityAsync(snapshot.Scan, observe: false);
        if (snapshot is null || lifetime.IsCancellationRequested || revision != ViewModel.ContentRevision) return;
        ViewModel.Load(snapshot.Configuration, CurrentOrderLocation.Resolve(selectedPaths));
        startupCacheDocumentRevision = ViewModel.DocumentRevision;
        ViewModel.ApplyScan(snapshot.Scan, records, observeActivity: false);
        ViewModel.IsShowingCache = true;
        ViewModel.Status = "已显示缓存，正在刷新…";
        ViewModel.SelectedRow = ViewModel.Rows.FirstOrDefault();
    }

    private Task<bool> SaveStartupSnapshotAsync(GamePaths selectedPaths, ModsConfigDocument configuration, ModScanResult result) =>
        Task.Run(() => ActiveCache.TrySave(selectedPaths, configuration, result));

#if DEBUG
    internal async Task CachedStartupForCheckAsync(GamePaths selectedPaths, StartupCacheStore store,
        Func<string, CancellationToken, Task<ModsConfigDocument>>? reader = null,
        Func<GamePaths, CancellationToken, IProgress<ScanProgress>, Task<ModScanResult>>? scanner = null)
    {
        paths = selectedPaths;
        checkCache = store;
        await LoadStartupSnapshotAsync();
        await RestartRefreshAsync(reader, scanner);
    }
#endif
}
