namespace RimWise.Integration.Marketplace;

/// <summary>The data repository is independent of software releases and source.</summary>
public static class MarketplaceRepository
{
    public const string Owner = "Manhattanca";
    public const string Name = "RimWise-Marketplace";
    public const string FullName = Owner + "/" + Name;
    public const string RawRoot = "https://raw.githubusercontent.com/" + FullName + "/main/";
    public const string CatalogueUrl = RawRoot + "marketplace/index.json";
    public const string PullRequestPath = "/" + FullName + "/pull/";
}
