namespace RimWise.Integration.AI;

/// <summary>Trusted client classification, never a provider-supplied instruction or game diagnosis.</summary>
public enum AiReplyFailure { None, InvalidJson, InvalidStructure, IncompleteResponse, ServiceFailure, Unspecified, OutputLimit, InvalidPlan }

public enum AiPlanIssue { None, Action, Target, Position, Dependency, Constraints }

public static class AiReplyFailureText
{
    public static string Describe(AiReplyFailure failure) => failure switch
    {
        AiReplyFailure.None => "",
        AiReplyFailure.InvalidJson => "AI 回复不是有效 JSON，未接受其中的操作。",
        AiReplyFailure.InvalidStructure => "AI 回复结构未通过校验，未接受其中的操作。",
        AiReplyFailure.InvalidPlan => "AI 回复已解析，但排序方案未通过本地校验，未应用任何动作。",
        AiReplyFailure.IncompleteResponse => "AI 回复未正常结束，未接受未完成的操作。",
        AiReplyFailure.OutputLimit => "AI 已用尽本次输出额度，尚未完成最终方案；思考 token 也占用输出额度。",
        AiReplyFailure.ServiceFailure => "AI 服务或响应读取失败，未取得可用方案。",
        _ => "AI 未返回通过校验的方案，未接受其中的操作。"
    };

    public static string Describe(AiReplyFailure failure, AiPlanIssue issue) => Describe(failure)
        + (failure == AiReplyFailure.InvalidPlan ? issue switch
        {
            AiPlanIssue.Action => "方案包含无效或不允许的动作。",
            AiPlanIssue.Target => "动作目标不在当前排序中、未安装或安装不唯一。",
            AiPlanIssue.Position => "动作指定的位置超出当前排序范围。",
            AiPlanIssue.Dependency => "存在未启用的前置，或提议加入的模组不是已确认的必要前置。",
            AiPlanIssue.Constraints => "方案未满足保护要求或加载顺序约束。",
            _ => ""
        } : "");
}
