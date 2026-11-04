using System.Text.Json;
using RimWise.Integration.AI;
using RimWise.Integration.Diagnostics;

internal static class AiContextRetentionChecks
{
    internal static void Run(Action<string, Action> check)
    {
        static void Assert(bool condition) { if (!condition) throw new Exception("AI context / retention regression"); }
        static void Reject(Action action, string fragment = "")
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            { Assert(ex.Message.Contains(fragment)); return; }
            throw new Exception("Invalid input accepted");
        }
        var ids = new[] { "author.legacy mod", "中文.模组[补丁]", "author's.mod+extra", new string('a', 512) };
        AiRequest Request() => new(AiSettings.Default, null, "1.7", ids, ids, [ids[1]], []);
        check("Legacy and Unicode IDs survive outbound JSON without renaming", () =>
        {
            var messages = AiPrompt.Messages(Request());
            using var json = JsonDocument.Parse(messages[1].Content);
            Assert(json.RootElement.GetProperty("CurrentOrder").EnumerateArray().Select(v => v.GetString()).SequenceEqual(ids));
        });
        check("Malformed advisory relations do not block valid dependency evidence", () =>
        {
            var context = AiOutboundContext.Create(Request() with
            {
                ModRequirements = [new(ids[0], [ids[1], "", "bad\nid", "https://invalid.example"],
                    [ids[2]], null!, ["invalid/path"])]
            });
            Assert(context.ModRequirements[0].Dependencies.SequenceEqual([ids[1]])
                && context.ModRequirements[0].LoadBefore.SequenceEqual([ids[2]])
                && context.MetadataWarnings.Any(w => w.Field == "Dependencies" && w.OmittedItems == 3)
                && context.MetadataWarningCount == 3);
        });
        check("Bad unused installation ID cannot block current order diagnosis", () =>
        {
            var context = AiOutboundContext.Create(Request() with { InstalledMods = [.. ids, "bad\nmetadata"] });
            Assert(context.CurrentOrder.SequenceEqual(ids) && context.InstalledMods.SequenceEqual(ids)
                && context.MetadataWarnings.Single().Field == "InstalledMods");
        });
        check("Warnings bounded without sending malformed raw metadata", () =>
        {
            var context = AiOutboundContext.Create(Request() with
            { ModRequirements = Enumerable.Range(0, 150).Select(_ => new AiModRequirementFact(ids[0], ["bad\nid"], [], [], [])).ToArray() });
            Assert(context.MetadataWarnings.Count == 100 && context.MetadataWarningCount == 150);
            Assert(!JsonSerializer.Serialize(context).Contains("bad"));
        });
        check("Invalid active ID reports exact field and position rather than dropping mod", () =>
            Reject(() => AiOutboundContext.Create(Request() with { CurrentOrder = [ids[0], "bad\nid"] }), "CurrentOrder 第 2 项"));
        check("Overlong IDs and paths remain invalid", () =>
        {
            foreach (var id in new[] { new string('x', 513), "C:\\secret", "../other", " " })
                Reject(() => AiOutboundContext.Create(Request() with { CurrentOrder = [id] }));
        });
        var validator = new AiActionValidator();
        AiAction Move(string id) => new("move", id, 1, false, null, null);
        check("Punctuation IDs can be reordered only by exact known membership", () =>
        {
            var result = validator.Validate([Move(ids[0]), Move(ids[1]), Move(ids[2]), Move(ids[3]), Move("invented.mod")],
                AiPermissionLevel.Level1OrderAndRequiredDependencies, ids, ids, []);
            Assert(result.Accepted.Count == 4 && result.Rejected.Count == 1);
        });
        check("Known IDs do not authorize paths code modification or protected removal", () =>
        {
            var result = validator.Validate([Move(ids[0]) with { TargetPath = "C:\\file" },
                new("modifycode", ids[0], null, false, null, null), new("remove", ids[1], null, false, null, null)],
                AiPermissionLevel.Level2ModManagement, ids, ids, [ids[1]]);
            Assert(result.Accepted.Count == 0 && result.Rejected.Count == 3);
        });
        check("First-level direct removal remains blocked", () =>
            Assert(validator.Validate([new("remove", ids[0], null, false, null, null)],
                AiPermissionLevel.Level1OrderAndRequiredDependencies, ids, ids, []).IsBlocked));
        check("Punctuation IDs remain usable in runtime and targeted evidence", () =>
        {
            var context = AiOutboundContext.Create(Request() with
            {
                RuntimeContext = new([new("MissingCrossReference", ids[0], 1)]),
                LogContext = new([], [new("Example.Type", ids[1], "assembly-type")], false)
                {
                    Files = [new("active-definition", ids[0], 1, "ThingDef", "Example", "<Defs/>")],
                    Code = [new("e1", ids[2], "Example.Method", new string('A', 64), "return 1;", false)],
                    CodeStatus = "available",
                    Assemblies = [new("assembly-reference", ids[0], ids[1], "Example.Type")]
                }
            });
            Assert(context.Logs!.Owners.Count == 1 && context.Logs.Files.Count == 1
                && context.Logs.Code.Count == 1 && context.Logs.Assemblies.Count == 1);
        });
        check("Expanded IDs survive persisted checkpoint validation", () =>
        {
            var checkpoint = new AiTaskCheckpoint(AiTaskKind.Sorting, AiTaskPhase.Prepared, new string('A', 64), ids, ids, DateTimeOffset.UtcNow)
            { Actions = [Move(ids[3])] };
            checkpoint.Validate();
            JsonSerializer.Deserialize<AiTaskCheckpoint>(JsonSerializer.Serialize(checkpoint))!.Validate();
        });

