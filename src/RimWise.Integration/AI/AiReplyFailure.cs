namespace RimWise.Integration.AI;

/// <summary>Trusted client classification, never a provider-supplied instruction or game diagnosis.</summary>
public enum AiReplyFailure { None, InvalidJson, InvalidStructure, IncompleteResponse, ServiceFailure, Unspecified, OutputLimit }

public static class AiReplyFailureText
{
    public static string Describe(AiReplyFailure failure) => failure switch
    {
        AiReplyFailure.None => "",
        AiReplyFailure.InvalidJson => "AI 回复不是有效 JSON，未接受其中的操作。",
        AiReplyFailure.InvalidStructure => "AI 回复结构未通过校验，未接受其中的操作。",
        AiReplyFailure.IncompleteResponse => "AI 回复未正常结束，未接受未完成的操作。",
        AiReplyFailure.OutputLimit => "AI 已用尽本次输出额度，尚未完成最终方案；思考 token 也占用输出额度。",
        AiReplyFailure.ServiceFailure => "AI 服务或响应读取失败，未取得可用方案。",
        _ => "AI 未返回通过校验的方案，未接受其中的操作。"
    };
}
