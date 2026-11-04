using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Core;
using RimWise.Formats;
using RimWise.Integration.Configuration;
using RimWise.Integration.Scanning;

namespace RimWise.Integration.Cache;

public sealed record StartupSnapshot(ModsConfigDocument Configuration, ModScanResult Scan, DateTimeOffset CreatedAt);

/// <summary>Disposable display snapshot. Reads no game files and writes only a fixed app-local cache.</summary>
public sealed class StartupCacheStore(string applicationDirectory)
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const int MaximumEntries = ModScanner.MaximumDirectories;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 32 };
    public string CachePath { get; } = Path.Combine(Path.GetFullPath(applicationDirectory), "Data", "startup-cache.json");

    // Explicit DTOs keep disk schema independent from catalogue indexes and runtime scan objects.
    private sealed record Envelope(int Schema, string Sha256, byte[] Payload);
    private sealed record CacheData(GamePaths Paths, string ConfigPath, DateTimeOffset CreatedAt, byte[] Configuration,
        string DetectedVersion, string RawVersion, string VersionFile, string TargetVersion, CachedMod[] Mods, CatalogueDiagnostic[] Diagnostics);
    private sealed record CachedMod(string Directory, string Folder, ModSource Source, string Workshop, CachedMetadata? Metadata,
        CatalogueDiagnostic[] Diagnostics, DateTimeOffset? LastUpdatedUtc = null, ModCodeKind CodeKind = ModCodeKind.Unknown,
        ModContentKinds ContentKinds = ModContentKinds.None);
    private sealed record CachedMetadata(string Name, string Author, string PackageId, string[] Versions, string LegacyVersion,
        ModDependency[] Dependencies, string[] Before, string[] After, string[] Incompatible, bool Versioned, string Description);

    public StartupSnapshot? TryLoad(GamePaths paths)
    {
        try
        {
            RejectLinks(CachePath);
            if (!File.Exists(CachePath)) return null;
            using var input = File.OpenRead(CachePath);
            var bytes = OrderBytes(input);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options);
            if (envelope is not { Schema: 1, Payload.Length: > 0 } || envelope.Payload.Length > MaximumBytes
                || !string.Equals(envelope.Sha256, Convert.ToHexString(SHA256.HashData(envelope.Payload)), StringComparison.Ordinal)) return null;
            var data = JsonSerializer.Deserialize<CacheData>(envelope.Payload, Options);
            if (data is null || !SamePaths(data.Paths, paths)
                || !string.Equals(data.ConfigPath, CurrentOrderLocation.Resolve(paths), StringComparison.OrdinalIgnoreCase)
                || data.Configuration is null || data.Configuration.Length > OrderFileCodec.MaximumBytes
                || data.Mods is null || data.Mods.Length > MaximumEntries || data.Diagnostics is null) return null;
            using var configStream = new MemoryStream(data.Configuration);
            var config = new ModsConfigXmlFormat().Read(configStream);
            if (config.Order.PackageIds.Count > MaximumEntries) return null;
            var normalizedPaths = paths.Normalize();
            var mods = data.Mods.Select(mod => RestoreMod(mod, normalizedPaths)).ToArray();
            ValidateDiagnostics(data.Diagnostics);
            var detected = new GameVersionDetection(GameVersion.Parse(data.DetectedVersion), Text(data.RawVersion), Text(data.VersionFile));
            return new(config, new(detected, GameVersion.Parse(data.TargetVersion), new ModCatalogue(mods), data.Diagnostics), data.CreatedAt);
        }
        catch (Exception ex) when (CacheFailure(ex)) { return null; }
    }

    public bool TrySave(GamePaths paths, ModsConfigDocument configuration, ModScanResult scan)
    {
        string? temporary = null;
        try
        {
            if (configuration.Order.PackageIds.Count > MaximumEntries || scan.Catalogue.Installations.Count > MaximumEntries) return false;
            var mods = scan.Catalogue.Installations.Select(mod => new CachedMod(mod.Directory, mod.FolderName, mod.Source, mod.WorkshopId,
                mod.Metadata is not { } meta ? null : new(meta.Name, meta.Author, meta.PackageId, meta.SupportedVersions.ToArray(), meta.LegacyTargetVersion,
                    meta.Requirements.Dependencies.ToArray(), meta.Requirements.LoadBefore.ToArray(), meta.Requirements.LoadAfter.ToArray(),
                    meta.Requirements.IncompatibleWith.ToArray(), meta.HasVersionedRequirements, meta.Description), mod.Diagnostics.ToArray(), mod.LastUpdatedUtc, mod.CodeKind, mod.ContentKinds)).ToArray();
            var data = new CacheData(paths.Normalize(), CurrentOrderLocation.Resolve(paths), DateTimeOffset.UtcNow, new ModsConfigXmlFormat().Write(configuration),
                scan.DetectedVersion.Version?.ToString() ?? "", scan.DetectedVersion.RawText, scan.DetectedVersion.EvidenceFile,
                scan.TargetVersion?.ToString() ?? "", mods, scan.Diagnostics.ToArray());
            var payload = JsonSerializer.SerializeToUtf8Bytes(data, Options);
            if (payload.Length > MaximumBytes / 2) return false;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToHexString(SHA256.HashData(payload)), payload), Options);
            if (bytes.Length > MaximumBytes) return false;
            RejectLinks(CachePath);
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            temporary = Path.Combine(Path.GetDirectoryName(CachePath)!, ".startup-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            RejectLinks(CachePath);
            File.Move(temporary, CachePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (CacheFailure(ex)) { return false; }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static byte[] OrderBytes(Stream input)
    {
        if (input.Length > MaximumBytes) throw new InvalidDataException("缓存过大。");
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("缓存过大。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static ModInstallation RestoreMod(CachedMod mod, GamePaths paths)
    {
        if (mod is null || !Enum.IsDefined(mod.Source) || !Enum.IsDefined(mod.CodeKind) || !Path.IsPathFullyQualified(mod.Directory)) throw new InvalidDataException();
        if (((int)mod.ContentKinds & ~127) != 0) throw new InvalidDataException();
        var root = mod.Source switch
        {
            ModSource.Game => paths.GameDirectory.Length > 0 ? Path.Combine(paths.GameDirectory, "Data") : "",
            ModSource.Workshop => paths.WorkshopDirectory,
            _ => paths.LocalModsDirectory.Length > 0 ? paths.LocalModsDirectory : paths.GameDirectory.Length > 0 ? Path.Combine(paths.GameDirectory, "Mods") : ""
        };
        // Cached paths never grant access to a different tree, even for lazy preview reads.
        var directory = Path.GetFullPath(mod.Directory);
        if (root.Length == 0 || !string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(directory), mod.Folder, StringComparison.Ordinal)) throw new InvalidDataException();
        ValidateDiagnostics(mod.Diagnostics);
        ModMetadata? metadata = null;
        if (mod.Metadata is { } meta)
        {
            CheckList(meta.Versions); CheckList(meta.Before); CheckList(meta.After); CheckList(meta.Incompatible);
            if (meta.Dependencies is null || meta.Dependencies.Length > MaximumEntries) throw new InvalidDataException();
            foreach (var dep in meta.Dependencies) { Text(dep.PackageId); Text(dep.DisplayName); }
            metadata = new(Text(meta.Name), Text(meta.Author), Text(meta.PackageId), meta.Versions, Text(meta.LegacyVersion),
                new(meta.Dependencies, meta.Before, meta.After, meta.Incompatible), meta.Versioned, Text(meta.Description));
        }
        return new(directory, Text(mod.Folder), mod.Source, Text(mod.Workshop), metadata, mod.Diagnostics)
        { LastUpdatedUtc = mod.LastUpdatedUtc, CodeKind = mod.CodeKind, ContentKinds = mod.ContentKinds };
    }

    private static bool SamePaths(GamePaths left, GamePaths right)
    {
        left = left.Normalize(); right = right.Normalize();
        return string.Equals(left.GameDirectory, right.GameDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.LocalModsDirectory, right.LocalModsDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.WorkshopDirectory, right.WorkshopDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ConfigDirectory, right.ConfigDirectory, StringComparison.OrdinalIgnoreCase)
            && left.TargetVersionOverride == right.TargetVersionOverride;
    }
    private static string Text(string value) => value is not null && value.Length <= 2 * 1024 * 1024 ? value : throw new InvalidDataException();
    private static void CheckList(string[] values)
    {
        if (values is null || values.Length > MaximumEntries) throw new InvalidDataException();
        foreach (var value in values) Text(value);
    }
    private static void ValidateDiagnostics(CatalogueDiagnostic[] values)
    {
        if (values is null || values.Length > MaximumEntries * 10) throw new InvalidDataException();
        foreach (var value in values)
        {
            if (value is null || !Enum.IsDefined(value.Severity)) throw new InvalidDataException();
            Text(value.Code); Text(value.Message); Text(value.Location); Text(value.Identifier);
        }
    }
    private static bool CacheFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or ArgumentException or InvalidDataException or System.Xml.XmlException or NotSupportedException or NullReferenceException;

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存目录不能包含链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
