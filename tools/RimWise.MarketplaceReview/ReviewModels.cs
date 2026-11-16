using System.Text.Json;
using System.Text.RegularExpressions;
using RimWise.Integration.Marketplace;

namespace RimWise.MarketplaceReview;

internal sealed record ReviewPull(int Number, string Title, string Author, string Body, string Sha,
    string HeadRepository, string BaseSha, string BaseBranch, bool Draft, DateTimeOffset Updated)
{
    internal long AuthorId { get; init; }
    public override string ToString() => $"#{Number}  {Title}  ·  {Author}";
    internal string Url => $"https://github.com/{ReviewApi.Repository}/pull/{Number}";
}
internal sealed record ChangedFile(string Path, string Status);
internal sealed record ReviewImage(MarketplaceImage Metadata, byte[] Content);
internal sealed record ReviewSnapshot(ReviewPull Pull, string Text, IReadOnlyList<ChangedFile> Files,
    IReadOnlyList<ReviewImage> Images, bool Supported, string Warning)
{
    internal bool IsRemoval => Files.Count > 0 && Files.All(file => file.Status == "removed");
}

internal static partial class ReviewRules
{
    internal const int MaximumImages = 3;
    internal static bool IsSha(string value) => value.Length == 40 && value.All(char.IsAsciiHexDigit);
    internal static bool IsRepository(string value) => RepositoryPattern().IsMatch(value);
    internal static bool IsManifest(string path, string author) => ManifestPattern().IsMatch(path)
        && path.Split('/')[2].Equals(author, StringComparison.OrdinalIgnoreCase);
    internal static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".webp" }
        .Contains(Path.GetExtension(path).ToLowerInvariant());
    internal static bool IsImagePath(string path, string author) => path.StartsWith("marketplace/images/" + author + "/", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\') && !path.Any(char.IsControl) && IsImage(path);
    internal static Uri Raw(string repository, string sha, string path)
    {
        if (!IsRepository(repository) || !IsSha(sha) || path.StartsWith('/') || path.Split('/').Any(part => part is "" or "." or "..")
            || path.Contains('\\') || path.Any(char.IsControl)) throw new InvalidDataException("投稿文件位置无效。");
        return new("https://raw.githubusercontent.com/" + repository + "/" + sha + "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
    }
    internal static bool IsAllowedImageHost(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.Host == "raw.githubusercontent.com" || uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/user-attachments/assets/", StringComparison.Ordinal)
            || uri.Host == "user-images.githubusercontent.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)
                && uri.Host.StartsWith("github-production-user-asset-", StringComparison.Ordinal));

    internal static ReviewPull Pull(JsonElement root)
    {
        var head = root.GetProperty("head");
        var basis = root.GetProperty("base");
        var repo = head.GetProperty("repo");
        var headRepository = repo.ValueKind == JsonValueKind.Null ? "" : repo.GetProperty("full_name").GetString() ?? "";
        var sha = head.GetProperty("sha").GetString() ?? "";
        var baseSha = basis.GetProperty("sha").GetString() ?? "";
        if (!IsSha(sha) || !IsSha(baseSha) || basis.GetProperty("repo").GetProperty("full_name").GetString() != ReviewApi.Repository)
            throw new InvalidDataException("GitHub 返回了不匹配的投稿仓库或提交。");
        return new(root.GetProperty("number").GetInt32(), root.GetProperty("title").GetString() ?? "",
            root.GetProperty("user").GetProperty("login").GetString() ?? "", root.GetProperty("body").GetString() ?? "",
            sha, headRepository, baseSha, basis.GetProperty("ref").GetString() ?? "", root.GetProperty("draft").GetBoolean(),
            root.GetProperty("updated_at").GetDateTimeOffset()) { AuthorId = root.GetProperty("user").GetProperty("id").GetInt64() };
    }
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"^marketplace/manifests/[A-Za-z0-9-]+/[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\.json$")]
    private static partial Regex ManifestPattern();
}

internal sealed class NotificationState
{
    public Dictionary<int, string> Heads { get; set; } = [];
    internal IReadOnlyList<ReviewPull> Update(IReadOnlyList<ReviewPull> pulls)
    {
        var changed = pulls.Where(pull => !Heads.TryGetValue(pull.Number, out var sha) || sha != pull.Sha).ToArray();
        Heads = pulls.ToDictionary(pull => pull.Number, pull => pull.Sha);
        return changed;
    }
}
