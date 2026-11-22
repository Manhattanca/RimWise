using System.Diagnostics;
using System.Text.Json;
using RimWise.Integration.Diagnostics;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Optional structural evidence. Writing it must never suppress cancellation or owned-process cleanup.</summary>
internal sealed class GameAttemptJournal(string directory)
{
    private readonly Stopwatch watch = Stopwatch.StartNew();

    internal void Record(string phase, string outcome = "running", Exception? error = null)
    {
        try
        {
            // No exception message, source path, environment or local-variable values.
            // Keep aggregate children too: parallel capture/analysis can fail independently.
            var pending = new Queue<Exception>();
            if (error is not null) pending.Enqueue(error);
            var chain = new List<object>();
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            while (pending.Count > 0 && chain.Count < 12)
            {
                var current = pending.Dequeue();
                if (!seen.Add(current)) continue;
                chain.Add(new { type = current.GetType().FullName, hresult = current.HResult,
                    frames = new StackTrace(current, false).GetFrames().Take(40).Select(frame => new
                    {
                        type = frame.GetMethod()?.DeclaringType?.FullName, method = frame.GetMethod()?.Name,
                        assembly = frame.GetMethod()?.Module.Assembly.GetName().Name, offset = frame.GetILOffset()
                    }).ToArray() });
                if (current is AggregateException aggregate)
                    foreach (var inner in aggregate.InnerExceptions.Take(12)) pending.Enqueue(inner);
                else if (current.InnerException is { } inner) pending.Enqueue(inner);
            }
            using var output = new FileStream(ScopedPaths.Child(directory, "attempt-lifecycle.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(output, new { schema = 1, timestampUtc = DateTimeOffset.UtcNow,
                elapsedMilliseconds = watch.ElapsedMilliseconds, phase, outcome, chain });
            output.WriteByte((byte)'\n');
            output.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        { DiagnosticEventLog.Current?.Record("game-test", "attempt-journal-unavailable", ex.GetType().Name); }
    }
}
