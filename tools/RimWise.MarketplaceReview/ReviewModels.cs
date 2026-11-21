using System.Text.Json;

namespace RimWise.MarketplaceReview;

internal sealed record MarketReviewItem(string Id, string? ListingId, string Version, string Title,
    string Author, string OwnerUsername, string State, string Sha256, string Reason,
    string GameVersions, string Description, string ReleaseNotes, string? WorkshopCollectionId,
    string Dlc, int ModCount, IReadOnlyList<ReviewImageReference> Images)
{
    public string ReviewToken { get; init; } = "";
    public override string ToString() => $"{Title} · v{Version} · {Author}";
}

internal sealed record ReviewImageReference(string Id, string Mime, long Bytes, string Sha256);
internal sealed record ReviewImage(byte[] Content);
internal sealed record ReviewSnapshot(MarketReviewItem Item, string Text,
    IReadOnlyList<ReviewImage> Images, bool Supported, string Warning);

internal sealed class NotificationState
{
    public Dictionary<string, string> Heads { get; set; } = new(StringComparer.Ordinal);

    internal IReadOnlyList<MarketReviewItem> Update(IReadOnlyList<MarketReviewItem> items)
    {
        var changed = items.Where(item => !Heads.TryGetValue(item.Id, out var sha)
            || !string.Equals(sha, item.ReviewToken, StringComparison.Ordinal)).ToArray();
        Heads = items.ToDictionary(item => item.Id, item => item.ReviewToken, StringComparer.Ordinal);
        return changed;
    }
}
