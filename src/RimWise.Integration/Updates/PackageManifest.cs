using System.Security.Cryptography;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Updates;

public sealed record PackageFile(string Path, string Sha256);

/// <summary>Distribution inventory, never a general-purpose filesystem instruction list.</summary>
public sealed record PackageManifest(int Schema, string Product, string Version, PackageFile[] Files, PackageFile[] Retired)
{
    public const string FileName = "RimWise.package.json";
    private static readonly string[] Required = ["RimWise.exe", "RimWise.dll", "RimWise.deps.json",
        "RimWise.runtimeconfig.json", "RimWise.Core.dll", "RimWise.Formats.dll", "RimWise.Integration.dll",
        "SteamBridge/RimWise.SteamBridge.exe", "SteamBridge/RimWise.SteamBridge.dll",
        "SteamBridge/RimWise.SteamBridge.deps.json", "SteamBridge/RimWise.SteamBridge.runtimeconfig.json",
        "SteamBridge/Steamworks.NET.dll", "SteamBridge/steam_api64.dll"];

    internal static (PackageManifest Manifest, string Fingerprint) Read(string installation, string version)
    {
        var path = ScopedPaths.Child(installation, FileName);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("更新清单过大。");
        var bytes = File.ReadAllBytes(path);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(bytes)
            ?? throw new InvalidDataException("更新清单无效。");
        if (manifest.Schema != 1 || manifest.Product != "RimWise" || manifest.Version != version
            || manifest.Files is null || manifest.Retired is null || manifest.Files.Length > 4096 || manifest.Retired.Length > 16384)
            throw new InvalidDataException("更新清单与正在运行的版本不一致。请完整覆盖解压新版文件。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ValidateFile(file);
            if (!names.Add(file.Path)) throw new InvalidDataException("更新清单包含重复路径。");
        }
        if (Required.Any(name => !names.Contains(name))) throw new InvalidDataException("更新清单缺少必要的运行文件。");
        if (System.Version.TryParse(version.Split('-')[0], out var number) && number >= new System.Version(0, 3)
            && new[] { "Decompiler/RimWise.Decompiler.exe", "Decompiler/RimWise.Decompiler.dll",
                "Decompiler/RimWise.Decompiler.deps.json", "Decompiler/RimWise.Decompiler.runtimeconfig.json",
                "Decompiler/ICSharpCode.Decompiler.dll", "Decompiler/ModelContextProtocol.dll",
                "Decompiler/ModelContextProtocol.Core.dll" }.Any(name => !names.Contains(name)))
            throw new InvalidDataException("更新清单缺少本地反编译运行文件。请完整覆盖解压新版文件。");
        foreach (var file in manifest.Retired) ValidateFile(file);
        return (manifest, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal static void ValidateFile(PackageFile file)
    {
        if (file is null || !AllowedPath(file.Path) || file.Sha256 is null || file.Sha256.Length != 64
            || !file.Sha256.All(char.IsAsciiHexDigit)) throw new InvalidDataException("更新清单含无效文件记录。");
    }

    internal static bool AllowedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':')) return false;
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
            || p.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)) return false;
        // User Data, custom subfolders, caches, logs and mod directories cannot be cleanup targets.
        return parts.Length == 1 && (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            || path is "Decompiler/runtimes/win/lib/net10.0/System.Diagnostics.EventLog.dll"
                or "Decompiler/runtimes/win/lib/net10.0/System.Diagnostics.EventLog.Messages.dll"
            || parts.Length == 2 && ((parts[0] is "SteamBridge" or "Decompiler") && (parts[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || parts[1].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || parts[1].EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || parts[1].EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                || parts[0] == "licenses" && parts[1].EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    internal static string HashFile(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
