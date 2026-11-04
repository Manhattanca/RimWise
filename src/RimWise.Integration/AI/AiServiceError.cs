namespace RimWise.Integration.AI;

// Only fixed classifications and status codes are persisted, never a provider's error body.
public sealed record AiServiceError(string Kind, int? HttpStatus = null)
{
    internal AiServiceError CopyValidated()
    {
        if (Kind is not ("http" or "network" or "read" or "format" or "size" or "redirect" or "cancelled" or "output-limit" or "incomplete")
            || HttpStatus is < 100 or > 599)
            throw new InvalidDataException("AI service error record is invalid.");
        return this;
    }

    public string DisplayText => Kind switch
    {
        "http" => $"AI 服务返回 HTTP {HttpStatus}。" + (HttpStatus switch
        {
            400 or 413 or 422 => "请检查模型接口参数及上下文容量。",
            401 or 403 => "请检查服务访问权限和密钥。",
            404 => "请检查接口地址和模型名称。",
            429 => "服务限流或额度不足，请检查服务端状态。",
            >= 500 => "服务端处理失败，请检查模型服务日志。",
            _ => "请检查模型服务日志。"
        }),
        "network" => "AI 服务连接失败或中断。",
        "read" => "读取 AI 响应时连接中断。",
        "format" => "AI 服务响应格式不兼容或流式响应不完整。",
        "output-limit" => "AI 已达到本次输出额度，最终方案未完成；推理也会占用输出额度。",
        "incomplete" => "AI 回复缺少正常结束信号，未应用部分方案。",
        "size" => "AI 单条消息或回复正文过大，已停止接收；未应用不完整方案。",
        "redirect" => "AI 接口重定向未通过检查，请核对最终接口地址。",
        "cancelled" => "AI 请求已取消。",
        _ => "AI 服务请求失败。"
    };
}
