using RimWise.Integration.Diagnostics;
using RimWise.Integration.Marketplace;

namespace RimWise.Desktop.Services;

public sealed class MarketplaceAuthorSession
{
    private readonly MarketplaceCredentialStore store;
    private string? accessToken;
    private GitHubMarketplaceUser? user;

    public string ClientId { get; }
    public bool IsConfigured { get; }
    public GitHubMarketplaceUser? User => user;

    public MarketplaceAuthorSession(string profileDirectory)
    {
        store = new MarketplaceCredentialStore(profileDirectory);
        ClientId = store.ReadClientId();
        IsConfigured = !string.IsNullOrWhiteSpace(store.ReadClientSecret());
    }

    public async Task<GitHubMarketplaceUser?> RefreshAsync(CancellationToken token)
    {
        var saved = accessToken ?? store.ReadToken();
        if (saved is null) return user = null;

        using var publisher = new GitHubMarketplacePublisher();
        try
        {
            user = await publisher.GetAuthenticatedUserAsync(saved, token);
            token.ThrowIfCancellationRequested();
            accessToken = saved;
            return user;
        }
        catch (MarketplaceException ex) when (ex.Message.Contains("HTTP 401", StringComparison.Ordinal))
        {
            store.DeleteToken();
            accessToken = null;
            return user = null;
        }
    }

    public async Task<GitHubMarketplaceUser> LoginAsync(Action<Uri> openBrowser, CancellationToken token)
    {
        if (await RefreshAsync(token) is { } existing) return existing;
        if (!IsConfigured) throw new InvalidOperationException("此版本未配置 GitHub 发布登录。");

        var secret = store.ReadClientSecret();
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("此版本未配置 GitHub 发布登录。");
        DiagnosticRedactor.RegisterSecret(secret);
        using var authorization = new GitHubLoopbackAuthorization(ClientId, secret);
        var granted = await authorization.AuthorizeAsync((uri, callbackToken) =>
        {
            callbackToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            openBrowser(uri);
            return Task.CompletedTask;
        }, token);
        DiagnosticRedactor.RegisterSecret(granted.AccessToken);
        using var publisher = new GitHubMarketplacePublisher();
        var authenticated = await publisher.GetAuthenticatedUserAsync(granted.AccessToken, token);
        token.ThrowIfCancellationRequested();
        store.SaveToken(granted.AccessToken);
        accessToken = granted.AccessToken;
        return user = authenticated;
    }

    public async Task<MarketplaceSubmission> SubmitAsync(MarketplaceManifest manifest,
        IProgress<string> progress, CancellationToken token)
    {
        var tokenValue = accessToken ?? store.ReadToken();
        if (tokenValue is null) throw new InvalidOperationException("请先登录 GitHub。");
        accessToken = tokenValue;
        using var publisher = new GitHubMarketplacePublisher();
        progress.Report("正在向市场提交排序审核…");
        return await publisher.SubmitAsync(manifest, tokenValue, token);
    }

    public async Task<MarketplaceSubmission> SubmitWithImagesAsync(MarketplaceManifest manifest, IReadOnlyList<MarketplaceImageUpload> images,
        IProgress<string> progress, CancellationToken token)
    {
        var saved = accessToken ?? store.ReadToken() ?? throw new InvalidOperationException("请先登录 GitHub。");
        using var publisher = new GitHubMarketplacePublisher();
        progress.Report($"正在上传 {images.Count} 张图片及排序清单…");
        return await publisher.SubmitWithImagesAsync(manifest, images, saved, token);
    }

    public async Task<MarketplaceSubmission> RequestDeletionAsync(MarketplaceEntry entry, CancellationToken token)
    {
        var tokenValue = accessToken ?? store.ReadToken();
        if (tokenValue is null) throw new InvalidOperationException("请先登录 GitHub。");
        using var publisher = new GitHubMarketplacePublisher();
        return await publisher.RequestDeletionAsync(entry, tokenValue, token);
    }

    public void Logout()
    {
        store.DeleteToken();
        accessToken = null;
        user = null;
    }
}
