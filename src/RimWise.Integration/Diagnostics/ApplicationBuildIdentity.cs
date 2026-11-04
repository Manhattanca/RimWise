using System.Reflection;
using System.Security.Cryptography;

namespace RimWise.Integration.Diagnostics;

public sealed record ApplicationComponentIdentity(string Name, string? Version, string? ProductVersion,
    string LoadedModuleId, string? DiskSha256);

/// <summary>Identify the running code and disk files separately, including an update applied while open.</summary>
public static class ApplicationBuildIdentity
{
    public static string? ProductVersion(Assembly? assembly) => assembly?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

    public static ApplicationComponentIdentity[] Capture() => new[] { Assembly.GetEntryAssembly(),
            typeof(ApplicationBuildIdentity).Assembly, typeof(RimWise.Core.ModCatalogue).Assembly,
            typeof(RimWise.Formats.OrderFileDocument).Assembly }
        .OfType<Assembly>().Distinct().Select(Describe).ToArray();

    internal static ApplicationComponentIdentity Describe(Assembly assembly)
    {
        string? hash = null;
        try
        {
            using var file = new FileStream(assembly.Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            hash = Convert.ToHexString(SHA256.HashData(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        return new(assembly.GetName().Name ?? "unknown", assembly.GetName().Version?.ToString(),
            ProductVersion(assembly), assembly.ManifestModule.ModuleVersionId.ToString("D"), hash);
    }
}
