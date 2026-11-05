using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Diagnostics;

/// <summary>Read-only profile discovery; never starts migration, maintenance, or the desktop app.</summary>
public static class EmergencyDiagnostics
{
    public static string ResolveProfile(string anchor)
    {
        var pointer = ScopedPaths.Child(anchor, "user-data-location.json");
        if (!File.Exists(pointer)) return ScopedPaths.Child(anchor, "UserData");
        using var stream = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 65536) throw new InvalidDataException("用户数据位置记录过大。");
        using var json = JsonDocument.Parse(stream);
        return ScopedPaths.Ordinary(json.RootElement.GetProperty("Current").GetString()!);
    }

    public static bool Export(string? profile, string destination, string applicationDirectory, string? discoveryFailure = null)
    {
        if (File.Exists(destination)) throw new IOException("诊断包已存在，请使用新文件名。");
        object snapshot = new { mode = "external", collectedUtc = DateTimeOffset.UtcNow,
            application = InstalledIdentity(applicationDirectory), processes = Processes(), discoveryFailure };
        string? failure = discoveryFailure;
        if (profile is not null)
        {
            try
            {
                DiagnosticExportService.Export(profile, destination, snapshot);
                return true;
            }
            catch (Exception ex) { failure = ex.GetType().Name; }
        }
        // Decryption/configuration failure: no raw logs or exception messages are safe to export.
        // Structural crash frames have no messages, arguments, source paths, or user text.
        var crashes = new List<JsonElement>();
        if (profile is not null)
        {
            try
            {
                var folder = ScopedPaths.Child(profile, "Data/Diagnostics");
                foreach (var path in Directory.EnumerateFiles(folder, "crash-*.json").OrderDescending().Take(20))
                {
                    try { crashes.Add(CrashEvidence.ReadStructure(path)); } catch { }
                }
            }
            catch { }
        }
        var target = ScopedPaths.Ordinary(Path.GetFullPath(destination));
        if (profile is not null && target.StartsWith(Path.GetFullPath(profile).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择用户数据目录之外的位置。");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                void Write(string name, object value)
                {
                    using var output = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open());
                    output.Write(JsonSerializer.Serialize(value));
                }
                Write("external-diagnostics.json", snapshot);
                Write("crash-structure.json", crashes);
                Write("manifest.json", new { format = "RimWiseEmergency/1", limited = true, failure,
                    omitted = new[] { "Raw logs and settings excluded because safe full export was unavailable." } });
                Write("token-summary.json", new { available = false, reason = "Full export unavailable; usage is unknown, not zero." });
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return false;
    }

    private static object InstalledIdentity(string directory)
    {
        try
        {
            var path = ScopedPaths.Child(directory, "RimWise.dll");
            using var file = File.OpenRead(path);
            return new { version = FileVersionInfo.GetVersionInfo(path).ProductVersion,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)) };
        }
        catch (Exception ex) { return new { unavailable = ex.GetType().Name }; }
    }

    private static object[] Processes()
    {
        var items = new List<object>();
        Process[] processes;
        try { processes = Process.GetProcessesByName("RimWise"); }
        catch (Exception ex) { return [new { unavailable = ex.GetType().Name }]; }
        foreach (var process in processes)
        {
            using (process)
            {
                try { items.Add(new { id = process.Id, responding = process.Responding,
                    cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
                    memoryBytes = process.WorkingSet64, startedUtc = process.StartTime.ToUniversalTime() }); }
                catch (Exception ex) { items.Add(new { id = process.Id, unavailable = ex.GetType().Name }); }
            }
        }
        return items.ToArray();
    }
}

/// <summary>Durable structural crash evidence. Never records exception messages or local variables.</summary>
public static class CrashEvidence
{
    public static void Write(string profile, Exception? error)
    {
        try
        {
            var directory = ScopedPaths.Child(profile, "Data/Diagnostics");
            Directory.CreateDirectory(directory);
            var chain = new List<object>();
            for (var current = error; current is not null && chain.Count < 12; current = current.InnerException)
                chain.Add(new { type = current.GetType().FullName, hresult = current.HResult,
                    frames = new StackTrace(current, false).GetFrames().Take(80).Select(frame => new {
                        type = frame.GetMethod()?.DeclaringType?.FullName, method = frame.GetMethod()?.Name,
                        assembly = frame.GetMethod()?.Module.Assembly.GetName().Name, offset = frame.GetILOffset() }).ToArray() });
            var path = ScopedPaths.Child(directory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(output, new { format = "RimWiseCrash/1", timestampUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId, version = ApplicationBuildIdentity.ProductVersion(Assembly.GetEntryAssembly()), chain });
            output.Flush(flushToDisk: true);
        }
        catch { } // Diagnostics cannot replace the original failure.
    }

    internal static JsonElement ReadStructure(string path)
    {
        ScopedPaths.Ordinary(path, true);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 256 * 1024) throw new InvalidDataException();
        using var document = JsonDocument.Parse(stream);
        // Accept only compiler identifier characters; arbitrary persisted text is not trusted.
        string? Identifier(JsonElement item, string name)
        {
            if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString()!;
            return text.Length <= 512 && text.All(c => char.IsAsciiLetterOrDigit(c) || "._+`<>[]".Contains(c)) ? text : null;
        }
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "RimWiseCrash/1") throw new InvalidDataException();
        return JsonSerializer.SerializeToElement(new {
            timestampUtc = root.GetProperty("timestampUtc").GetDateTimeOffset(), processId = root.GetProperty("processId").GetInt32(),
            chain = root.GetProperty("chain").EnumerateArray().Take(12).Select(e => new {
                type = Identifier(e, "type"), hresult = e.GetProperty("hresult").GetInt32(),
                frames = e.GetProperty("frames").EnumerateArray().Take(80).Select(f => new {
                    type = Identifier(f, "type"), method = Identifier(f, "method"), assembly = Identifier(f, "assembly"),
                    offset = f.GetProperty("offset").GetInt32() }).ToArray() }).ToArray() });
    }
}
