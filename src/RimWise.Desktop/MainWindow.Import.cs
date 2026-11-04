using System.IO;
using System.Windows;
using RimWise.Core;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Safety;
using RimWise.Integration.Scanning;
using RimWise.Integration.Steam;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? importCancellation;
    private long importGeneration;
    private readonly SemaphoreSlim importDownloads = new(1);

    private static bool IsOrderDrop(IDataObject data, out string? file)
    {
        file = null;
        if (!data.GetDataPresent(DataFormats.FileDrop, false)
            || data.GetData(DataFormats.FileDrop, false) is not string[] { Length: 1 } files) return false;
        if (!new[] { ".xml", ".json", ".rml", ".rws" }.Contains(Path.GetExtension(files[0]), StringComparer.OrdinalIgnoreCase)) return false;
        file = files[0];
        return true;
    }

    private void Window_FileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        ClearDropIndicator();
        e.Effects = IsOrderDrop(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_FileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!IsOrderDrop(e.Data, out var file))
        { ViewModel.Status = "请一次拖入一个排序文件（XML／JSON／RML）或环世界存档（RWS）"; return; }
        e.Effects = DragDropEffects.Copy;
        await ImportOrderAsync(file!);
    }

    internal Task ImportOrderAsync(string file,
        Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>>? download = null,
        Func<CancellationToken, Task<ModScanResult>>? rescan = null)
    {
        var revision = ViewModel.OrderRevision;
        return RunMissingModDownloadsAsync(async token =>
        {
            var loaded = await Task.Run(() => orderFiles.Read(file), token);
            token.ThrowIfCancellationRequested();
            if (revision != ViewModel.OrderRevision) return null;
            ViewModel.ReplaceFileUndoable(loaded, file, "导入排序");
            RememberOrderDirectory(file, exported: false);
            ViewModel.SelectedRow = ViewModel.Rows.FirstOrDefault();
            ViewModel.Status = $"已导入 {ViewModel.Rows.Count} 项，正在核对已安装模组…";
            return loaded;
        }, null, download, rescan);
    }

    internal Task DownloadMissingModsAsync(IEnumerable<string> identifiers,
        Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>>? download = null,
        Func<CancellationToken, Task<ModScanResult>>? rescan = null)
    {
        if (!ViewModel.HasDocument) return Task.CompletedTask;
        var document = ViewModel.CreateOrderFile();
        return RunMissingModDownloadsAsync(_ => Task.FromResult<OrderFileDocument?>(document),
            identifiers.ToHashSet(StringComparer.OrdinalIgnoreCase), download, rescan);
    }

    private async Task RunMissingModDownloadsAsync(Func<CancellationToken, Task<OrderFileDocument?>> read,
        IReadOnlySet<string>? requested,
        Func<ImportDownloadPlan, IProgress<string>, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>>? download,
        Func<CancellationToken, Task<ModScanResult>>? rescan)
    {
        var generation = ++importGeneration;
        importCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        importCancellation = cancellation;
        long? documentRevision = null;
        GamePaths? downloadPaths = null;
        bool Current() => generation == importGeneration && !cancellation.IsCancellationRequested;
        bool SameDocument() => documentRevision is null || documentRevision == ViewModel.DocumentRevision;
        void EditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (!SameDocument()) cancellation.Cancel();
        }
        ViewModel.PropertyChanged += EditorChanged;
        async Task RefreshCatalogueAsync(CancellationToken token)
        {
            if (rescan is not null)
            {
                var result = await rescan(token);
                token.ThrowIfCancellationRequested();
                if (generation == importGeneration && SameDocument()) ViewModel.ApplyScan(result);
            }
            else if (HasScanPaths()) await RestartScanAsync();
        }
        async Task MergeDownloadsAsync(IReadOnlyList<WorkshopItemState> completed, CancellationToken token)
        {
            IReadOnlyList<ModInstallation> changed;
            if (rescan is not null)
            {
                // Synthetic transport provides a catalogue, but commit only its downloaded directories.
                var result = await rescan(token);
                var directories = completed.Select(item => item.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
                changed = result.Catalogue.Installations.Where(mod => directories.Contains(mod.Directory)).ToArray();
            }
            else
            {
                if (downloadPaths is null || paths != downloadPaths || downloadPaths.WorkshopDirectory.Length == 0)
                    throw new InvalidOperationException("模组路径已变化，请刷新后重试下载。");
                var root = ScopedPaths.Ordinary(downloadPaths.WorkshopDirectory, true);
                foreach (var item in completed)
                {
                    if (ImportedOrderDownloads.WorkshopIdFromInput(item.Id) != item.Id
                        || !string.Equals(ScopedPaths.Ordinary(Path.Combine(root, item.Id), true),
                            ScopedPaths.Ordinary(item.Directory, true), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Steam 下载目录与已配置工坊目录不符，未读取其他位置。");
                }
                var targetVersion = ViewModel.CatalogueVersion;
                changed = await Task.Run(() => new ModScanner().ReadWorkshopItems(root,
                    completed.Select(item => item.Id), targetVersion, token), token);
            }
            token.ThrowIfCancellationRequested();
            // A separately requested refresh may still be running. Merge into its latest result.
            if (scanTask is not null) await scanTask;
            if (!Current() || !SameDocument() || downloadPaths != paths) throw new OperationCanceledException(token);
            var records = await Task.Run(() => new ModActivityStore(UserStorage.DirectoryPath)
                .Observe(changed, DateTimeOffset.UtcNow), token);
            if (scanTask is not null) await scanTask;
            token.ThrowIfCancellationRequested();
            if (!Current() || !SameDocument() || downloadPaths != paths) throw new OperationCanceledException(token);
            ViewModel.MergeWorkshopInstallations(changed, records);
        }
        try
        {
            var loaded = await read(cancellation.Token);
            if (loaded is null || !Current()) return;
            documentRevision = ViewModel.DocumentRevision;
            var version = ViewModel.CatalogueVersion ?? GameVersion.Parse(loaded.Configuration.GameVersion);
            var initialMissing = ViewModel.HasCatalogue
                ? ViewModel.Rows.Where(row => (requested is null || requested.Contains(row.PackageId))
                    && ViewModel.CurrentCatalogue!.Resolve(row.PackageId, version).Candidates.Count == 0)
                    .Select(row => row.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            async Task<ImportDownloadPlan> ResolvePlanAsync(IProgress<string> progress, CancellationToken token)
            {
                progress.Report("正在核对已安装模组…");
                if (pathInitialization is not null) await pathInitialization.WaitAsync(token);
                if (scanTask is not null) await scanTask.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                // Reuse the loaded catalogue. A download is not a request to rescan every installed Mod.
                if (rescan is not null || !ViewModel.HasCatalogue) await RefreshCatalogueAsync(token);
                if (!Current() || !SameDocument()) throw new OperationCanceledException(token);
                downloadPaths = paths;
                progress.Report("已发现缺失模组，正在检索工坊来源…");
                // Load the selected database even if the file was dropped before startup completed.
                if (ViewModel.CommunityDatabase is null && download is null) await LoadCommunityDatabaseAsync().WaitAsync(token);
                if (!Current() || !SameDocument()) throw new OperationCanceledException(token);
                version = ViewModel.CatalogueVersion ?? GameVersion.Parse(loaded.Configuration.GameVersion);
                var catalogue = ViewModel.CurrentCatalogue ?? new ModCatalogue([]);
                var database = ViewModel.CommunityDatabase;
                // Source lookup may traverse a large database; never block the dialog's UI thread.
                var fullPlan = await Task.Run(() => ImportedOrderDownloads.Plan(loaded, catalogue, version, database), token);
                token.ThrowIfCancellationRequested();
                if (!Current() || !SameDocument()) throw new OperationCanceledException(token);
                if (downloadPaths != paths) throw new InvalidOperationException("模组路径已变化，请刷新后重试下载。");
                // Exclude entries removed while startup/scanning/source lookup was pending.
                var wanted = ViewModel.Rows.Select(row => row.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (requested is not null) wanted.IntersectWith(requested);
                var issues = fullPlan.Issues.Where(item => wanted.Contains(item.Identifier)).ToArray();
                return fullPlan with
                {
                    Downloads = fullPlan.Downloads.Where(item => wanted.Contains(item.Identifier)).ToArray(),
                    Issues = issues, Unresolved = issues.Select(i => i.Identifier + "：" + i.Reason).ToArray()
                };
            }
            var completedCount = 0;
            var subscribedCount = 0;
            async Task ExecuteDownloadAsync(ImportDownloadPlan selected, IProgress<string> progress, CancellationToken token, bool enqueueOnly)
            {
                token.ThrowIfCancellationRequested();
                if (download is null && !HasScanPaths())
                    throw new InvalidOperationException("请先在路径设置中选择游戏和创意工坊模组目录，再右键选择订阅下载。");
                CancelImportDownloadButton.Visibility = Visibility.Visible;
                await importDownloads.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!Current() || !SameDocument()) throw new OperationCanceledException(token);
                    var current = ViewModel.Rows.Select(row => row.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (selected.Downloads.Any(d => !current.Contains(d.Identifier)))
                        throw new InvalidOperationException("该模组已从当前排序移除，未发出订阅。");
                    if (enqueueOnly)
                    {
                        await ImportedOrderDownloads.EnqueueAsync(selected, AppContext.BaseDirectory, progress, token);
                        subscribedCount += selected.Downloads.Count;
                        return;
                    }
                    var completed = await (download?.Invoke(selected, progress, token)
                        ?? ImportedOrderDownloads.WaitAsync(selected, AppContext.BaseDirectory, progress, token));
                    token.ThrowIfCancellationRequested();
                    await MergeDownloadsAsync(completed, token);
                    if (!Current() || !SameDocument()) throw new OperationCanceledException(token);
                    ImportedOrderDownloads.VerifyInstallations(ViewModel.CurrentCatalogue ?? new ModCatalogue([]), selected, version, completed);
                    completedCount += completed.Count;
                }
                finally { importDownloads.Release(); }
            }
            Task SubscribeAsync(ImportDownloadPlan selected, IProgress<string> progress, CancellationToken token)
                => ExecuteDownloadAsync(selected, progress, token, enqueueOnly: false);
            Task EnqueueAsync(ImportDownloadPlan selected, IProgress<string> progress, CancellationToken token)
                => ExecuteDownloadAsync(selected, progress, token, enqueueOnly: true);
            string? resolutionFailure = null;
            if (download is not null)
            {
                // Synthetic transport checks exercise the same executor without modal UI or Steam.
                var progress = new Progress<string>(message => { if (Current()) ViewModel.Status = message; });
                var plan = await ResolvePlanAsync(progress, cancellation.Token);
                if (plan.Downloads.Count > 0) await SubscribeAsync(plan, progress, cancellation.Token);
            }
            else if (initialMissing.Length > 0 || !ViewModel.HasCatalogue || scanTask is { IsCompleted: false }
                || pathInitialization is { IsCompleted: false })
            {
                var window = new MissingModsWindow(initialMissing, ResolvePlanAsync, SubscribeAsync, cancellation.Token, EnqueueAsync) { Owner = this };
                window.ShowDialog();
                await window.PendingResolution;
                await window.PendingDownloads;
                resolutionFailure = window.ResolutionFailure;
            }
            if (!Current() || !SameDocument()) return;
            ViewModel.Status = resolutionFailure is not null ? "缺失模组检索未完成，保留当前排序：" + resolutionFailure
                : completedCount > 0 ? $"已通过 Steam 下载 {completedCount} 项并更新新增模组，保留当前排序"
                : subscribedCount > 0 ? $"Steam 已确认订阅 {subscribedCount} 项，尚未核对全部安装；保留当前排序，可再次查询下载"
                : requested is null ? "已导入排序，保留原有顺序；缺失项可右键订阅下载" : "已保留当前排序；缺失项可再次右键订阅下载";
        }
        catch (OperationCanceledException)
        { if (generation == importGeneration && !lifetime.IsCancellationRequested) ViewModel.Status = "已停止等待下载，保留当前排序；Steam 已接收的下载可能仍会继续"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Xml.XmlException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException)
        {
            if (!Current() || !SameDocument()) return;
            ViewModel.Status = "导入或下载未完成，保留当前清单：" + ex.Message;
            MessageDialog.Show(this, ViewModel.Status, "导入／下载提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ViewModel.PropertyChanged -= EditorChanged;
            if (ReferenceEquals(importCancellation, cancellation))
            { importCancellation = null; CancelImportDownloadButton.Visibility = Visibility.Collapsed; }
        }
    }

    private void CancelImportDownload_Click(object sender, RoutedEventArgs e) => importCancellation?.Cancel();
}
