namespace RimWise.Integration.Steam;

/// <summary>Read Steam download activity without starting or restarting individual downloads.</summary>
public static class WorkshopDownloadMonitor
{
    public static async Task<IReadOnlyList<WorkshopItemState>> WaitAsync(string[] ids,
        Func<string[], CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> states,
        IProgress<string>? progress, CancellationToken token, TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<bool>? skipWait = null)
    {
        if (ids.Length == 0) return [];
        time ??= TimeProvider.System;
        delay ??= (duration, cancellation) => Task.Delay(duration, cancellation);
        var start = time.GetUtcNow();
        var lastActivity = start;
        var lastBytes = new Dictionary<string, ulong>(StringComparer.Ordinal);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = await states(ids, token);
            token.ThrowIfCancellationRequested();
            var now = time.GetUtcNow();
            var complete = current.Count(item => item.Subscribed && item.Installed && !string.IsNullOrWhiteSpace(item.Directory));
            if (complete == ids.Length)
            {
                progress?.Report($"Steam 下载已完成（{complete}/{ids.Length}），正在准备导入排序…");
                return current;
            }
            if (skipWait?.Invoke() == true) return current;
            var pending = current.Count(item => item.DownloadPending && !item.Downloading);
            var active = current.Any(item => item.Downloading || item.DownloadPending
                || lastBytes.TryGetValue(item.Id, out var bytes) && item.BytesDownloaded > bytes);
            foreach (var item in current) lastBytes[item.Id] = item.BytesDownloaded;
            if (active) lastActivity = now;
            if (current.Count == ids.Length && current.All(item => item.Installed || (!item.Subscribed && !item.Downloading && !item.DownloadPending)))
            {
                progress?.Report("未订阅项已跳过，正在导入排序…");
                return current;
            }
            var downloading = current.Count(item => item.Downloading);
            progress?.Report(now - lastActivity >= TimeSpan.FromSeconds(10)
                ? $"Steam 尚未报告进度（已安装 {complete}/{ids.Length}），可继续等待或跳过等待并导入。"
                : $"请等待创意工坊下载完成（已安装 {complete}/{ids.Length}，下载中 {downloading}，排队 {pending}）…");
            await delay(TimeSpan.FromSeconds(1), token);
        }
    }
}
