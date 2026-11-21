namespace RimWise.Integration.Marketplace;

public sealed record MarketplaceMod(string PackageId, string? WorkshopId);
public enum MarketplaceDeletionScope { SelectedVersion, AllVersions }
public sealed record MarketplaceDeletionRequest(MarketplaceEntry Entry, MarketplaceDeletionScope Scope);
public sealed record MarketplaceImage(string Path, string Sha256, int Bytes, int Width, int Height);
public sealed record MarketplaceImageUpload(MarketplaceImage Image, byte[] Content);

/// <summary>Author is the GitHub login, not a display name. Mods preserve load order.</summary>
public sealed record MarketplaceManifest(
    int SchemaVersion, string Id, string Version, string Title,
    string Author, string Description, string GameVersion,
    IReadOnlyList<string> RequiredDlc, string? WorkshopCollectionId,
    IReadOnlyList<MarketplaceMod> Mods)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteRevisionId { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public IReadOnlyList<MarketplaceImage> Images { get; init; } = [];
}

public sealed record MarketplaceEntry(
    string Id, string Version, string Title, string Author,
    string Description, string GameVersion, IReadOnlyList<string> RequiredDlc,
    string? WorkshopCollectionId, string ManifestPath, string Sha256)
{
    public int? ModCount { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public IReadOnlyList<MarketplaceImage> Images { get; init; } = [];
    public string? AuthorDisplayName { get; init; }
    public bool IsAccountMarket { get; init; }
    public string? PublicationState { get; init; }
    public string? RejectionReason { get; init; }
    public string DisplayAuthor => string.IsNullOrWhiteSpace(AuthorDisplayName) ? (IsAccountMarket ? "排序作者" : Author) : AuthorDisplayName;
}

public sealed record MarketplaceCatalogue(int SchemaVersion, IReadOnlyList<MarketplaceEntry> Entries);
public sealed record MarketplaceResult<T>(T Value, bool FromCache, string? Warning);
public sealed record MarketplaceTransferProgress(long Received, long? Total);

public sealed record GitHubDeviceCode(
    string DeviceCode, string UserCode, Uri VerificationUri, int ExpiresIn, int Interval)
{
    public override string ToString() => $"GitHub device authorization ({ExpiresIn}s)";
}

public sealed record GitHubAccessToken(string AccessToken, string TokenType, string Scope)
{
    public override string ToString() => "GitHub access token (redacted)";
}

public sealed record GitHubMarketplaceUser(string Login, string? Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "市场用户" : Name;
}
public sealed record MarketplaceSubmission(
    int PullRequestNumber, Uri PullRequestUri, string Branch, string ManifestPath, string Sha256)
{
    public bool IsAccountMarket { get; init; }
}

/// <summary>Safe, credential-free message for display. No server response bodies are included.</summary>
public sealed class MarketplaceException(string message) : Exception(message);
