using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWise.Integration.Configuration;

namespace RimWise.Desktop.Services;

internal sealed record ImportedAppearanceTheme(string Name, AppearanceSettings Settings, AppearanceImage? Image);

/// <summary>A bounded, data-only container. Entries are read explicitly, never extracted to disk.</summary>
internal static class AppearanceThemePackage
{
    internal const string Extension = ".rimwise-theme";
    internal const int MaximumManifestBytes = 256_000;
    internal const int MaximumPackageBytes = AppearanceImage.MaximumBytes + MaximumManifestBytes + 64_000;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        IgnoreReadOnlyProperties = true, WriteIndented = true
    };
    private sealed record Manifest
    {
        [JsonRequired] public int SchemaVersion { get; init; }
        [JsonRequired] public string Name { get; init; } = "";
        [JsonRequired] public AppearanceSettings Settings { get; init; } = new();
    }

    internal static void Write(string path, string name, AppearanceSettings settings, AppearanceImage? image)
    {
        AppearanceSettingsStore.ValidateName(name); settings.Validate();
        ValidateImage(settings, image);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest { SchemaVersion = 1, Name = name, Settings = settings }, Json);
        if (manifest.Length > MaximumManifestBytes) throw new InvalidDataException("主题配置过大。");
        var destination = Path.GetFullPath(path);
        AppearanceImage.RejectLinks(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".rimwise-theme-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    Add(archive, "theme.json", manifest);
                    if (image is not null) Add(archive, "background.png", image.Png);
                }
                if (output.Length > MaximumPackageBytes) throw new InvalidDataException("主题文件过大。");
                output.Flush(flushToDisk: true);
            }
            // Never leave a partially-written destination or truncate an existing exported theme on failure.
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static ImportedAppearanceTheme Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumPackageBytes) throw new InvalidDataException("主题文件为空或过大。");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > 2) throw new InvalidDataException("主题文件内容无效，只允许主题配置和一张背景图片。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entry.FullName is not ("theme.json" or "background.png") || !entries.TryAdd(entry.FullName, entry)
                || kind is not (0 or 0x8000))
                throw new InvalidDataException("主题包含重复、未知文件或链接，不予导入。");
        }
        if (!entries.TryGetValue("theme.json", out var config)) throw new InvalidDataException("主题配置缺失。");
        var bytes = ReadEntry(config, MaximumManifestBytes);
        using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 })) RejectDuplicates(document.RootElement);
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes, Json) ?? throw new InvalidDataException("主题配置缺失。");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("暂不支持此主题文件版本，请更新软件后重试。");
        AppearanceSettingsStore.ValidateName(manifest.Name);
        if (manifest.Settings is null) throw new InvalidDataException("主题配置缺失。");
        manifest.Settings.Validate();
        AppearanceImage? image = null;
        if (manifest.Settings.Background is { } hash)
        {
            if (!entries.TryGetValue("background.png", out var background)) throw new InvalidDataException("主题背景图片缺失。");
            var png = ReadEntry(background, AppearanceImage.MaximumBytes);
            if (HashName(png) != hash) throw new InvalidDataException("主题背景图片校验失败。");
            // Only a validated static bitmap is ever applied; no XAML, scripts, URLs or filesystem references.
            if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("主题背景图片格式无效。");
            image = AppearanceImage.Decode(png) with { Name = hash, Png = png };
        }
        else if (entries.ContainsKey("background.png")) throw new InvalidDataException("主题包含未声明的背景图片。");
        return new(manifest.Name, manifest.Settings, image);
    }

    private static void ValidateImage(AppearanceSettings settings, AppearanceImage? image)
    {
        if (settings.Background is null && image is null) return;
        if (image is null || image.Png.Length is <= 0 or > AppearanceImage.MaximumBytes
            || settings.Background != image.Name || HashName(image.Png) != image.Name)
            throw new InvalidDataException("背景图片缺失或校验失败，请重新导入背景图片后重试。");
    }
    private static string HashName(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".png";
    private static void Add(ZipArchive archive, string name, byte[] bytes)
    {
        using var output = archive.CreateEntry(name, CompressionLevel.Optimal).Open(); output.Write(bytes);
    }
    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length is <= 0 || entry.Length > maximum) throw new InvalidDataException("主题内容为空或超出大小限制。");
        using var stream = entry.Open();
        var bytes = new byte[(int)entry.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("主题内容长度无效。");
        return bytes;
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("主题配置包含重复字段。");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
