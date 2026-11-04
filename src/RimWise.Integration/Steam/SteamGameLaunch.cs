using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Steam;

/// <summary>Routes Steam library installations through the installed client. Never falls back to the game EXE.</summary>
internal static class SteamGameLaunch
{
    internal const string AppId = "294100";

    internal static bool IsSteamInstallation(string gameDirectory)
    {
        var game = ScopedPaths.Ordinary(gameDirectory, true);
        var common = Directory.GetParent(game);
        var steamapps = common?.Parent;
        if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) != true
            || steamapps?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) != true) return false;
        var manifest = ScopedPaths.Child(steamapps.FullName, "appmanifest_294100.acf");
        // A missing or damaged manifest must not turn a Steam library into a direct-launch installation.
        if (!File.Exists(manifest)) return true;
        using var input = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("Steam 游戏清单过大。");
        using var reader = new StreamReader(input);
        var state = KeyValueText.Parse(reader.ReadToEnd()).Block("AppState");
        if (state?.Value("appid") != AppId || !string.Equals(state.Value("installdir"), Path.GetFileName(game), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam 清单与所选游戏目录不匹配，未启动游戏。");
        return true;
    }

    internal static ProcessStartInfo CreateRequest(string steamExecutable, IReadOnlyList<string> gameArguments)
    {
        steamExecutable = ScopedPaths.Ordinary(steamExecutable, true);
        if (!Path.GetFileName(steamExecutable).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam 启动程序无效。");
        var info = new ProcessStartInfo(steamExecutable)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(steamExecutable)!,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-applaunch");
        info.ArgumentList.Add(AppId);
        foreach (var argument in gameArguments) info.ArgumentList.Add(argument);
        // Steam keeps its normal Windows environment. The game and Steam are not AI agents.
        return info;
    }

    internal static ProcessStartInfo CreateRequest(IReadOnlyList<string> gameArguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return CreateRequest(FindClient(), gameArguments);
    }

    internal static void Request(ProcessStartInfo info)
    {
        using var client = Process.Start(info) ?? throw new IOException("无法向 Steam 提交启动请求。");
        // This is a Steam client/forwarder handle, never the owned game process. Do not terminate it.
    }

    [SupportedOSPlatform("windows")]
    private static string FindClient()
    {
        var candidates = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var steam = registry.OpenSubKey(@"Software\Valve\Steam", false);
                if (steam?.GetValue("SteamExe") is string exe) candidates.Add(exe.Replace('/', Path.DirectorySeparatorChar).Trim('"'));
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                    if (steam?.GetValue(name) is string root) candidates.Add(Path.Combine(root.Replace('/', Path.DirectorySeparatorChar).Trim('"'), "steam.exe"));
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "Steam", "steam.exe"));
        }
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Path.IsPathFullyQualified(path) && File.Exists(path) && Path.GetFileName(path).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
                return ScopedPaths.Ordinary(path, true);
        throw new FileNotFoundException("未找到 Steam 客户端。请先安装或启动 Steam；Steam 版不会改为直接运行游戏程序。");
    }
}
