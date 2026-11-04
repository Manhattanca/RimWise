using System.IO;
using System.Xml;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private async Task RestartRefreshAsync(
        Func<string, CancellationToken, Task<ModsConfigDocument>>? reader = null,
        Func<GamePaths, CancellationToken, IProgress<ScanProgress>, Task<ModScanResult>>? scanner = null, bool deferContent = false)
    {
        var generation = ++scanGeneration;
        scanCancellation?.Cancel();
        if (scanTask is not null) await scanTask;
        if (generation != scanGeneration || lifetime.IsCancellationRequested) return;
        scanTask = PerformRefreshAsync(generation, paths, reader, scanner, deferContent);
        await scanTask;
    }

    private async Task PerformRefreshAsync(int generation, GamePaths selectedPaths,
        Func<string, CancellationToken, Task<ModsConfigDocument>>? reader,
        Func<GamePaths, CancellationToken, IProgress<ScanProgress>, Task<ModScanResult>>? scanner, bool deferContent)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        scanCancellation = cancellation;
        var revision = ViewModel.ContentRevision;
        ViewModel.IsScanning = true;
        ViewModel.Status = "正在刷新模组和当前排序…";
        var progress = new Progress<ScanProgress>(value =>
        {
            if (generation == scanGeneration && ViewModel.IsScanning && !cancellation.IsCancellationRequested)
                ViewModel.Status = (ViewModel.IsShowingCache ? "缓存数据 · " : "") + $"正在刷新，已检查 {value.Processed} 项…";
        });
        try
        {
            var file = CurrentOrderLocation.Resolve(selectedPaths);
            ModsConfigDocument? loaded = null;
            string? notice = null;
            try
            {
                loaded = await (reader?.Invoke(file, cancellation.Token)
                    ?? Task.Run(() => files.Read(file), cancellation.Token));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or ArgumentException)
            {
                notice = "未找到当前排序或无法读取，保留当前清单；请检查配置目录或导入文件";
            }
            cancellation.Token.ThrowIfCancellationRequested();
            ModScanResult? result = null;
            if (selectedPaths.GameDirectory.Length > 0 || selectedPaths.LocalModsDirectory.Length > 0 || selectedPaths.WorkshopDirectory.Length > 0)
                result = await (scanner?.Invoke(selectedPaths, cancellation.Token, progress)
                    ?? Task.Run(() => new ModScanner().Scan(selectedPaths, cancellation.Token, progress, inspectContent: !deferContent), cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (result?.Diagnostics.Any(issue => issue.Code is "ScanLimit" or "ScanRootUnreadable") == true)
                throw new IOException("模组目录未完整读取，请检查路径设置后重试。");
            var records = result is null ? null : await ReadActivityAsync(result, observe: true);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != scanGeneration || lifetime.IsCancellationRequested) return;
            if (revision != ViewModel.ContentRevision)
            {
                // Refresh cached metadata without replacing an order edited during startup.
                // The persisted snapshot still uses the actual game order, never those edits.
                if (ViewModel.IsShowingCache && loaded is not null && result is not null)
                {
                    ViewModel.ApplyScan(result, records);
                    ViewModel.IsShowingCache = false;
                    await SaveStartupSnapshotAsync(selectedPaths, loaded, result);
                }
                ViewModel.Status = "刷新期间清单已修改，已保留当前编辑；可再次刷新";
                return;
            }
            // Commit the complete pair together. Cancellation never resets editor history or one pane alone.
            // A failed refresh must not make a cached pair look authoritative.
            if (ViewModel.IsShowingCache && (loaded is null || result is null))
            {
                ViewModel.Status = notice ?? "刷新未完成，仍显示缓存";
                return;
            }
            if (loaded is not null) ViewModel.ReloadCurrentOrder(loaded, file);
            if (result is not null) ViewModel.ApplyScan(result, records);
            ViewModel.IsShowingCache = false;
            ViewModel.Status = notice ?? $"已刷新排序 {ViewModel.Rows.Count} 项，未启用 {ViewModel.AvailableMods.Count} 个模组";
            if (ViewModel.SelectedRow is null && ViewModel.SelectedAvailable is null)
                ViewModel.SelectedRow = ViewModel.Rows.FirstOrDefault();
            if (loaded is not null && result is not null)
                await SaveStartupSnapshotAsync(selectedPaths, loaded, result);
            if (deferContent && result is not null)
            {
                ViewModel.IsEnrichingContent = true;
                ViewModel.Status = "列表已载入，正在补充模组类型与更新时间…";
                // Yield a frame before doing content I/O, so a cold start does not wait for every texture and DLL.
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var metadata = result;
                result = await InspectStartupContentAsync(metadata, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                records = await ReadActivityAsync(result, observe: true);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != scanGeneration || lifetime.IsCancellationRequested) return;
                ViewModel.ApplyContentScan(result, records);
                ViewModel.Status = $"已载入 {result.Catalogue.Installations.Count} 个模组";
                if (loaded is not null) await SaveStartupSnapshotAsync(selectedPaths, loaded, result);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == scanGeneration && !lifetime.IsCancellationRequested)
                ViewModel.Status = deferContent && ViewModel.HasCatalogue ? "已取消详细读取，保留当前列表；可刷新补全" : "已取消刷新，保留上次结果";
        }
        catch (Exception ex)
        {
            if (generation == scanGeneration && !lifetime.IsCancellationRequested)
                ViewModel.Status = (deferContent && ViewModel.HasCatalogue ? "详细读取未完成，保留当前列表：" : "刷新未完成，保留上次结果：") + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(scanCancellation, cancellation))
            {
                ViewModel.IsScanning = false;
                ViewModel.IsEnrichingContent = false;
                scanCancellation = null;
            }
        }
    }

    internal async Task PrepareAutomaticCatalogueAsync()
    {
        if (ViewModel.IsEnrichingContent)
        {
            scanCancellation?.Cancel();
            if (scanTask is not null) await scanTask;
        }
        if (pathInitialization is not null) await pathInitialization;
        lifetime.Token.ThrowIfCancellationRequested();
        var selectedPaths = paths;
        var installed = await Task.Run(() => GameVersionDetector.Detect(selectedPaths.GameDirectory).Version, lifetime.Token);
        if (installed is not null && ViewModel.CatalogueVersion?.Series != installed.Series)
        {
            var revision = ViewModel.ContentRevision;
            var refreshed = await Task.Run(() => new ModScanner().Scan(selectedPaths, lifetime.Token,
                targetOverride: installed, inspectContent: false), lifetime.Token);
            if (paths != selectedPaths || revision != ViewModel.ContentRevision)
                throw new IOException("准备期间排序或路径发生变化，请重新开始自动排序。");
            ViewModel.ApplyScan(refreshed);
        }
    }

    private Task<ModScanResult> InspectStartupContentAsync(ModScanResult metadata, CancellationToken cancellation)
    {
#if DEBUG
        if (contentInspectorForCheck is not null) return contentInspectorForCheck(metadata, cancellation);
#endif
        return Task.Run(() => new ModScanner().InspectContent(metadata, cancellation), cancellation);
    }

#if DEBUG
    private Func<ModScanResult, CancellationToken, Task<ModScanResult>>? contentInspectorForCheck;
    internal async Task FastStartupForCheckAsync(GamePaths selectedPaths,
        Func<GamePaths, CancellationToken, IProgress<ScanProgress>, Task<ModScanResult>> metadataScanner,
        Func<ModScanResult, CancellationToken, Task<ModScanResult>> inspector, bool loadCache = false)
    {
        paths = selectedPaths;
        contentInspectorForCheck = inspector;
        if (loadCache) await LoadStartupSnapshotAsync();
        await RestartRefreshAsync(scanner: metadataScanner, deferContent: true);
    }
#endif
}
