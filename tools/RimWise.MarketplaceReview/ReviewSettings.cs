using System.Text.Json;
using Microsoft.Win32;
using RimWise.Integration.AI;
using RimWise.Integration.Safety;

namespace RimWise.MarketplaceReview;

internal sealed class ReviewSettings
{
    internal const string StartupName = "RimWiseMarketplaceReview";
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    public NotificationState Notifications { get; set; } = new();
    public DateTimeOffset? LastCheckUtc { get; set; }
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimWiseMarketplaceReview");
    internal static string PathName => ScopedPaths.Child(DirectoryPath, "settings.json");
    internal DateTimeOffset NextCheckUtc => LastCheckUtc?.Add(PollInterval) ?? DateTimeOffset.MinValue;
    internal static ReviewSettings Load()
    {
        if (!File.Exists(PathName)) return new();
        if (new FileInfo(PathName).Length > 512 * 1024) throw new InvalidDataException("审核助手设置过大，请备份后检查设置文件。");
        var value = JsonSerializer.Deserialize<ReviewSettings>(File.ReadAllBytes(PathName)) ?? new();
        if (value.Notifications?.Heads is null) throw new InvalidDataException("审核助手通知记录损坏。");
        return value;
    }
    internal void Save()
    {
        ScopedPaths.Ordinary(DirectoryPath); Directory.CreateDirectory(DirectoryPath);
        var temporary = PathName + ".tmp-" + Guid.NewGuid().ToString("N");
        try { using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { file.Write(JsonSerializer.SerializeToUtf8Bytes(this));file.Flush(true); } File.Move(temporary, PathName, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return string.Equals(key?.GetValue(StartupName) as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
    }
    internal static string StartupCommand()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定审核助手路径。");
        if (path.Contains('"')) throw new InvalidDataException("审核助手路径无效。");
        return "\"" + path + "\" --background";
    }
    internal static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue(StartupName, StartupCommand(), RegistryValueKind.String);
        else key.DeleteValue(StartupName, false);
    }
}

/// <summary>Windows local credential store only; no AI services are constructed or contacted.</summary>
internal static class ReviewCredentials
{
    private const string Name = "RimWise/MarketplaceReview/GitHub";
    internal static string? Read() => new WindowsCredentialSecretStore().Read(Name);
    internal static void Write(string value) => new WindowsCredentialSecretStore().Write(Name, value);
    internal static void Delete() => new WindowsCredentialSecretStore().Delete(Name);
}
