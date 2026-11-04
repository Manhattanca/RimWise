using System.Text.Json;
using System.Threading.Channels;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Diagnostics;

/// <summary>Bounded, asynchronous per-process log. Never records input fields or HTTP bodies.</summary>
public sealed class DiagnosticEventLog : IDisposable
{
    public static DiagnosticEventLog? Current { get; set; }
    private sealed record Entry(string? Json, TaskCompletionSource? Completion = null);
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(2048)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task writer;
    private long dropped;
    public string? Problem { get; private set; }
    public string FilePath { get; }

    public DiagnosticEventLog(string directory)
    {
        var root = ScopedPaths.Child(directory, "Data/Diagnostics");
        Directory.CreateDirectory(root);
        // Bound retained UI telemetry without touching any other application data.
        foreach (var old in Directory.EnumerateFiles(root, "ui-*.jsonl").OrderByDescending(Path.GetFileName).Skip(39))
        {
            try { File.Delete(ScopedPaths.Ordinary(old)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        FilePath = ScopedPaths.Child(root, $"ui-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        writer = Task.Run(WriteAsync);
    }

    public void Record(string area, string operation, string? detail = null)
    {
        try
        {
            var redactor = new DiagnosticRedactor();
            var safeDetail = detail is null ? null : redactor.Text(detail);
            var json = redactor.Json(JsonSerializer.Serialize(new
                { timestampUtc = DateTimeOffset.UtcNow, processId = Environment.ProcessId, area, operation,
                    detail = safeDetail is { Length: > 32768 } ? safeDetail[..32768] + " [truncated]" : safeDetail }));
            // Single-line framing remains valid even if a message contains newlines.
            using var parsed = JsonDocument.Parse(json);
            if (!queue.Writer.TryWrite(new(JsonSerializer.Serialize(parsed.RootElement)))) Interlocked.Increment(ref dropped);
        }
        catch { Interlocked.Increment(ref dropped); } // Diagnostics must not break the operation being diagnosed.
    }

    private async Task WriteAsync()
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var output = new StreamWriter(stream) { AutoFlush = true };
            await foreach (var entry in queue.Reader.ReadAllAsync())
            {
                if (entry.Completion is not null) { await output.FlushAsync(); entry.Completion.TrySetResult(); continue; }
                if (stream.Length >= 2 * 1024 * 1024) { Interlocked.Increment(ref dropped); continue; }
                await output.WriteLineAsync(entry.Json);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, droppedEvents = dropped }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Problem = ex.GetType().Name; }
        finally { queue.Writer.TryComplete(); while (queue.Reader.TryRead(out var pending)) pending.Completion?.TrySetResult(); }
    }

    public async Task FlushAsync(CancellationToken token = default)
    {
        if (writer.IsCompleted) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await queue.Writer.WriteAsync(new(null, completion), token);
            await Task.WhenAny(completion.Task, writer).WaitAsync(token);
        }
        catch (ChannelClosedException) { }
    }

    public long DroppedEvents => Interlocked.Read(ref dropped);
    public void Dispose()
    {
        queue.Writer.TryComplete();
        writer.Wait(TimeSpan.FromSeconds(2));
    }
}
