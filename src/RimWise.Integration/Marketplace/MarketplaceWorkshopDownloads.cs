using RimWise.Integration.Steam;

namespace RimWise.Integration.Marketplace;

/// <summary>Automatically enqueue only unsubscribed collection members, then observe their Steam state as a batch.</summary>
public static class MarketplaceWorkshopDownloads
{
    public static Task<IReadOnlyList<WorkshopItemState>> DownloadAsync(MarketplaceManifest manifest,
        string applicationDirectory, IProgress<string>? progress, CancellationToken token,
        Func<int, CancellationToken, Task<bool>>? confirmMissing = null, CancellationToken skipWait = default, Action<bool>? waiting = null)
    {
        var client = new SteamWorkshopClient(applicationDirectory);
        return DownloadAsync(manifest, client.StatesAsync, client.CollectionItemsAsync, client.EnqueueMissingAsync,
            confirmMissing ?? ((_, _) => Task.FromResult(false)), progress, token, watch: async ids =>
            {
                if (ids.Length == 0) return [];
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, skipWait);
                waiting?.Invoke(true);
                try { return await client.WatchAsync(ids, progress, linked.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && skipWait.IsCancellationRequested)
                { return await client.StatesAsync(ids, token); }
                finally { waiting?.Invoke(false); }
            });
    }

    // Injectable transport and clock keep tests away from the player's Steam account.
    public static async Task<IReadOnlyList<WorkshopItemState>> DownloadAsync(MarketplaceManifest manifest,
        Func<string[], CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> states,
        Func<string, IProgress<string>?, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> collection,
        Func<string[], IProgress<string>?, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> enqueue,
        Func<int, CancellationToken, Task<bool>> confirmMissing,
        IProgress<string>? progress, CancellationToken token, TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<bool>? skipWait = null,
        Func<string[], Task<IReadOnlyList<WorkshopItemState>>>? watch = null)
    {
        MarketplaceJson.ValidateManifest(manifest);
        token.ThrowIfCancellationRequested();
        var expected = manifest.Mods.Where(mod => mod.WorkshopId is not null)
            .Select(mod => mod.WorkshopId!).Distinct(StringComparer.Ordinal).ToArray();
        var watched = expected;
        if (manifest.WorkshopCollectionId is { Length: > 0 } id)
        {
            progress?.Report("正在读取绑定的 Steam 创意工坊合集…");
            IReadOnlyList<WorkshopItemState> children;
            try
            {
                children = await collection(id, progress, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsDownloadFailure(ex))
            {
                progress?.Report("Steam 未能读取绑定合集下载状态；将跳过等待并继续导入，缺失模组会在完成后提示。");
                return [];
            }
            token.ThrowIfCancellationRequested();
            watched = expected.Concat(children.Select(item => item.Id)).Distinct(StringComparer.Ordinal).ToArray();
            var missing = children.Where(item => !item.Subscribed).Select(item => item.Id).ToArray();
            if (missing.Length > 0)
            {
                progress?.Report($"正在通过 Steam 自动订阅合集内 {missing.Length} 个未订阅模组；已有订阅项将跳过…");
                try { await enqueue(missing, progress, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsDownloadFailure(ex))
                {
                    progress?.Report("Steam 未能补全合集订阅；将继续等待可用下载并导入排序，缺失模组会在完成后提示。");
                }
            }
        }
        else if (expected.Length > 0)
        {
            IReadOnlyList<WorkshopItemState> current;
            try { current = await states(expected, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsDownloadFailure(ex))
            {
                progress?.Report("无法读取 Steam 订阅状态；将跳过下载并继续导入，缺失模组会在完成后提示。");
                return [];
            }
            token.ThrowIfCancellationRequested();
            var subscribed = current.Where(item => item.Subscribed).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var missing = expected.Where(id => !subscribed.Contains(id)).ToArray();
            if (missing.Length > 0)
            {
                var approved = await confirmMissing(missing.Length, token);
                token.ThrowIfCancellationRequested();
                if (!approved)
                {
                    progress?.Report("该排序未绑定 Steam 合集，已跳过补全订阅，继续按原顺序导入；缺失模组仅提示。");
                    return current;
                }
                progress?.Report($"正在补全 {missing.Length} 个未订阅模组，已订阅项自动跳过…");
                try { await enqueue(missing, progress, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsDownloadFailure(ex))
                {
                    progress?.Report("Steam 未能开始补全订阅；将跳过下载并继续导入，缺失模组会在完成后提示。");
                    return current;
                }
            }
        }
        IReadOnlyList<WorkshopItemState> result;
        try { result = watch is null ? await WorkshopDownloadMonitor.WaitAsync(watched, states, progress, token, time, delay, skipWait) : await watch(watched); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsDownloadFailure(ex))
        {
            progress?.Report("Steam 下载状态无法确认；将跳过下载并继续导入，缺失模组会在完成后提示。");
            return [];
        }
        var allowed = watched.ToHashSet(StringComparer.Ordinal);
        return result.Where(item => allowed.Contains(item.Id)).ToArray();
    }

    private static bool IsDownloadFailure(Exception ex) => ex is IOException or InvalidDataException
        or UnauthorizedAccessException or InvalidOperationException or System.Net.Http.HttpRequestException or TimeoutException;

}
