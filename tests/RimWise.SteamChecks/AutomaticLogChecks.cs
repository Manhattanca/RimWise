using System.IO.Compression;
using System.Text.Json;
using RimWise.Integration.AI;
using RimWise.Integration.Diagnostics;

internal static class AutomaticLogChecks
{
    internal static void Run(Action<string, Action> check)
    {
        static void Assert(bool value) { if (!value) throw new Exception("Automatic log regression"); }
        var root = Path.Combine(AppContext.BaseDirectory, "自动日志 [回归]", Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(profile);
        var store = new AutomaticSortingLogStore(profile);
        check("Log export defaults off and Unicode destination persists separately from game paths", () =>
        {
            Assert(!store.LoadSettings().AutoSave);
            var settings = store.SaveSettings(new(true, Path.Combine(root, "日志 输出")));
            Assert(new AutomaticSortingLogStore(profile).LoadSettings() == settings);
            Assert(!Directory.Exists(settings.Directory));
            foreach (var path in new[] { "relative", profile, Path.Combine(profile, "Data", "Exports") })
            {
                try { store.SaveSettings(new(true, path)); }
                catch (ArgumentException) { continue; }
                throw new Exception("Invalid export path accepted");
            }
            Assert(store.LoadSettings() == settings);
        });
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var gameRoot = Path.Combine(profile, "Data", "GameTests");
        string Session(string text)
        {
            var path = Path.Combine(gameRoot, "game-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(path, "attempt-01 测试数据"));
            File.WriteAllText(Path.Combine(path, "finished.json"), "{\"ProcessStopped\":true}");
            File.WriteAllText(Path.Combine(path, "attempt-01 测试数据", "runtime-Player.log"), text);
            return path;
        }
        var earlier = Session("OLDER_GAME_SENTINEL");
        var recent = Session("RECENT_GAME_SENTINEL SyntheticLogSecret629481");
        foreach (var name in new[] { "intended-ModsConfig.xml", "observed-ModsConfig.xml" })
            File.WriteAllText(Path.Combine(recent, "attempt-01 测试数据", name),
                "<ModsConfigData><activeMods><li>test.reset.evidence</li></activeMods></ModsConfigData>");
        string TaskId(DateTimeOffset time)
        {
            using var task = new AiTaskRecoveryStore(profile).Create(new(AiTaskKind.Sorting, AiTaskPhase.Completed,
                new string('A', 64), [], [], time));
            return task.Id;
        }
        var oldId = TaskId(started.AddDays(-1));
        var recentId = TaskId(started.AddSeconds(1));
        var run = new AutomaticLogRun(started, DateTimeOffset.UtcNow, [Path.GetFileName(recent)], [recentId]);
        check("Last-run index survives restart and keeps latest evidence clearable only after replacement", () =>
        {
            store.SaveRun(run);
            Assert(new AutomaticSortingLogStore(profile).LoadLastRun()!.Sessions.SequenceEqual(run.Sessions));
            TestDataRetention.MaintainTestRoot(gameRoot);
            Assert(StoredEvidence.ReadText(Path.Combine(recent, "attempt-01 测试数据", "runtime-Player.log"), 4096).Contains("RECENT_GAME"));
            Assert(TestDataRetention.InspectStorage(profile).ProtectedEvidenceBytes > 0);
            Assert(File.Exists(Path.Combine(recent, "attempt-01 测试数据", "intended-ModsConfig.xml.gz")));
        });
        check("Last-run ZIP selects its task and sessions, filters UI time, redacts keys and keeps token summary", () =>
        {
            new LocalEncryptedSecretStore(profile).Write(AiSettings.Default.SecretName, "SyntheticLogSecret629481");
            var ui = Path.Combine(profile, "Data", "Diagnostics");
            Directory.CreateDirectory(ui);
            File.WriteAllLines(Path.Combine(ui, "ui-example.jsonl"), new[]
            {
                JsonSerializer.Serialize(new { timestampUtc = started.AddDays(-1), detail = "OLDER_UI_SENTINEL" }),
                JsonSerializer.Serialize(new { timestampUtc = started.AddSeconds(1), detail = "RECENT_UI_SENTINEL SyntheticLogSecret629481" })
            });
            var target = Path.Combine(root, "日志 包.zip");
            DiagnosticExportService.Export(profile, target, run: run);
            using var zip = ZipFile.OpenRead(target);
            Assert(zip.GetEntry("token-summary.json") is not null && zip.GetEntry("automatic-run.json") is not null);
            Assert(zip.GetEntry("tasks/" + recentId + ".json") is not null && zip.GetEntry("tasks/" + oldId + ".json") is null);
            foreach (var name in new[] { "intended-ModsConfig.xml", "observed-ModsConfig.xml" })
                Assert(zip.Entries.Any(entry => entry.FullName.EndsWith("/" + name, StringComparison.Ordinal)));
            var content = string.Join('\n', zip.Entries.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
            Assert(content.Contains("RECENT_GAME_SENTINEL") && content.Contains("RECENT_UI_SENTINEL")
                && !content.Contains("OLDER_GAME_SENTINEL") && !content.Contains("OLDER_UI_SENTINEL")
                && !content.Contains("SyntheticLogSecret629481"));
        });
        check("Non-log files do not evict completed logs while reproducible cache still obeys budget", () =>
        {
            var other = Path.Combine(gameRoot, "unknown-output.dat");
            var cache = Path.Combine(gameRoot, "AnalysisCache", "large.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            try
            {
                using (var file = File.Create(other)) file.SetLength(100_000_001);
                TestDataRetention.EnsureTestSpace(gameRoot);
                Assert(File.Exists(StoredEvidence.Resolve(Path.Combine(earlier, "attempt-01 测试数据", "runtime-Player.log"))));
                using (var file = File.Create(cache)) file.SetLength(90_000_001);
                TestDataRetention.EnsureTestSpace(gameRoot);
                Assert(!File.Exists(cache) && new FileInfo(other).Length == 100_000_001);
                Assert(File.Exists(StoredEvidence.Resolve(Path.Combine(recent, "attempt-01 测试数据", "runtime-Player.log"))));
            }
            finally { File.Delete(other); File.Delete(cache); }
        });
        check("Delete all removes last completed evidence but preserves unfinished sessions and token records", () =>
        {
            var unfinished = Session("PENDING_EVIDENCE");
            File.Delete(Path.Combine(unfinished, "finished.json"));
            var unknown = Path.Combine(gameRoot, "user-note.txt");
            File.WriteAllText(unknown, "keep");
            using (var lease = new FileStream(Path.Combine(gameRoot, "ordinary-game-test.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert(TestDataRetention.MaintainProfile(profile, true, false).Busy);
            var result = TestDataRetention.MaintainProfile(profile, clearLogs: true, preserveLastRun: false);
            Assert(!result.Busy && !File.Exists(StoredEvidence.Resolve(Path.Combine(recent, "attempt-01 测试数据", "runtime-Player.log"))));
            Assert(File.Exists(StoredEvidence.Resolve(Path.Combine(unfinished, "attempt-01 测试数据", "runtime-Player.log"))));
            Assert(File.ReadAllText(unknown) == "keep" && new AiTaskRecoveryStore(profile).InspectUsage().Any(t => t.Id == recentId));
            Assert(File.Exists(Path.Combine(recent, "finished.json")));
            Assert(!File.Exists(StoredEvidence.Resolve(Path.Combine(recent, "attempt-01 测试数据", "intended-ModsConfig.xml"))));
        });
        check("Export cleanup deletes verified sorting ZIPs only, preserving unrelated files and subfolders", () =>
        {
            var destination = store.LoadSettings().Directory;
            Directory.CreateDirectory(destination);
            var valid = Path.Combine(destination, "RimWise-sorting-valid.zip");
            DiagnosticExportService.Export(profile, valid, run: run);
            File.Copy(valid, Path.Combine(destination, "personal.zip"));
            var nested = Path.Combine(destination, "nested");
            Directory.CreateDirectory(nested);
            File.Copy(valid, Path.Combine(nested, "RimWise-sorting-nested.zip"));
            File.WriteAllText(Path.Combine(destination, "RimWise-sorting-invalid.zip"), "not an archive");
            var locked = Path.Combine(destination, "RimWise-sorting-locked.zip");
            File.Copy(valid, locked);
            using (var lease = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = store.DeleteExportedLogs();
                Assert(result.Removed == 1 && result.Skipped == 2 && !File.Exists(valid));
            }
            Assert(File.Exists(Path.Combine(destination, "personal.zip")) && File.Exists(Path.Combine(nested, "RimWise-sorting-nested.zip")));
            Assert(store.DeleteExportedLogs() == (1, 1));
        });
    }
}
