namespace RimWise.Integration.AI;

/// <summary>Read-only connection check and the separate, metered model access check.</summary>
public static class AiSortingPreflight
{
    internal const int ProbeOutputLimit = 256;
    private const int ReservedTokens = ProbeOutputLimit + 128;

    public static async Task<(AiConnectionResult Result, int BudgetCharge)> CheckModelAsync(IAiClient client,
        AiSettings settings, string? key, AiTaskRecoveryStore tasks, MaintenanceLimits limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { settings = settings.Normalize(); }
        catch (ArgumentException) { return (Failed("AI 配置无效，请检查服务地址和模型名称。"), 0); }
        if (!settings.Enabled) return (Failed("AI 未启用。"), 0);
        if (AiClient.ConfigurationProblem(settings) is { } problem) return (Failed(problem), 0);
        if (limits.WouldExceedTokenLimit(ReservedTokens + 100))
            return (Failed("token 预算不足，无法预留模型校验用量及后续排序预算。"), 0);

        using var requestSlot = tasks.AcquireRequestSlot();
        token.ThrowIfCancellationRequested();
        var request = new AiRequest(settings, key, "", [], [], [], []) { OutputTokenLimit = ProbeOutputLimit };
        var started = DateTimeOffset.UtcNow;
        using var task = tasks.Create(new(AiTaskKind.Connection, AiTaskPhase.RequestPending,
            AiTaskRecoveryStore.ContextHash(request, ""), [], [], started)
        { Requests = 1, UnknownUsage = true, ReservedTokens = ReservedTokens, Message = "正在校验所选模型。" });
        using var period = task.TrackUsagePeriod(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var charge = 0;
        var failure = "AI 连接校验超时。";
        AiTaskCheckpoint Finish(AiTaskCheckpoint checkpoint, AiClientResult? reply)
        {
            var usage = reply?.Usage is { Valid: true } reported ? reported : null;
            return checkpoint with { Phase = failure.Length == 0 ? AiTaskPhase.Completed : AiTaskPhase.Cancelled,
                ReportedTokens = usage?.Total ?? 0, LastUsage = usage, UnknownUsage = usage is null,
                ReservedTokens = usage is null ? ReservedTokens : 0,
                Message = token.IsCancellationRequested ? "已取消模型校验。" : failure.Length == 0 ? reply!.Message : failure };
        }
        try
        {
            var reply = await AiUsageTracking.TrackAsync(task, client.ProbeModelAsync, request, deadline.Token,
                (checkpoint, result) =>
                {
                    if (!deadline.IsCancellationRequested)
                    {
                        failure = result.Message;
                        if (result.Succeeded)
                        {
                            var actual = result.Usage is { Valid: true } usage ? usage.Total : ReservedTokens;
                            if (limits.WouldExceedTokenLimit((long)actual + 100))
                                failure = "模型校验已完成，但实际用量已使剩余排序预算不足。";
                            else { charge = actual; failure = ""; }
                        }
                    }
                    return Finish(checkpoint, result);
                }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return failure.Length == 0 ? (new(true, reply.Message) { Usage = reply.Usage }, charge)
                : (Failed(failure) with { Usage = reply.Usage }, 0);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (HttpRequestException) { failure = "无法连接 AI 服务，请检查网络和服务地址。"; }
        catch (TimeoutException) { }
        finally
        {
            if (task.Current.Phase == AiTaskPhase.RequestPending) task.Save(Finish(task.Current, null));
        }
        return (Failed(failure), 0);
    }

    public static async Task<AiConnectionResult> CheckAsync(IAiClient client, AiSettings settings, string? key,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            settings = settings.Normalize();
            if (!settings.Enabled) return Failed("AI 未启用。");
            if (AiClient.ConfigurationProblem(settings) is { } problem) return Failed(problem);
            var connection = await client.TestConnectionAsync(settings, key, deadline.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) return Failed("AI 连接校验超时。");
            return connection.Succeeded ? connection : Failed(connection.Message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failed("AI 连接校验超时。"); }
        catch (HttpRequestException) { return Failed("无法连接 AI 服务，请检查网络和服务地址。"); }
        catch (IOException) { return Failed("读取 AI 连接校验响应失败。"); }
        catch (ArgumentException) { return Failed("AI 配置无效，请检查服务地址和模型名称。"); }
        catch (TimeoutException) { return Failed("AI 连接校验超时。"); }
    }

    private static AiConnectionResult Failed(string reason) => new(false,
        "排序前 AI 连接校验未通过：" + reason + " 已中断自动排序，未调整排序、订阅模组或启动游戏测试。请检查设置后重新开始。");
}
