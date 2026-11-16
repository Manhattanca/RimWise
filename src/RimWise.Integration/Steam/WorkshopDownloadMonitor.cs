namespace RimWise.Integration.Steam;

/// <summary>Read Steam download activity without starting or restarting individual downloads.</summary>
public static class WorkshopDownloadMonitor
{
    public static async Task<IReadOnlyList<WorkshopItemState>> WaitAsync(string[] ids,
        Func<string[], CancellationToken, Task<IReadOnlyList<WorkshopItemState>>> states,
        IProgress<string>? progress, CancellationToken token, TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
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
            var active = current.Any(item => item.Downloading
                || lastBytes.TryGetValue(item.Id, out var bytes) && item.BytesDownloaded > bytes);
            foreach (var item in current) lastBytes[item.Id] = item.BytesDownloaded;
            if (active) lastActivity = now;
            if (now - lastActivity >= TimeSpan.FromSeconds(30))
            {
                progress?.Report("连续 30 秒未检测到 Steam 下载活动，继续导入排序；尚未安装的模组会提示缺失。");
                return current;
            }
            var downloading = current.Count(item => item.Downloading);
            progress?.Report($"请等待创意工坊下载完成（已安装 {complete}/{ids.Length}，正在下载 {downloading} 项）；连续 30 秒无下载活动将继续导入…");
            await delay(TimeSpan.FromSeconds(1), token);
        }
    }
}
