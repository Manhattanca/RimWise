using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.Safety;
using RimWise.Integration.Steam;

namespace RimWise.Integration.Game;

/// <summary>Fixed test arguments. No command or environment values come from AI.</summary>
internal static class GameTestLaunch
{
    internal static ProcessStartInfo Create(string executable, string data)
    {
        executable = ScopedPaths.Ordinary(executable, true);
        data = ScopedPaths.Ordinary(data, true);
        if (SteamGameLaunch.IsSteamInstallation(Path.GetDirectoryName(executable)!))
            throw new InvalidOperationException("Steam 版只能经 Steam 启动，不能直接运行游戏程序。");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.Environment.Clear();
        // Preserve Windows identity/driver discovery instead of inventing an empty user profile.
        // Secrets and runtime injection variables are not part of this direct-install allowlist.
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "OS", "SystemDrive", "COMSPEC",
            "ProgramData", "ALLUSERSPROFILE", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
            "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "DriverData",
            "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "HOME",
            "USERNAME", "USERDOMAIN", "USERDNSDOMAIN", "COMPUTERNAME", "PROCESSOR_ARCHITECTURE",
            "PROCESSOR_IDENTIFIER", "NUMBER_OF_PROCESSORS", "TEMP", "TMP",
            "VK_DRIVER_FILES", "VK_ICD_FILENAMES", "VK_ADD_DRIVER_FILES", "VK_LAYER_PATH",
            "VK_ADD_LAYER_PATH", "VK_LOADER_DRIVERS_SELECT", "VK_LOADER_DRIVERS_DISABLE" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        foreach (var argument in Arguments(data)) info.ArgumentList.Add(argument);
        return info;
    }

    internal static string[] Arguments(string data) =>
        ["-savedatafolder=" + data, "-logFile", ScopedPaths.Child(data, "Player.log")];

    internal static void WriteConfiguration(string data, string version, IReadOnlyList<string> order,
        IReadOnlyList<string> knownExpansions, string? languageFolder = null)
    {
        var directory = ScopedPaths.Child(data, "Config");
        Directory.CreateDirectory(directory);
        var targetVersion = GameVersion.Parse(version) ?? throw new InvalidDataException("游戏版本未知，无法生成测试排序。");
        var launchOrder = new LoadOrder(order).ForGame(targetVersion);
        // Construct test-only settings. Only the validated language value is inherited; no other preferences are copied.
        new XDocument(new XElement("ModsConfigData", new XElement("version", version),
            // Modern package IDs are canonical lowercase in the game's configuration. Some
            // game/DLC activation checks are case-sensitive even though our resolver is not.
            new XElement("activeMods", launchOrder.PackageIds.Select(id => new XElement("li", id))),
            // Without this inventory the game treats installed DLC as newly discovered and can
            // append them, changing the tested order (including DLC deliberately left inactive).
            new XElement("knownExpansions", knownExpansions.Select(id => id.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal).Select(id => new XElement("li", id)))))
            .Save(ScopedPaths.Child(directory, "ModsConfig.xml"));
        new XDocument(new XElement("PrefsData", new XElement("screenWidth", 1280), new XElement("screenHeight", 900),
            new XElement("fullscreen", false), new XElement("volumeGame", 0), new XElement("volumeMusic", 0),
            new XElement("runInBackground", true), new XElement("logVerbose", true),
            languageFolder is null ? null : new XElement("langFolderName", languageFolder)))
            .Save(ScopedPaths.Child(directory, "Prefs.xml"));
    }
    internal static string? ReadLanguage(string personalConfigPath)
    {
        var directory = Path.GetDirectoryName(ScopedPaths.Ordinary(personalConfigPath, true))!;
        var path = ScopedPaths.Child(directory, "Prefs.xml");
        if (!File.Exists(path)) return null;
        path = ScopedPaths.Ordinary(path, true);
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != "PrefsData") throw new InvalidDataException("游戏语言配置结构无效。");
        var values = document.Root.Elements("langFolderName").ToArray();
        if (values.Length > 1) throw new InvalidDataException("游戏语言配置重复。");
        var language = values.SingleOrDefault()?.Value.Trim();
        if (string.IsNullOrEmpty(language)) return null;
        if (language.Length > 128 || language is "." or ".." || language.Any(char.IsControl)
            || language.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("游戏语言名称无效。");
        return language;
    }

}
