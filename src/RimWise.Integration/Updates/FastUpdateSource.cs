using System.Net.Http;

namespace RimWise.Integration.Updates;

/// <summary>A watchdog independent of response headers and blocked reads. Never retries user cancellation.</summary>
internal static class FastUpdateSource
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    internal const long MinimumBytesPerSecond = 1_000_000;

    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Action<long>, Task<T>> primary,
        Func<CancellationToken, Task<T>>? backup, Action? switching, Action<T>? discard,
        CancellationToken token, long? assetSize = null)
    {
        if (backup is null) return await primary(token, _ => { }).ConfigureAwait(false);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(token);
        long received = 0;
        var first = new TaskCompletionSource<(long Bytes, long Time)>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool PastHalf() => assetSize is long total && Interlocked.Read(ref received) > total / 2;
        var watchdog = assetSize.HasValue
            ? WatchAsync(first.Task, () => Interlocked.Read(ref received), watch.Token)
            : Task.Delay(StartTimeout, watch.Token); // Release metadata has no bandwidth requirement.
        var work = primary(attempt.Token, count =>
        {
            if (Interlocked.Add(ref received, count) == count)
                first.TrySetResult((count, System.Diagnostics.Stopwatch.GetTimestamp()));
        });
        try
        {
            await Task.WhenAny(work, watchdog).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // A small completed file wins even if the timer fires at the same instant.
            if (work.IsCompleted || PastHalf()) return await work.ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
            throw new SlowSourceException();
        }
        catch (Exception ex) when (!token.IsCancellationRequested && !PastHalf() &&
            ex is IOException or HttpRequestException or OperationCanceledException)
        {
            attempt.Cancel();
            // Do not wait another network timeout before starting the mirror. The abandoned
            // attempt owns its unique temporary file and can never commit a completed update.
            _ = DiscardAsync(work, discard);
            switching?.Invoke();
            token.ThrowIfCancellationRequested();
            return await backup(token).ConfigureAwait(false);
        }
        finally
        {
            attempt.Cancel();
            watch.Cancel();
            if (token.IsCancellationRequested) _ = DiscardAsync(work, discard);
        }
    }

    private static async Task WatchAsync(Task<(long Bytes, long Time)> first, Func<long> bytes, CancellationToken token)
    {
        (long Bytes, long Time) start;
        try { start = await first.WaitAsync(StartTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { return; }
        var (previous, previousTime) = start;
        while (true)
        {
            await Task.Delay(Window, token).ConfigureAwait(false);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var current = bytes();
            var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(previousTime, now).TotalSeconds;
            if ((current - previous) / seconds < MinimumBytesPerSecond) return;
            previous = current;
            previousTime = now;
        }
    }

    private static async Task DiscardAsync<T>(Task<T> task, Action<T>? discard)
    {
        try { var value = await task.ConfigureAwait(false); discard?.Invoke(value); }
        catch (Exception) { /* The caller already reports the selected source's result. */ }
    }

    private sealed class SlowSourceException : IOException;
}
