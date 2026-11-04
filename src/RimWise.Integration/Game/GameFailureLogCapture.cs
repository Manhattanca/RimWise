using System.Text;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using RimWise.Core;
using RimWise.Integration.Safety;

namespace RimWise.Integration.Game;

/// <summary>Read-only capture of fixed Unity log locations. A timestamp alone never
/// associates another application's crash with our test. Dumps remain on the machine.</summary>
internal sealed class GameFailureLogCapture
{
    private static readonly string[] CrashTextNames = ["error.log", "Player.log", "output_log.txt", "crash.log"];
    private readonly string executable, data;
    private readonly string[] roots, startupFiles;
    private readonly HashSet<string> existingCrashFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> existingFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime Write)> baseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private readonly List<string> observations = [];

    internal GameFailureLogCapture(string executable, string data, string? runtimeLog,
        IReadOnlyList<string>? crashRootsForChecks = null)
    {
        this.executable = executable;
        this.data = data;
        var product = runtimeLog is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(runtimeLog));
        var localLow = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!,
            "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios");
        roots = (crashRootsForChecks ?? new[] { Path.Combine(data, "Crashes"), Path.Combine(localLow, "Crashes"),
                Path.Combine(Path.GetTempPath(), "Ludeon Studios", "RimWorld by Ludeon Studios", "Crashes"),
                product is null ? Path.Combine(data, "Crashes") : Path.Combine(product, "Crashes") })
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        startupFiles = new[] { runtimeLog, product is null ? null : Path.Combine(product, "Player.log"),
            product is null ? null : Path.Combine(product, "Logs", "Player.log"),
            Path.Combine(localLow, "Player.log"), Path.Combine(localLow, "Logs", "Player.log"),
            Path.Combine(localLow, "output_log.txt"), Path.Combine(Path.GetDirectoryName(executable)!,
                Path.GetFileNameWithoutExtension(executable) + "_Data", "output_log.txt") }
            .Where(p => p is not null).Select(p => Path.GetFullPath(p!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in startupFiles) TryObserve(() => Remember(path));
        foreach (var root in roots)
            TryObserve(() =>
            {
                foreach (var folder in Folders(root)) existingFolders.Add(folder);
                foreach (var file in FlatCrashFiles(root)) existingCrashFiles.Add(file);
            });
    }

    internal async Task<IReadOnlyList<GameLogSnapshot>> CaptureAsync(int pid, string? nonce, ModCatalogue catalogue,
        bool waitForCrashReport, CancellationToken token, string? capturedStartupSource = null)
    {
        // Unity's native reporter can finish after the game exits. Give a correlated report
        // a short bounded chance to arrive; missing reports must never stall recovery.
        if (waitForCrashReport && !token.IsCancellationRequested)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                var found = false;
                var helper = ReadHelperFolder(pid, nonce);
                TryObserve(() => found = CrashFolders(helper).Any(folder => !existingFolders.Contains(folder)
                    && CrashTextNames.Select(name => ScopedPaths.Child(folder, name)).Where(Changed)
                        .Any(path => string.Equals(folder, helper, StringComparison.OrdinalIgnoreCase)
                            || HasAttemptCommand(PlayerLogReader.Read(path).Text))));
                try { await Task.Delay(found ? 250 : 200, token); }
                catch (OperationCanceledException) { break; }
                if (found) break;
            }
        }
        return Capture(pid, nonce, catalogue, capturedStartupSource);
    }

    internal IReadOnlyList<GameLogSnapshot> Capture(int pid, string? nonce, ModCatalogue catalogue,
        string? capturedStartupSource = null)
    {
        var logs = new List<GameLogSnapshot>();
        var artifacts = new List<object>();
        var helperFolder = ReadHelperFolder(pid, nonce);
        foreach (var (path, index) in startupFiles.Select((p, i) => (p, i)))
            TryObserve(() =>
            {
                if (string.Equals(path, capturedStartupSource, StringComparison.OrdinalIgnoreCase)) return;
                if (!Changed(path)) return;
                var snapshot = PlayerLogReader.Read(path, catalogue);
                if (HasAttemptCommand(snapshot.Text)) logs.Add(Copy(snapshot, "external-startup-" + index + ".log", "startup"));
            });
        foreach (var folder in CrashFolders(helperFolder))
            TryObserve(() =>
            {
                // Unity's API promises a unique startup folder. Existing reports are never reused.
                if (existingFolders.Contains(folder)) return;
                var text = CrashTextNames.Select(name => ScopedPaths.Child(folder, name)).Where(Changed)
                    .Select(path => PlayerLogReader.Read(path, catalogue)).ToArray();
                if (text.Length == 0) return;
                var identified = string.Equals(folder, helperFolder, StringComparison.OrdinalIgnoreCase)
                    || text.Any(log => HasAttemptCommand(log.Text));
                if (!identified) { observations.Add("UncorrelatedCrashIgnored"); return; }
                foreach (var snapshot in text)
                {
                    if (logs.Count >= 12)
                    {
                        observations.Add("CaptureFileLimit");
                        // Missing part of a correlated report must not authorize automatic repair.
                        logs[^1] = logs[^1] with { Truncated = true };
                        break;
                    }
                    logs.Add(Copy(snapshot, "crash-" + logs.Count + "-" + Path.GetFileName(snapshot.Path), "crash"));
                }
                foreach (var name in new[] { "crash.dmp", "error.dmp", "minidump.dmp" })
                {
                    var path = ScopedPaths.Child(folder, name);
                    if (Changed(path)) artifacts.Add(new { Path = path, Bytes = new FileInfo(path).Length });
                }
            });
        // Managed runtimes can write timestamped reports directly inside Crashes,
        // rather than Unity's unique per-crash directory. Accept only a new report
        // whose captured command belongs to this exact executable and savedata folder.
        foreach (var root in roots)
            TryObserve(() =>
            {
                foreach (var path in FlatCrashFiles(root).Where(path => !existingCrashFiles.Contains(path) && Changed(path)))
                {
                    if (logs.Count >= 12) { observations.Add("CaptureFileLimit"); logs[^1] = logs[^1] with { Truncated = true }; break; }
                    var snapshot = PlayerLogReader.Read(path, catalogue);
                    if (!HasAttemptCommand(snapshot.Text)) { observations.Add("UncorrelatedCrashIgnored"); continue; }
                    logs.Add(Copy(snapshot, "crash-" + logs.Count + "-" + Path.GetFileName(path), "crash"));
                }
            });
        OrdinaryGameRunner.WriteNew(ScopedPaths.Child(data, "diagnostic-sources.json"), new
        {
            StartupCaptured = logs.Any(l => l.Kind == "startup"),
            CrashCaptured = logs.Any(l => l.Kind == "crash"),
            CrashStatus = logs.Any(l => l.Kind == "crash") ? "Captured" : "NoCorrelatedReportAvailable",
            Files = logs.Select(l => new { l.Kind, l.Path, l.Truncated }), DumpsLocalOnly = artifacts,
            Observations = observations.Distinct().ToArray()
        });
        return logs;
    }

    private IReadOnlyList<string> CrashFolders(string? helper)
    {
        // The current helper's validated unique path takes priority even when there are
        // more historical reports than our bounded directory enumeration permits.
        var folders = new List<string>();
        if (helper is not null && Directory.Exists(helper)) folders.Add(helper);
        foreach (var root in roots) TryObserve(() => folders.AddRange(Folders(root)));
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private GameLogSnapshot Copy(GameLogSnapshot snapshot, string name, string kind)
    {
        var path = ScopedPaths.Child(data, name);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            using var input = new FileStream(ScopedPaths.Ordinary(snapshot.Path, true), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var complete = new LogSnapshotStream(input, input.Length);
            complete.CopyTo(output);
            output.Flush(true);
        }
        return snapshot with { Path = path, Kind = kind };
    }

    private string? ReadHelperFolder(int pid, string? nonce)
    {
        string? result = null;
        TryObserve(() =>
        {
            if (pid <= 0 || nonce is null) return;
            var path = ScopedPaths.Child(data, "workbench-log-locations.xml");
            if (!File.Exists(path)) return;
            using var input = new FileStream(ScopedPaths.Ordinary(path, true), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (input.Length is 0 or > 16384) return;
            using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 16384 });
            var element = XDocument.Load(xml).Root;
            if (element?.Name != "logLocations" || element.HasElements || (string?)element.Attribute("nonce") != nonce
                || !int.TryParse((string?)element.Attribute("pid"), out var found) || found != pid) return;
            var crash = (string?)element.Attribute("crash");
            if (string.IsNullOrWhiteSpace(crash)) return;
            crash = ScopedPaths.Ordinary(crash);
            // Treat the helper's path as untrusted input too; it cannot redirect the reader
            // into personal documents or other applications' directories.
            if (roots.Any(root => IsWithin(crash, root)) && !existingFolders.Contains(crash)) result = crash;
        });
        return result;
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(root.TrimEnd('\\', '/')
        + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> Folders(string root)
    {
        if (!Directory.Exists(root)) return [];
        root = ScopedPaths.Ordinary(root, true);
        // Common Unity versions create one report subdirectory, some have a second level.
        // Bound traversal; never recursively scan a Windows profile or the game tree.
        var first = Directory.EnumerateDirectories(root).Take(32).Select(p => ScopedPaths.Ordinary(p, true)).ToArray();
        return new[] { root }.Concat(first).Concat(first.SelectMany(p => Directory.EnumerateDirectories(p)
            .Take(8).Select(q => ScopedPaths.Ordinary(q, true)))).Take(96).ToArray();
    }

    private static IEnumerable<string> FlatCrashFiles(string root)
    {
        if (!Directory.Exists(root)) return [];
        ScopedPaths.Ordinary(root, true);
        return Directory.EnumerateFiles(root, "crash-*.txt", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(32).Select(file => ScopedPaths.Ordinary(file.FullName, true)).ToArray();
    }

    private void Remember(string path)
    {
        if (!File.Exists(path)) return;
        var file = new FileInfo(ScopedPaths.Ordinary(path, true));
        baseline[path] = (file.Length, file.LastWriteTimeUtc);
    }

    private bool Changed(string path)
    {
        if (!File.Exists(path)) return false;
        var file = new FileInfo(ScopedPaths.Ordinary(path, true));
        return file.LastWriteTimeUtc >= startedUtc.AddSeconds(-2)
            && (!baseline.TryGetValue(path, out var old) || old != (file.Length, file.LastWriteTimeUtc));
    }

    private bool HasAttemptCommand(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // The unique -savedatafolder must be part of a launch command, not just an arbitrary
        // path mentioned in an exception. Use the same parser as process ownership checks.
        foreach (var line in text.Split('\n').Take(8192))
        {
            var trimmed = line.Trim();
            foreach (var prefix in new[] { "Command:", "CommandLine:", "Command line:" })
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var command = trimmed[prefix.Length..].Trim();
                    if (SteamTestProcess.Matches(command, executable, data)
                        || SteamTestProcess.Matches(command, Path.ChangeExtension(executable, ".dll"), data)) return true;
                }
        }
        return false;
    }

    private void TryObserve(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or XmlException)
        { observations.Add("SourceUnavailable-" + ex.GetType().Name); }
    }
}
