namespace RimWise.Integration.AI;

/// <summary>Only a local decision and checkpoint selection. Never invokes an AI client.</summary>
public static class AiStartupRecovery
{
    public static async Task<IReadOnlyList<string>> AskAsync(AiTaskRecoveryStore store,
        Func<AiRecoverableTask, Task<bool>> continueTask, CancellationToken token = default)
    {
        var tasks = await Task.Run(store.Inspect, token).ConfigureAwait(false);
        var accepted = new List<string>();
        foreach (var task in tasks.Where(t => !t.Active && (t.Problem is not null
            || t.Checkpoint is { Finished: false })))
        {
            token.ThrowIfCancellationRequested();
            if (await continueTask(task).ConfigureAwait(false)) accepted.Add(task.Id);
            else await Task.Run(() => store.Discard(task.Id), token).ConfigureAwait(false);
        }
        return accepted;
    }
}