        // Text-only fixtures under project build output. No binary fixtures or processes.
        var root = Path.Combine(AppContext.BaseDirectory, "日志 回归", Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var games = Path.Combine(profile, "Data", "GameTests");
        Directory.CreateDirectory(games);
        string Session(bool recovered, bool secondPending = false)
        {
            var session = Path.Combine(games, "game-" + Guid.NewGuid().ToString("N"));
            var attempt = Path.Combine(session, "attempt-01 测试数据");
            Directory.CreateDirectory(attempt);
            File.WriteAllText(Path.Combine(attempt, "launch.json"), "{}");
            File.WriteAllText(Path.Combine(attempt, "runtime-Player.log"), "中文 error evidence\n");
            if (recovered) File.WriteAllText(Path.Combine(attempt, "recovered.json"),
                JsonSerializer.Serialize(new { RecoveredUtc = DateTime.UtcNow, Matched = false, OldLogsAreNewEvidence = false }));
            if (secondPending)
            {
                var second = Path.Combine(session, "attempt-02 测试数据");
                Directory.CreateDirectory(second);
                File.WriteAllText(Path.Combine(second, "launch.json"), "{}");
            }
            return session;
        }
        string Log(string session) => Path.Combine(session, "attempt-01 测试数据", "runtime-Player.log");
        check("Recovered crash session logs become clearable without faking a successful test", () =>
        {
            var session = Session(true);
            TestDataRetention.MaintainOwnedRoot(games, true);
            Assert(!File.Exists(StoredEvidence.Resolve(Log(session))) && !File.Exists(Path.Combine(session, "finished.json"))
                && File.Exists(Path.Combine(Path.GetDirectoryName(Log(session))!, "recovered.json")));
        });
        check("Pending task evidence stays readable even after process recovery", () =>
        {
            var session = Session(true);
            TestDataRetention.MaintainOwnedRoot(games, true, new(StringComparer.OrdinalIgnoreCase) { session });
            Assert(StoredEvidence.ReadText(Log(session), 4096) == "中文 error evidence\n");
        });
        check("Partial recovery cannot clear unfinished session evidence", () =>
        {
            var session = Session(true, true);
            TestDataRetention.MaintainOwnedRoot(games, true);
            Assert(File.Exists(StoredEvidence.Resolve(Log(session))));
        });
        check("Corrupt recovery marker never authorizes deletion", () =>
        {
            var session = Session(true);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Log(session))!, "recovered.json"), "{}");
            TestDataRetention.MaintainOwnedRoot(games, true);
            Assert(File.Exists(StoredEvidence.Resolve(Log(session))));
        });
        check("Unfinished cache is disposable but logs and process records survive", () =>
        {
            var session = Session(false);
            var cache = Path.Combine(Path.GetDirectoryName(Log(session))!, "TextureCache");
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "texture.txt"), "disposable");
            TestDataRetention.MaintainOwnedRoot(games, true);
            Assert(!Directory.Exists(cache) && File.Exists(StoredEvidence.Resolve(Log(session)))
                && File.Exists(Path.Combine(Path.GetDirectoryName(Log(session))!, "launch.json")));
        });
        check("Storage summary accounts for non-exportable files without reading their contents", () =>
        {
            var file = Path.Combine(games, "other-data.bin");
            File.WriteAllText(file, "not diagnostic contents");
            var summary = TestDataRetention.InspectStorage(profile);
            Assert(summary.OtherBytes >= new FileInfo(file).Length
                && summary.TotalBytes == Directory.EnumerateFiles(games, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                && !JsonSerializer.Serialize(summary).Contains("not diagnostic contents"));
        });
        check("Repeated cleanup is idempotent and retains quota protections", () =>
        {
            TestDataRetention.MaintainOwnedRoot(games);
            var second = TestDataRetention.MaintainOwnedRoot(games);
            Assert(second.RemovedFiles == 0 && second.CompressedFiles == 0 && second.BeforeBytes == second.AfterBytes);
        });
        check("Interrupted task without saved feedback protects recovered session logs", () =>
        {
            var session = Session(true);
            var checkpoint = new AiTaskCheckpoint(AiTaskKind.Maintenance, AiTaskPhase.Testing,
                new string('A', 64), ids, ids, DateTimeOffset.UtcNow)
                { Attempts = 1, Limits = new(), DeadlineUtc = DateTimeOffset.UtcNow.AddHours(1) };
            using (var task = new AiTaskRecoveryStore(profile).Create(checkpoint)) { }
            TestDataRetention.MaintainTestRoot(games);
            Assert(TestDataRetention.InspectStorage(profile).ProtectedEvidenceBytes > 0
                && StoredEvidence.ReadText(Log(session), 4096) == "中文 error evidence\n");
        });
        check("Diagnostic export includes storage accounting without unrelated file contents", () =>
        {
            var destination = Path.Combine(root, "diagnostic.zip");
            DiagnosticExportService.Export(profile, destination);
            using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
            Assert(archive.GetEntry("test-storage.json") is not null);
            Assert(archive.Entries.All(e => !e.FullName.EndsWith("other-data.bin")));
            using var reader = new StreamReader(archive.GetEntry("test-storage.json")!.Open());
            using var json = JsonDocument.Parse(reader.ReadToEnd());
            Assert(json.RootElement.GetProperty("otherBytes").GetInt64() > 0);
        });
        check("Non-log data exceeding quota cannot block tests or evict useful evidence", () =>
        {
            var file = Path.Combine(games, "quota.dat");
            try
            {
                using (var data = File.Create(file)) data.SetLength(90_000_001);
                var session = Session(true);
                TestDataRetention.EnsureTestSpace(games);
                Assert(new FileInfo(file).Length == 90_000_001
                    && File.Exists(StoredEvidence.Resolve(Log(session))));
            }
            finally { File.Delete(file); }
        });
    }
}
