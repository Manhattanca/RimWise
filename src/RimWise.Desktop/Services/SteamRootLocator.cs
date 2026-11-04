using System.Security;
using System.IO;
using Microsoft.Win32;
using RimWise.Integration.Steam;

namespace RimWise.Desktop.Services;

/// <summary>Queries only known Steam registry values and standard install directories.</summary>
internal static class SteamRootLocator
{
    public static SteamDiscoveryResult Discover(CancellationToken token = default) =>
        new SteamGameDiscovery().Discover(Find(), token, FindGameDirectories());

    public static IReadOnlyList<string> Find()
    {
        var roots = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var steam = registry.OpenSubKey(@"Software\Valve\Steam", writable: false);
                    foreach (var name in new[] { "SteamPath", "InstallPath", "SteamExe" })
                    {
                        if (steam?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
                        {
                            value = value.Replace('/', Path.DirectorySeparatorChar).Trim('"');
                            if (name == "SteamExe") value = Path.GetDirectoryName(value) ?? string.Empty;
                            if (!string.IsNullOrEmpty(value)) roots.Add(value);
                        }
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var directory = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(directory)) roots.Add(System.IO.Path.Combine(directory, "Steam"));
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> FindGameDirectories()
    {
        var paths = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var game = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 294100", false);
                if (game?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                    paths.Add(location);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
