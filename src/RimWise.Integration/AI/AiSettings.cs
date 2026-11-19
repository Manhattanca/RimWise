namespace RimWise.Integration.AI;

public sealed record AiSettings(
    int SchemaVersion,
    bool Enabled,
    AiProviderKind Provider,
    string Endpoint,
    string Model,
    AiPermissionLevel PermissionLevel,
    string SecretName)
{
    public bool StreamResponses { get; init; } = true;
    public bool CompleteBaseAddress { get; init; } = true;
    public static AiSettings Default => new(
        1,
        true,
        AiProviderKind.OpenAiCompatible,
        "https://api.openai.com/v1/chat/completions",
        "",
        AiPermissionLevel.Level1OrderAndRequiredDependencies,
        "RimWise/AiApiKey");

    // Keep the editable address separate from the final HTTP request URL.
    public AiSettings ValidateInput()
    {
        var endpoint = Endpoint.Trim();
        if (endpoint.Length > 2048) throw new ArgumentException("AI 地址过长。", nameof(Endpoint));
        Uri? uri = null;
        if (endpoint.Length > 0 && !Uri.TryCreate(endpoint, UriKind.Absolute, out uri))
            throw new ArgumentException("AI 地址必须是完整的 http 或 https 地址。", nameof(Endpoint));
        else if (endpoint.Length > 0 && uri!.Scheme is not ("http" or "https"))
            throw new ArgumentException("AI 地址只支持 http 或 https。", nameof(Endpoint));
        else if (endpoint.Length > 0 && uri!.Scheme == "http"
            && uri.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new ArgumentException("远程 AI 地址必须使用 HTTPS；HTTP 仅允许本机地址。", nameof(Endpoint));
        if (uri is not null)
        {
            if (uri.UserInfo.Length > 0) throw new ArgumentException("请在密钥栏填写凭据，不要放入 AI 地址。", nameof(Endpoint));
            if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("API 地址不能包含查询参数或片段，请将凭据放入密钥栏。", nameof(Endpoint));
        }
        if (Model.Trim().Length > 200) throw new ArgumentException("模型名称过长。", nameof(Model));
        if (SecretName.Length is < 1 or > 256 || SecretName.Contains('\n') || SecretName.Contains('\r'))
            throw new ArgumentException("密钥名称无效。", nameof(SecretName));
        if (!Enum.IsDefined(PermissionLevel)) throw new ArgumentException("AI 权限级别无效。", nameof(PermissionLevel));
        return this with { Endpoint = endpoint, Model = Model.Trim(), SecretName = SecretName.Trim() };
    }

    public AiSettings Normalize()
    {
        var settings = ValidateInput();
        if (settings.Endpoint.Length == 0) return settings;
        var uri = new Uri(settings.Endpoint);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (settings.CompleteBaseAddress && (uri.AbsolutePath == "/" || path.EndsWith("/v1", StringComparison.Ordinal)))
        {
            uri = new UriBuilder(uri)
            { Path = uri.AbsolutePath == "/" ? "/v1/chat/completions" : path + "/chat/completions" }.Uri;
        }
        return settings with { Endpoint = uri.AbsoluteUri };
    }
}
