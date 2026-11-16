using RimWise.Integration.Steam;

namespace RimWise.Integration.Marketplace;

/// <summary>Subscribe the bound collection, or offer missing subscriptions, then observe Steam as a batch.</summary>
public static class MarketplaceWorkshopDownloads
{
    public static Task<IReadOnlyList<WorkshopItemState>> DownloadAsync(MarketplaceManifest manifest,
        string applicationDirectory, IProgress<string>? progress, CancellationToken token,
        Func<int, CancellationToken, Task<bool>>? confirmMissing = null)
    {
        var client = new SteamWorkshopClient(applicationDirectory);
        return DownloadAsync(manifest, client.StatesAsync, client.EnqueueCollectionAsync, client.EnqueueMissingAsync,
            confirmMissing ?? ((_, _) => Task.FromResult(false)), progress, token);
    }

    // Injectable transport and clock keep tests away from the player's Steam account.
    public static async Task<IReadOnlyList<WorkshopItemState>> DownloadAsync(MarketplaceManifest manifest,
        Func<string[], CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> states,
        Func<string, IProgress<string>?, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> collection,
        Func<string[], IProgress<string>?, CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> enqueue,
        Func<int, CancellationToken, Task<bool>> confirmMissing,
        IProgress<string>? progress, CancellationToken token, TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        MarketplaceJson.ValidateManifest(manifest);
        token.ThrowIfCancellationRequested();
        var expected = manifest.Mods.Where(mod => mod.WorkshopId is not null)
            .Select(mod => mod.WorkshopId!).Distinct(StringComparer.Ordinal).ToArray();
        var watched = expected;
        if (manifest.WorkshopCollectionId is { Length: > 0 } id)
        {
            progress?.Report("正在订阅绑定的 Steam 创意工坊合集；已订阅模组将自动跳过…");
            var children = await collection(id, progress, token);
            token.ThrowIfCancellationRequested();
            watched = expected.Concat(children.Select(item => item.Id)).Distinct(StringComparer.Ordinal).ToArray();
        }
        else if (expected.Length > 0)
        {
            var current = await states(expected, token);
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
                await enqueue(missing, progress, token);
            }
        }
        var result = await WorkshopDownloadMonitor.WaitAsync(watched, states, progress, token, time, delay);
        return result.Where(item => expected.Contains(item.Id, StringComparer.Ordinal)).ToArray();
    }
}
