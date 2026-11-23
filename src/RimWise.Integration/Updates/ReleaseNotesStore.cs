using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimWise.Integration.Updates;

public sealed record InstalledReleaseNotes(string Version, string Body)
{
    public static bool IsStableVersion(string? version) => version is { Length: >= 5 and <= 27 } && Regex.IsMatch(version,
        @"^\d{1,6}\.\d{1,6}\.\d{1,6}(?:\.\d{1,6})?$", RegexOptions.CultureInvariant)
        && System.Version.TryParse(version, out _);
    public void Validate()
    {
        if (!IsStableVersion(Version) || string.IsNullOrWhiteSpace(Body) || Body.Length > 65536)
            throw new InvalidDataException("更新日志无效。");
    }
}

/// <summary>Version-scoped release text and seen receipts survive installation replacement.</summary>
public sealed class ReleaseNotesStore(string userDataDirectory)
{
    private readonly string directory = Path.Combine(Path.GetFullPath(userDataDirectory), "Data", "ReleaseNotes");
    private string FilePath(string version, string extension)
    {
        if (!InstalledReleaseNotes.IsStableVersion(version)) throw new ArgumentException("版本号无效。", nameof(version));
        var parent = Path.GetDirectoryName(directory)!;
        EnsureNotLink(parent); EnsureNotLink(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, version + extension); EnsureNotLink(path); return path;
    }
    private static void EnsureNotLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("更新日志目录或文件不能是链接。");
    }
    public bool HasShown(string version)
    {
        var path = FilePath(version, ".seen");
        return File.Exists(path) && new FileInfo(path).Length <= 128 && File.ReadAllText(path) == version;
    }
    public FileStream Acquire(string version) => new(FilePath(version, ".lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);
    public void MarkShown(string version) => Write(version, ".seen", version);
    public void Cache(InstalledReleaseNotes notes)
    {
        notes.Validate(); Write(notes.Version, ".json", JsonSerializer.Serialize(notes));
    }
    public InstalledReleaseNotes? Read(string version)
    {
        var path = FilePath(version, ".json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("更新日志缓存过大。");
        var notes = JsonSerializer.Deserialize<InstalledReleaseNotes>(File.ReadAllText(path));
        notes?.Validate(); return notes?.Version == version ? notes : null;
    }
    private void Write(string version, string extension, string text)
    {
        var destination = FilePath(version, extension);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) { writer.Write(text); writer.Flush(); stream.Flush(true); }
            EnsureNotLink(destination); File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
