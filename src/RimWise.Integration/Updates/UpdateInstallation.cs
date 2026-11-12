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

    // Keep the update beside the running copy. For versioned installs, the installation
    // root is the directory above "versions"; portable copies use their own directory.
    public static string? MatchInstalledDirectory(string applicationDirectory, string? registeredDirectory)
    {
        var application = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory)));
        if (application.Parent?.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) == true &&
            application.Parent.Parent is { } versionedRoot)
            return versionedRoot.FullName;

        if (!string.IsNullOrWhiteSpace(registeredDirectory))
        {
            var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredDirectory));
            if (string.Equals(application.FullName, registered, StringComparison.OrdinalIgnoreCase))
                return registered;
        }

        return application.FullName;
    }
}
