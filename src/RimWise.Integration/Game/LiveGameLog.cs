using System.Text;
using System.Text.RegularExpressions;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

public enum LiveLogLevel { Information, Warning, Error, Exception }
public sealed record LiveLogLine(long Number, LiveLogLevel Level, string Text)
{
    public string LevelLabel => Level switch { LiveLogLevel.Warning => "警告", LiveLogLevel.Error => "错误", LiveLogLevel.Exception => "异常", _ => "信息" };
}
public sealed record LiveLogBatch(bool Reset, IReadOnlyList<LiveLogLine> Lines, long BytesRead,
    long FileBytes, DateTime LastWriteUtc, long TotalLines, long[] Counts, bool HasMore);

/// <summary>Incremental read-only tail. Reopens shared handles so game rotation is never blocked.
/// UI batches are bounded; the original file is never modified or truncated.</summary>
public sealed partial class LiveGameLog(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    private long offset, number;
    private DateTime created, written;
    private byte[] anchor = [];
    private Decoder decoder = Encoding.UTF8.GetDecoder();
    private string pending = "";
    private readonly long[] counts = new long[4];
    private LiveLogLevel previous;
    private bool structured;
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?\s+(?<level>TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\s+")]
    private static partial Regex Stamp();
    [GeneratedRegex(@"\b(?:exception|error)\b|Could not resolve|Failed to |Patch operation .* failed", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorText();
    [GeneratedRegex(@"(?:\b[A-Za-z_.]+Exception\s*:|^Unhandled exception)", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionText();

    public LiveLogBatch Read(CancellationToken token = default)
    {
        using var file = new FileStream(ScopedPaths.Ordinary(Path, true), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var creation = File.GetCreationTimeUtc(file.SafeFileHandle);
        var write = File.GetLastWriteTimeUtc(file.SafeFileHandle);
        var length = file.Length;
        var reset = offset == 0 || length < offset || creation != created || length == offset && write != written;
        if (!reset && anchor.Length > 0)
        {
            file.Position = offset - anchor.Length;
            var check = new byte[anchor.Length];
            reset = file.Read(check) != check.Length || !check.AsSpan().SequenceEqual(anchor);
        }
        if (reset)
        {
            offset = number = 0; pending = ""; Array.Clear(counts); anchor = [];
            previous = LiveLogLevel.Information; structured = false; decoder = Encoding.UTF8.GetDecoder();
        }
        created = creation; written = write;
        token.ThrowIfCancellationRequested();
        file.Position = offset;
        var bytes = new byte[(int)Math.Min(256 * 1024, Math.Max(0, length - offset))];
        var read = file.Read(bytes);
        var skip = 0;
        if (offset == 0)
        {
            // Wait for a partial BOM rather than introducing replacement characters.
            if (read < 4 && read > 0 && bytes[0] is 0xef or 0xff or 0xfe)
                return new(reset, [], 0, length, write, 0, counts.ToArray(), false);
            if (read >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) skip = 3;
            else if (read >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { decoder = Encoding.Unicode.GetDecoder(); skip = 2; }
            else if (read >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { decoder = Encoding.BigEndianUnicode.GetDecoder(); skip = 2; }
        }
        var chars = new char[read + 2];
        var used = decoder.GetChars(bytes, skip, read - skip, chars, 0, flush: false);
        var text = pending + new string(chars, 0, used);
        var lines = new List<LiveLogLine>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            Add(text[start..i].TrimEnd('\r')); start = i + 1;
        }
        pending = text[start..];
        // A malformed single line must not monopolize memory. Raw bytes remain on disk.
        while (pending.Length > 16384) { Add(pending[..16384]); pending = pending[16384..]; }
        offset += read;
        file.Position = Math.Max(0, offset - 64);
        anchor = new byte[(int)(offset - file.Position)];
        if (file.Read(anchor) != anchor.Length) anchor = [];
        return new(reset, lines, offset, length, write, number, counts.ToArray(), offset < length);

        void Add(string value)
        {
            var stamp = Stamp().Match(value);
            LiveLogLevel level;
            if (stamp.Success)
            {
                structured = true;
                level = stamp.Groups["level"].Value switch
                {
                    "WARN" or "WARNING" => LiveLogLevel.Warning,
                    "ERROR" or "FATAL" => ExceptionText().IsMatch(value) ? LiveLogLevel.Exception : LiveLogLevel.Error,
                    _ => LiveLogLevel.Information
                };
            }
            else if (structured) level = previous;
            else if (ExceptionText().IsMatch(value)) level = LiveLogLevel.Exception;
            else if (value.StartsWith("  ", StringComparison.Ordinal) || value.StartsWith("at ", StringComparison.Ordinal)
                || value.StartsWith("--->", StringComparison.Ordinal) || value.StartsWith("[Ref ", StringComparison.Ordinal)) level = previous;
            else if (value.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("WARN ", StringComparison.Ordinal)) level = LiveLogLevel.Warning;
            else level = ErrorText().IsMatch(value) ? LiveLogLevel.Error : LiveLogLevel.Information;
            previous = level; counts[(int)level]++;
            lines.Add(new(++number, level, value));
        }
    }
}
