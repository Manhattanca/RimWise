using System.Security.Cryptography;
using System.Text;

namespace RimWise.Formats;

/// <summary>Serializes this application's writers across instances; hash checks still guard external writers.</summary>
internal sealed class GameWriteLock : IDisposable
{
    private readonly List<Mutex> held = [];
    internal GameWriteLock(string target)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(target).ToUpperInvariant())));
        try
        {
            // Keep coordinating with old versions during upgrades, as well as RimWise.
            foreach (var prefix in new[] { "Local\\RimSortWorkbench.GameOrder.", "Local\\RimWise.GameOrder." })
            {
                var mutex = new Mutex(false, prefix + key);
                try
                {
                    try { if (!mutex.WaitOne(0)) throw new IOException("另一个窗口正在保存此游戏配置，请稍后重试。"); }
                    catch (AbandonedMutexException) { /* Durable journal recovery handles the old process's bytes. */ }
                    held.Add(mutex);
                }
                catch { mutex.Dispose(); throw; }
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var mutex in held.AsEnumerable().Reverse()) { mutex.ReleaseMutex(); mutex.Dispose(); }
        held.Clear();
    }
}
