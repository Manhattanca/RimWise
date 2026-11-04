using System.Diagnostics;

namespace RimWise.Integration.Safety;

/// <summary>Reports work every two seconds; a quiet console must not look like a frozen launch.</summary>
internal sealed class GameTestProgress(string phase, int total, Action<string>? report)
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private long lastReport = -2000;

    internal void Update(int done)
    {
        if (done != total && elapsed.ElapsedMilliseconds - lastReport < 2000) return;
        var position = total > 0 ? $"{done}/{total}" : $"已核对 {done} 个对象";
        report?.Invoke($"{phase} {position}，已用 {elapsed.Elapsed.TotalSeconds:F1} 秒");
        lastReport = elapsed.ElapsedMilliseconds;
    }
}
