using System.IO;
using System.Windows;
using RimWise.Core;
using RimWise.Desktop.Views;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Marketplace;
using RimWise.Integration.Scanning;
using RimWise.Integration.Steam;
using RimWise.Integration.Safety;

namespace RimWise.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? marketplaceApplyCancellation;
    private long marketplaceApplyGeneration;
    private CancellationTokenSource? steamWait;
    private void SkipMarketplaceWait_Click(object sender, RoutedEventArgs e) => steamWait?.Cancel();

    // Subscription/waiting is advisory; missing installations never block the authored import.
    internal async Task<bool> ApplyMarketplaceManifestAsync(MarketplaceManifest manifest,
        IProgress<string>? progress, CancellationToken token,
        Func<OrderComparison, string, CancellationToken, Task<bool>>? approvePreview = null,
        Func<MarketplaceManifest, IProgress<string>?, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>>? download = null,
        Action<MarketplaceDownloadResult>? onSaved = null)
    {
        Dispatcher.VerifyAccess();
        var generation = ++marketplaceApplyGeneration;
        using var skip = new CancellationTokenSource();
        steamWait = skip;
        marketplaceApplyCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        marketplaceApplyCancellation = cancellation;
        var operationToken = cancellation.Token;
        var initialRevision = (ViewModel.DocumentRevision, ViewModel.OrderRevision, ViewModel.ContentRevision);
        var editorChanged = false;
        var publishing = false;
        void Changed()
        {
            if (publishing) return;
            editorChanged = true;
            cancellation.Cancel();
        }
        ViewModel.EditorChanged += Changed;
        bool Current() => generation == marketplaceApplyGeneration && !lifetime.IsCancellationRequested;
        void Report(string message)
        {
            if (!Current()) return;
            // A progress observer cannot turn a completed disk/editor commit into a reported failure.
            try { ViewModel.Status = message; progress?.Report(message); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { }
        }
        try
        {
            operationToken.ThrowIfCancellationRequested();
            if (!ViewModel.HasDocument)
                throw new InvalidOperationException("尚未读取当前游戏配置，请检查游戏配置目录后重试导入。");
            MarketplaceJson.ValidateManifest(manifest);
            // Snapshot lists: even a caller holding mutable manifest collections cannot change an
            // approved order while the preview is open.
            manifest = manifest with { Mods = Array.AsReadOnly(manifest.Mods.ToArray()),
                RequiredDlc = Array.AsReadOnly(manifest.RequiredDlc.ToArray()) };
            var authored = MarketplaceOrderCompatibility.ToOrderFile(manifest);
            if (pathInitialization is not null) await pathInitialization.WaitAsync(operationToken);
            operationToken.ThrowIfCancellationRequested();
            if (initialRevision != (ViewModel.DocumentRevision, ViewModel.OrderRevision, ViewModel.ContentRevision))
                throw new InvalidOperationException("当前排序已变化，请重新预览市场排序。");
            var originalPaths = paths;
            var originalStorage = UserStorage.DirectoryPath;
            EnsureGameClosed();
            var store = CreateGameOrderStore();
            // Capture exact target bytes before preview. WithOrder below keeps these bytes
            // as the precondition rather than accepting intervening external edits.
            // The manifest declares supported versions, not the target config's identifier encoding.
            // Save into the current config format; a differing support declaration is advisory only.
            ModsConfigDocument targetTemplate;
            using (var input = File.OpenRead(store.TargetPath)) targetTemplate = new ModsConfigXmlFormat().Read(input);
            var runtimeVersion = ResolveTargetGameVersion(targetTemplate);
            var baseline = store.Prepare(targetTemplate.WithOrder(authored.Configuration.Order), runtimeVersion);
            void Guard()
            {
                operationToken.ThrowIfCancellationRequested();
                if (!Current() || editorChanged
                    || initialRevision != (ViewModel.DocumentRevision, ViewModel.OrderRevision, ViewModel.ContentRevision)
                    || paths != originalPaths || UserStorage.DirectoryPath != originalStorage)
                    throw new InvalidOperationException("排序、路径或用户数据位置已变化，请重新预览市场排序。");
                if (ResolveTargetGameVersion(targetTemplate) != runtimeVersion)
                    throw new InvalidOperationException("游戏版本已变化，请重新预览市场排序。");
                EnsureGameClosed();
            }
            Task<bool> ConfirmMissing(int count, CancellationToken cancellationToken)
            {
                Guard();
                var dialog = new MessageDialogWindow(this,
                    $"该排序未绑定 Steam 创意工坊合集，发现 {count} 个尚未订阅的模组。是否补全未订阅模组？\n\n是：只订阅尚未订阅的模组，并等待 Steam 下载。\n否：直接导入排序，缺失模组仅提示。\n取消：保留当前排序。",
                    "补全未订阅模组", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                using var closeOnCancel = cancellationToken.Register(() => Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
                dialog.ShowDialog();
                cancellationToken.ThrowIfCancellationRequested();
                if (dialog.Result == MessageBoxResult.Cancel) throw new OperationCanceledException(cancellationToken);
                return Task.FromResult(dialog.Result == MessageBoxResult.Yes);
            }
            Report("正在核对 Steam 订阅及下载状态…");
            IReadOnlyList<WorkshopItemState> completed;
            try
            {
                completed = await (download?.Invoke(manifest, progress, operationToken)
                    ?? MarketplaceWorkshopDownloads.DownloadAsync(manifest, AppContext.BaseDirectory, progress, operationToken, ConfirmMissing, skip.Token,
                        waiting => { if (Current()) MarketplaceSkipWait.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed; }));
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.Net.Http.HttpRequestException or TimeoutException)
            {
                Guard();
                completed = [];
                Report("Steam 下载未能完成，已跳过不可用下载并继续导入；缺失模组会在完成后提示。");
            }
            Guard();
            MarketplaceSkipWait.Visibility = Visibility.Collapsed;
            var expectedIds = manifest.Mods.Where(mod => mod.WorkshopId is not null).Select(mod => mod.WorkshopId!).ToHashSet(StringComparer.Ordinal);
            var collectionBound = !string.IsNullOrWhiteSpace(manifest.WorkshopCollectionId);
            if (!collectionBound && completed.Any(item => !expectedIds.Remove(item.Id)))
                throw new IOException("Steam 返回了重复或无关项目，未读取其他模组目录。");
            completed = completed.Where(item => item.Installed && !string.IsNullOrWhiteSpace(item.Directory)).ToArray();
            if (completed.Count > 0 && download is null)
            {
                // Refresh only downloaded display metadata. No dependency/order/source compatibility checks.
                try
                {
                    var root = ScopedPaths.Ordinary(originalPaths.WorkshopDirectory, true);
                    // Display metadata only: an unexpected library cannot authorize reading other directories.
                    var readable = completed.Where(item => string.Equals(ScopedPaths.Ordinary(Path.Combine(root, item.Id), true),
                        ScopedPaths.Ordinary(item.Directory, true), StringComparison.OrdinalIgnoreCase)).ToArray();
                    var changed = await Task.Run(() => new ModScanner().ReadWorkshopItems(root,
                        readable.Select(item => item.Id), runtimeVersion, operationToken), operationToken);
                    Guard();
                    var records = await Task.Run(() => new ModActivityStore(originalStorage)
                        .Observe(changed, DateTimeOffset.UtcNow), operationToken);
                    Guard();
                    ViewModel.MergeWorkshopInstallations(changed, records);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
                { /* Download completion is authoritative; unreadable mod metadata must not gate import. */ }
            }
            Guard();
            var installedIds = ViewModel.CurrentCatalogue?.Installations
                .Select(mod => mod.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var incomplete = manifest.Mods
                .Where(mod => !GameContentPolicy.KeepWhenClearing(mod.PackageId, runtimeVersion) && !installedIds.Contains(mod.PackageId))
                .Select(mod => mod.PackageId).ToArray();
            Report("正在保存排序…");
            var importNotice = MarketplaceOrderCompatibility.VersionNotice(manifest, runtimeVersion);
            if (approvePreview is not null)
            {
                if (!await approvePreview(new OrderComparison(ViewModel.CreateExportDocument().Order, authored.Configuration.Order), importNotice, operationToken)) return false;
            }
            Guard();
            var finalPlan = baseline.WithOrder(authored.Configuration.Order);
            var replacement = authored.WithConfiguration(finalPlan.After);
            // No await between the last guard, guarded disk commit, and combined editor/history publication.
            publishing = true;
            ViewModel.ApplyMarketplaceOrder(replacement, manifest.Title, () => { Guard(); return finalPlan.Apply(); },
                EnsureGameClosed);
            ViewModel.IsShowingCache = false;
            var title = $"已使用《{manifest.Title}》排序";
            var details = string.Join("\n", new[] { importNotice, incomplete.Length == 0 ? "" : $"缺少 {incomplete.Length} 个模组：\n" + string.Join("\n", incomplete) }.Where(s => s.Length > 0));
            ViewModel.Status = title;
            try { onSaved?.Invoke(new(true, title, details)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { /* A completed import remains successful if its notification cannot be shown. */ }
            return true;
        }
        catch (OperationCanceledException)
        {
            if (Current()) Report(editorChanged ? "当前排序已变化，已停止市场应用；保留当前清单和游戏配置。"
                : "已取消市场排序导入，保留当前清单和游戏配置。");
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Xml.XmlException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException or MarketplaceException)
        {
            if (Current()) Report("市场排序未应用：" + ex.Message);
            return false;
        }
        finally
        {
            ViewModel.EditorChanged -= Changed;
            if (ReferenceEquals(steamWait, skip)) steamWait = null;
            if (Current()) MarketplaceSkipWait.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(marketplaceApplyCancellation, cancellation)) marketplaceApplyCancellation = null;
        }
    }

}
