namespace RimWise.Integration.AI;

public sealed record AiServicePreset(string Name, string Endpoint, string Note = "");

/// <summary>OpenAI-compatible chat endpoints. Models remain server/user supplied.</summary>
public static class AiServicePresets
{
    public static IReadOnlyList<AiServicePreset> All { get; } = Array.AsReadOnly(new[]
    {
        new AiServicePreset("自定义 / 中转服务", "", "填写服务商提供的 OpenAI 兼容 API 地址，或粘贴连接信息。"),
        new AiServicePreset("OpenAI", "https://api.openai.com/v1/chat/completions"),
        new AiServicePreset("DeepSeek", "https://api.deepseek.com/chat/completions"),
        new AiServicePreset("硅基流动 SiliconFlow", "https://api.siliconflow.cn/v1/chat/completions"),
        new AiServicePreset("月之暗面 Kimi", "https://api.moonshot.cn/v1/chat/completions"),
        new AiServicePreset("火山方舟 / 豆包", "https://ark.cn-beijing.volces.com/api/v3/chat/completions", "按服务商说明使用模型 ID 或推理接入点 ID；列表不可用时手动填写。"),
        new AiServicePreset("阿里云百炼 / 通义千问（中国内地）", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"),
        new AiServicePreset("阿里云百炼 / 通义千问（国际）", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions", "不同地域使用对应的密钥。"),
        new AiServicePreset("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4/chat/completions"),
        new AiServicePreset("Google Gemini（兼容接口）", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"),
        new AiServicePreset("OpenRouter", "https://openrouter.ai/api/v1/chat/completions"),
        new AiServicePreset("Groq", "https://api.groq.com/openai/v1/chat/completions"),
        new AiServicePreset("xAI / Grok", "https://api.x.ai/v1/chat/completions"),
        new AiServicePreset("Ollama（本机）", "http://localhost:11434/v1/chat/completions", "先启动本地服务并下载模型；通常无需密钥。"),
        new AiServicePreset("LM Studio（本机）", "http://localhost:1234/v1/chat/completions", "先启动本地服务并加载模型；如启用了认证，请填写密钥。")
    });

    public static int Find(string endpoint)
    {
        var normalized = NormalizeAddress(endpoint);
        for (var index = 1; index < All.Count; index++)
            if (string.Equals(normalized, All[index].Endpoint, StringComparison.Ordinal)) return index;
        return 0;
    }

    public static string NormalizeAddress(string endpoint)
    {
        try { return (AiSettings.Default with { Endpoint = endpoint }).Normalize().Endpoint.TrimEnd('/'); }
        catch (ArgumentException) { return endpoint.Trim(); }
    }
}
