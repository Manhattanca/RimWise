using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWise.Integration.Marketplace;
using RimWise.Integration.Safety;

namespace RimWise.Desktop.Services;

public sealed record MarketplaceSavedDraft(string Key, MarketplaceManifest Manifest, bool IsUpdate, DateTimeOffset SavedUtc)
{
    public IReadOnlyList<byte[]> ImageContents { get; init; } = [];
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Manifest.Title) ? "未命名草稿" : Manifest.Title;
    [JsonIgnore] public string Summary => $"{(IsUpdate ? "更新草稿" : "新排序草稿")} · {Manifest.Mods.Count} 个模组 · {SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>Editable local snapshots, independent of publication validation and remote identifiers.</summary>
public sealed class MarketplaceDraftStore
{
    private const int MaximumBytes = 23 * 1024 * 1024;
    private readonly string profile;
    private readonly string directory;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 24 };

    public MarketplaceDraftStore(string profileDirectory)
    {
        profile = ScopedPaths.Ordinary(profileDirectory);
        directory = ScopedPaths.Child(profile, Path.Combine("Data", "Marketplace", "Drafts"));
    }

    public void Save(string key, MarketplaceManifest manifest, bool isUpdate, IReadOnlyList<byte[]>? images = null)
    {
        var path = DraftPath(key);
        images ??= [];
        if (images.Count > MarketplaceImages.MaximumCount) throw new InvalidDataException("最多保存三张图片。");
        foreach (var image in images) _ = MarketplaceImages.Inspect(image);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new MarketplaceSavedDraft(key, manifest, isUpdate, DateTimeOffset.UtcNow) { ImageContents = images }, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("本地草稿过大，未覆盖原草稿。");
        ScopedPaths.Ordinary(directory);
        Directory.CreateDirectory(directory);
        var temporary = ScopedPaths.Ordinary(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            ScopedPaths.Ordinary(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<MarketplaceSavedDraft> ReadAll()
    {
        var result = new List<MarketplaceSavedDraft>();
        ScopedPaths.Ordinary(directory);
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var key = Path.GetFileNameWithoutExtension(file);
                    var path = DraftPath(key);
                    if (new FileInfo(path).Length is 0 or > MaximumBytes) continue;
                    var draft = JsonSerializer.Deserialize<MarketplaceSavedDraft>(File.ReadAllBytes(path), Options);
                    if (draft is not null && draft.Key == key && Usable(draft.Manifest) && draft.ImageContents is { Count: <= 3 })
                    { foreach (var image in draft.ImageContents) _ = MarketplaceImages.Inspect(image); result.Add(draft); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or MarketplaceException) { }
            }
        }
        // Preserve the previous single draft without deleting it. A deterministic key
        // makes edits replace its migrated copy rather than creating duplicates.
        const string legacyKey = "00000000000000000000000000000001";
        if (!result.Any(draft => draft.Key == legacyKey))
        {
            try
            {
                var legacy = new MarketplaceCredentialStore(profile);
                if (legacy.ReadDraft() is { } bytes && JsonSerializer.Deserialize<MarketplaceManifest>(bytes, Options) is { } manifest && Usable(manifest))
                    result.Add(new(legacyKey, manifest, false, File.GetLastWriteTimeUtc(legacy.DraftPath)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result.OrderByDescending(draft => draft.SavedUtc).ToArray();
    }

    public void Delete(string key)
    {
        var path = DraftPath(key);
        // Delete the legacy source first so a removed migrated draft cannot reappear.
        // This path is separate from the encrypted account credentials.
        if (key == "00000000000000000000000000000001")
            File.Delete(ScopedPaths.Ordinary(new MarketplaceCredentialStore(profile).DraftPath));
        File.Delete(ScopedPaths.Ordinary(path));
    }

    private static bool Usable(MarketplaceManifest? value) => value is not null && value.Mods is not null
        && value.RequiredDlc is not null && value.Mods.All(mod => mod is not null && mod.PackageId is not null)
        && value.RequiredDlc.All(id => id is not null) && value.Id is not null && value.Author is not null
        && value.Version is not null && value.GameVersion is not null && value.Title is not null && value.Description is not null;

    private string DraftPath(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("本地草稿编号无效。");
        return ScopedPaths.Child(directory, key + ".json");
    }
}
