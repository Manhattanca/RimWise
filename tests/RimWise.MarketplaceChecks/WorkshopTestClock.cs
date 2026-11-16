internal sealed class WorkshopTestClock : TimeProvider
{
    internal TimeSpan Elapsed { get; private set; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;
    internal Task Delay(TimeSpan duration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Elapsed += duration;
        return Task.CompletedTask;
    }
}

internal sealed class ImmediateProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
