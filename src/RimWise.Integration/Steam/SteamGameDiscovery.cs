using System.Text;
using RimWise.Integration.Configuration;

namespace RimWise.Integration.Steam;

/// <summary>Reads Steam metadata for app 294100. It neither launches Steam nor writes library files.</summary>
public sealed class SteamGameDiscovery
{
    public SteamDiscoveryResult Discover(IEnumerable<string> steamRoots, CancellationToken cancellationToken = default,
        IEnumerable<string>? gameDirectories = null)
    {
        var libraries = new List<string>();
        var warnings = new List<string>();
        foreach (var root in steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeLocalDirectory(root);
            if (normalized is null || !Directory.Exists(normalized)) continue;
            AddUnique(libraries, normalized);
            foreach (var relative in new[] { Path.Combine("steamapps", "libraryfolders.vdf"), Path.Combine("config", "libraryfolders.vdf") })
            {
                var file = Path.Combine(normalized, relative);
                if (!File.Exists(file)) continue;
                try
                {
                    var folders = ReadMetadata(file).Block("libraryfolders");
                    if (folders is null) throw new InvalidDataException("缺少 libraryfolders。");
                    foreach (var candidate in folders.LibraryPaths())
                    {
                        var library = NormalizeLocalDirectory(candidate);
                        if (library is not null && Directory.Exists(library)) AddUnique(libraries, library);
                    }
                }
                catch (Exception ex) when (IsReadFailure(ex)) { warnings.Add($"无法读取 Steam 游戏库：{file}\n{ex.Message}"); }
            }
        }

        var workshop = libraries.Select(library => Path.Combine(library, "steamapps", "workshop", "content", "294100"))
            .Where(Directory.Exists).ToArray();
        var installations = new List<SteamGameInstallation>();
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = Path.Combine(library, "steamapps", "appmanifest_294100.acf");
            if (!File.Exists(manifest))
            {
                // A moved installation can lose its manifest. Probe only the known game folder.
                AddInstallation(Path.Combine(library, "steamapps", "common", "RimWorld"), library);
                continue;
            }
            try
            {
                var state = ReadMetadata(manifest).Block("AppState");
                if (state?.Value("appid") != "294100")
                    throw new InvalidDataException("游戏清单标识不匹配。");
                var installName = state.Value("installdir");
                if (string.IsNullOrWhiteSpace(installName) || installName is "." or ".."
                    || installName.IndexOfAny(['/', '\\', ':']) >= 0)
                    throw new InvalidDataException("游戏清单目录无效。");
                var gameDirectory = Path.Combine(library, "steamapps", "common", installName);
                if (!IsGameDirectory(gameDirectory))
                {
                    warnings.Add($"游戏目录不可用：{gameDirectory}\n需要 Data 文件夹和 RimWorld.exe、RimWorldWin64.exe 或 RimWorldWin.exe。");
                    continue;
                }
                AddInstallation(gameDirectory, library);
            }
            catch (Exception ex) when (IsReadFailure(ex)) { warnings.Add($"无法读取 RimWorld 安装信息：{manifest}\n{ex.Message}"); }
        }

        foreach (var hint in gameDirectories ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = NormalizeLocalDirectory(hint);
            if (game is null) continue;
            var common = Path.GetDirectoryName(game);
            var steamapps = common is null ? null : Path.GetDirectoryName(common);
            var library = common is not null && steamapps is not null
                && Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(steamapps).Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(steamapps) ?? string.Empty : string.Empty;
            AddInstallation(game, library);
        }
        return new SteamDiscoveryResult(installations.AsReadOnly(), Array.AsReadOnly(workshop), warnings.AsReadOnly());

        void AddInstallation(string game, string library)
        {
            if (!IsGameDirectory(game) || installations.Any(item => item.Paths.GameDirectory.Equals(game, StringComparison.OrdinalIgnoreCase))) return;
            var localMods = Path.Combine(game, "Mods");
            var matchingWorkshop = string.IsNullOrEmpty(library) ? string.Empty
                : Path.Combine(library, "steamapps", "workshop", "content", "294100");
            installations.Add(new SteamGameInstallation(library, new GamePaths
            {
                GameDirectory = game,
                LocalModsDirectory = Directory.Exists(localMods) ? localMods : string.Empty,
                WorkshopDirectory = Directory.Exists(matchingWorkshop) ? matchingWorkshop : workshop.FirstOrDefault() ?? string.Empty
            }));
        }
    }

    public static bool IsGameDirectory(string directory) => Directory.Exists(Path.Combine(directory, "Data"))
        && new[] { "RimWorld.exe", "RimWorldWin64.exe", "RimWorldWin.exe" }.Any(name => File.Exists(Path.Combine(directory, name)));

    private static KeyValueText ReadMetadata(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Steam 元数据过大。");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (text.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Steam 元数据过大。");
            text.Append(buffer, 0, count);
        }
        return KeyValueText.Parse(text.ToString());
    }

    private static string? NormalizeLocalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    private static void AddUnique(List<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException;
}
