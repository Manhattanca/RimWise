using System.Globalization;
using System.Text;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Some runtimes ignore Unity's logFile argument. Read their fixed log only when its
/// header identifies this exact test. Never scan the profile or use a previous game's log.</summary>
internal sealed class GameTestLogCapture(string executable, string data, int processId,
    DateTime startedUtc, string? runtimeLog, Func<string, FileStream>? openRuntimeLog = null)
{
    private const int MaximumHeaderBytes = 16 * 1024;

    internal static string RuntimeLogForConfiguration(string config) => Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(config)))!, "Logs", "Player.log");

    internal long CorrelatedLength()
    {
        using var stream = OpenCorrelated();
        return stream?.Length ?? 0;
    }

    internal void PollReset(GameModResetDetection detector)
    {
        using var stream = OpenCorrelated();
        if (stream is not null) detector.Poll(stream);
    }

    internal bool CopyCorrelated(string destination)
    {
        using var source = OpenCorrelated();
        if (source is null) return false;
        source.Position = 0;
        var remaining = source.Length;
        using var target = new FileStream(ScopedPaths.Ordinary(destination), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read);
        var buffer = new byte[8192];
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new IOException("本轮游戏日志在读取期间缩短，不能确认日志完整性。");
            target.Write(buffer, 0, read);
            remaining -= read;
        }
        target.Flush(flushToDisk: true);
        return true;
    }

    private FileStream? OpenCorrelated()
    {
        if (!OperatingSystem.IsWindows() || runtimeLog is null || !File.Exists(runtimeLog)) return null;
        FileStream stream;
        try
        {
            var path = ScopedPaths.Ordinary(runtimeLog, true);
            stream = openRuntimeLog is not null ? openRuntimeLog(path) : new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        // The game's logger can rename/recreate its file between Exists and Open. Absence
        // is no evidence for this poll; retry correlation on the next poll, without stopping it.
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var accepted = false;
        try
        {
            // Check and read the same open file, including after a path rotation. An unflushed
            // header supplies no evidence; it can be retried after the owned game has stopped.
            if (File.GetLastWriteTimeUtc(stream.SafeFileHandle) < startedUtc) return null;
            var buffer = new byte[(int)Math.Min(stream.Length, MaximumHeaderBytes)];
            var used = 0;
            while (used < buffer.Length)
            {
                var read = stream.Read(buffer, used, buffer.Length - used);
                if (read == 0) break;
                used += read;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, used).Replace("\r", "");
            var end = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (end < 0) return null;
            var lines = text[..end].Split('\n');
            if (lines[0] != "=== Waylith log ===") return null;
            var processes = lines.Where(line => line.StartsWith("Process:", StringComparison.Ordinal)).ToArray();
            var commands = lines.Where(line => line.StartsWith("Command:", StringComparison.Ordinal)).ToArray();
            if (processes.Length != 1 || commands.Length != 1
                || processes[0]["Process:".Length..].Trim() != "pid " + processId.ToString(CultureInfo.InvariantCulture)) return null;
            var command = commands[0]["Command:".Length..].Trim();
            if (!SteamTestProcess.Matches(command, executable, data)
                && !SteamTestProcess.Matches(command, Path.ChangeExtension(executable, ".dll"), data)) return null;
            accepted = true;
            return stream;
        }
        finally { if (!accepted) stream.Dispose(); }
    }
}
