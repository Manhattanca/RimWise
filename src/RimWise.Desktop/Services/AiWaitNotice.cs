using System.Diagnostics;
using System.Windows.Threading;

namespace RimWise.Desktop.Services;

/// <summary>A UI reminder only. It never cancels a request or claims the provider is computing.</summary>
internal sealed class AiWaitNotice : IDisposable
{
    internal static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer timer;

    internal AiWaitNotice(TimeSpan delay, Action<TimeSpan, bool> update)
    {
        var watch = Stopwatch.StartNew();
        timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => update(watch.Elapsed, watch.Elapsed >= delay);
        timer.Start();
    }

    public void Dispose() => timer.Stop();
}
