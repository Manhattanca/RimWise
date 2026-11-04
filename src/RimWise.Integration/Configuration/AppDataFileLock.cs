using System.Diagnostics;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Configuration;

/// <summary>Short cross-process lock for one data file, never held across network or UI work.</summary>
internal static class AppDataFileLock
{
    internal static FileStream Acquire(string path)
    {
        path = ScopedPaths.Ordinary(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(20); }
        }
    }
}
