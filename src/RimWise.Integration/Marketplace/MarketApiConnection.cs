using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RimWise.Integration.Marketplace;

/// <summary>Trust is scoped to this API and host, never to Windows or other HTTP traffic.</summary>
public static class MarketApiConnection
{
    public static Uri DefaultOrigin { get; } = new("https://122.51.236.8:5443/");

    public static Uri ReadOrigin()
    {
        var value = Environment.GetEnvironmentVariable("RIMWISE_MARKET_API");
        return string.IsNullOrWhiteSpace(value) ? DefaultOrigin : new Uri(value, UriKind.Absolute);
    }

    public static HttpClientHandler CreateHandler(Uri origin)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(), UseCookies = true,
            AllowAutoRedirect = false, UseDefaultCredentials = false
        };
        if (origin == DefaultOrigin)
            handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        return handler;
    }

    public static bool ValidateCertificate(HttpRequestMessage request, X509Certificate2? certificate,
        X509Chain? platformChain, SslPolicyErrors errors)
    {
        if (request.RequestUri is not { } uri || uri.Scheme != "https" ||
            !uri.Host.Equals(DefaultOrigin.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != DefaultOrigin.Port ||
            certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;
        try
        {
            using var stream = typeof(MarketApiConnection).Assembly.GetManifestResourceStream("RimWise.MarketApi.Root")
                ?? throw new InvalidOperationException("市场证书配置缺失。");
            using var reader = new StreamReader(stream);
            using var root = X509Certificate2.CreateFromPem(reader.ReadToEnd());
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(certificate);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or InvalidOperationException) { return false; }
    }
}
