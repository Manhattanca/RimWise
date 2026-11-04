using System.ComponentModel;
using System.Security.Authentication;

namespace RimWise.Integration.AI;

/// <summary>Display transport failures without reflecting URLs, credentials or server text.</summary>
internal static class AiTransportError
{
    internal static string Describe(HttpRequestException error)
    {
        var tls = error.HttpRequestError == HttpRequestError.SecureConnectionError;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            tls |= current is AuthenticationException;
            // SEC_E_NO_CREDENTIALS is a local Windows security-provider failure,
            // not an HTTP response proving that an API key was rejected.
            if (current is Win32Exception native && unchecked((uint)native.NativeErrorCode) == 0x8009030E)
                return "Windows 无法建立 AI 安全连接（0x8009030E：安全包中没有可用的凭证）。请在普通桌面环境运行连接诊断；这不能用于判断 API 密钥是否有效。";
        }
        if (tls) return "AI 的 HTTPS 安全连接失败，请检查系统时间、证书及代理的 TLS 设置。证书校验仍然开启。";
        return error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "无法解析 AI 服务地址，请检查地址及 DNS。",
            HttpRequestError.ConnectionError => "无法连接 AI 服务，请检查网络、代理及服务是否可用。",
            _ => "无法连接 AI 服务，请检查地址及网络。"
        };
    }
}
