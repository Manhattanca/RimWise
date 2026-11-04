using System.Diagnostics;

namespace RimWise.Integration.Updates;

/// <summary>Creates a fixed installer invocation; never runs arbitrary commands from release text.</summary>
public static class UpdateInstallation
{
    public static ProcessStartInfo CreateStartInfo(string installer, ReleaseDownload release, string? installationDirectory)
    {
        if (!release.IsInstaller || !Version.TryParse(release.Version, out var version) || version < new Version(0, 5, 2) ||
            release.FileName != $"RimWise-v{release.Version}-Setup.exe" ||
            !Path.GetFileName(installer).Equals(release.FileName, StringComparison.Ordinal))
            throw new InvalidDataException("此发布尚不支持一键更新，请等待提供新版安装包。");
        var info = new ProcessStartInfo(Path.GetFullPath(installer))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(installer))!
        };
        foreach (var argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/RIMWISEUPDATE=1" })
            info.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(installationDirectory))
            info.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory)));
        return info;
    }

    // Only an installed copy follows its registered root. A ZIP copy installs to the
    // normal managed location instead of mixing versioned files into the ZIP folder.
    public static string? MatchInstalledDirectory(string applicationDirectory, string? registeredDirectory)
    {
        if (string.IsNullOrWhiteSpace(registeredDirectory)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredDirectory));
        var application = new DirectoryInfo(Path.GetFullPath(applicationDirectory));
        return application.Parent?.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(application.Parent.Parent?.FullName, root, StringComparison.OrdinalIgnoreCase)
            ? root : null;
    }
}
