using System.Text;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Recognizes the game's explicit failed-load recovery, not ordinary errors or a missing helper.</summary>
internal sealed class GameModResetDetection
{
    internal const string Marker = "Caught exception while loading play data but there are active mods other than Core. Resetting mods config and trying again.";
    internal const string Recovered = "Successfully recovered from errors and loaded play data.";
    private long offset;
    private string overlap = "";
    internal bool Detected { get; private set; }
    internal bool RecoveryFinished { get; private set; }

    internal void Poll(string path)
    {
        try
        {
            using var stream = new FileStream(ScopedPaths.Ordinary(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Poll(stream);
        }
        catch (IOException) { } // A logger may rotate or temporarily lock its file; retry next poll.
        catch (UnauthorizedAccessException) { }
    }

    internal void Poll(Stream stream)
    {
        if (stream.Length < offset) { offset = 0; overlap = ""; }
        stream.Position = offset;
        var buffer = new byte[8192];
        // Bound work per UI progress tick, not retained log size. The next poll resumes here.
        var remaining = Math.Min(1024 * 1024, stream.Length - offset);
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            var text = overlap + Encoding.UTF8.GetString(buffer, 0, read);
            Detected |= text.Contains(Marker, StringComparison.Ordinal);
            RecoveryFinished |= Detected && text.Contains(Recovered, StringComparison.Ordinal);
            overlap = text[^Math.Min(text.Length, Marker.Length + Recovered.Length)..];
            offset += read;
            remaining -= read;
        }
    }

    // Collected during the full-file scan, independently of abbreviated display text and error quotas.
    internal sealed class Evidence
    {
        private readonly StringBuilder block = new();
        private bool complete;
        internal void Observe(string line)
        {
            if (complete) return;
            if (block.Length == 0 && !line.Contains(Marker, StringComparison.Ordinal)) return;
            if (block.Length < 32768) block.AppendLine(line[..Math.Min(line.Length, 32768 - block.Length)]);
            if (line.Contains(Recovered, StringComparison.Ordinal)) complete = true;
        }
        internal string? Text => block.Length == 0 ? null : block.ToString();
    }
}
