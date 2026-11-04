using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace RimWise.Desktop.Services;

internal enum WorkshopOpenResult { Failed, Steam, Browser }

/// <summary>Opens player-selected locations through Windows, without building shell commands.</summary>
internal sealed class ModLocationOpener
{
    private readonly Action<string> open;
    private readonly Func<bool> steamAvailable;

    internal ModLocationOpener(Action<string>? open = null, Func<bool>? steamAvailable = null)
    {
        this.steamAvailable = steamAvailable ?? HasSteamProtocol;
        this.open = open ?? (target =>
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        });
    }

    internal WorkshopOpenResult OpenWorkshop(Uri uri)
    {
        const string prefix = "https://steamcommunity.com/sharedfiles/filedetails/?id=";
        var address = uri.IsAbsoluteUri ? uri.AbsoluteUri : "";
        if (!address.StartsWith(prefix, StringComparison.Ordinal)
            || !ulong.TryParse(address[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id == 0) return WorkshopOpenResult.Failed;

        // A dispatched protocol request cannot tell us whether Steam rendered the page.
        // Fall back only when Windows cannot dispatch it, avoiding opening both clients.
        if (steamAvailable() && TryOpen("steam://url/CommunityFilePage/" + id.ToString(CultureInfo.InvariantCulture)))
            return WorkshopOpenResult.Steam;
        return TryOpen(address) ? WorkshopOpenResult.Browser : WorkshopOpenResult.Failed;
    }

    internal bool OpenDirectory(string directory) => Path.IsPathFullyQualified(directory)
        && Directory.Exists(directory) && TryOpen(directory);

    private static bool HasSteamProtocol()
    {
        try
        {
            using var protocol = Registry.ClassesRoot.OpenSubKey("steam", writable: false);
            using var command = protocol?.OpenSubKey(@"shell\open\command", writable: false);
            return protocol?.GetValue("URL Protocol") is not null
                && command?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { return false; }
    }

    private bool TryOpen(string target)
    {
        try { open(target); return true; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        { return false; }
    }
}
