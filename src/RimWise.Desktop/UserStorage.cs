using RimWise.Integration.Configuration;

namespace RimWise.Desktop;

internal static class UserStorage
{
    internal static UserDataLocation? Location { get; set; }
    // Debug smoke runs are explicitly isolated from the real profile.
    internal static string DirectoryPath { get; set; } = null!;
}
