using System.Text.Json;
using System.Security.Cryptography;

namespace RimWise.Integration.Configuration;

/// <summary>Writes only the application's fixed settings file; never writes selected game paths.</summary>
internal sealed class AppJsonFile<T>(string applicationDirectory, string fileName, long maximumBytes = 64 * 1024,
    bool keepPreviousVersions = true) where T : class
{
    private readonly string dataDirectory = Path.Combine(Path.GetFullPath(applicationDirectory), "Data");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };
    private byte[]? loadedHash;
    private bool loaded;
    public string SettingsPath => Path.Combine(dataDirectory, fileName + ".json");

    public T? Load()
    {
        if (!File.Exists(SettingsPath)) { loaded = true; loadedHash = null; return null; }
        RejectLinks(SettingsPath);
        using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes) throw new InvalidDataException("设置文件过大。");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            // The file can grow after the initial size check. Never read it without a bound.
            if (bytes.Length + read > maximumBytes) throw new InvalidDataException("设置文件过大。");
            bytes.Write(buffer, 0, read);
        }
        var content = bytes.ToArray();
        var settings = JsonSerializer.Deserialize<T>(content, JsonOptions)
            ?? throw new InvalidDataException("设置文件为空。");
        loadedHash = SHA256.HashData(content);
        loaded = true;
        return settings;
    }

    public void Save(T settings)
    {
        RejectLinks(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        RejectLinks(SettingsPath);
        using var fileLock = AppDataFileLock.Acquire(SettingsPath + ".lock");
        // Refuse stale read/modify/write rather than silently undoing another window's changes.
        if (loaded)
        {
            byte[]? currentHash = null;
            if (File.Exists(SettingsPath))
            {
                using var current = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (current.Length > maximumBytes) throw new InvalidDataException("设置文件过大。");
                currentHash = SHA256.HashData(current);
            }
            if (!(loadedHash is null ? currentHash is null : currentHash is not null && loadedHash.SequenceEqual(currentHash)))
                throw new IOException("此配置已被其他 RimWise 窗口更新。请重新打开设置或刷新后再保存，已有数据未被覆盖。");
        }
        var temporary = Path.Combine(dataDirectory, "." + fileName + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                if (stream.Length > maximumBytes) throw new InvalidDataException("设置文件过大。");
                stream.Flush(flushToDisk: true);
            }
            RejectLinks(SettingsPath);
            if (File.Exists(SettingsPath))
            {
                // Keep a unique previous version, including a malformed config that the user replaces.
                // Keep the old bytes first, then rename the staged file on the same volume.
                // File.Replace additionally merges ACL metadata, which is unnecessary here and
                // fails under some restricted directory permission profiles.
                if (keepPreviousVersions)
                {
                    var backup = Path.Combine(dataDirectory, fileName + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                        + "-" + Guid.NewGuid().ToString("N") + ".bak");
                    File.Copy(SettingsPath, backup, overwrite: false);
                }
                File.Move(temporary, SettingsPath, overwrite: true);
            }
            else File.Move(temporary, SettingsPath);
            using var saved = File.OpenRead(SettingsPath);
            loadedHash = SHA256.HashData(saved);
            loaded = true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("软件配置目录不支持符号链接或目录联接，请将软件放在普通目录。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

}
