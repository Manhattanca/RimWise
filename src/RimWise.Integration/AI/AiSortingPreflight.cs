namespace RimWise.Integration.AI;

/// <summary>One read-only check before sorting; never falls back to paid generation or retries.</summary>
public static class AiSortingPreflight
{
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
        "排序前 AI 连接校验未通过：" + reason + " 已中断自动排序，未调整排序、订阅模组或启动游戏测试。请检查 AI 设置后重新开始。");
}
