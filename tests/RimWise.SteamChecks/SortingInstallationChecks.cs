using System.Text.Json;
using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Configuration;
using RimWise.Integration.Game;
using RimWise.Integration.Scanning;

internal static class SortingInstallationChecks
{
    private static readonly GameVersion Version = new(1, 7);
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "sorting-installations", Guid.NewGuid().ToString("N"));
    private static void Assert(bool value, string message = "Sorting installation policy regression")
    { if (!value) throw new Exception(message); }

    private sealed class Fixture
    {
        internal readonly string Directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        internal GamePaths Paths => new() { LocalModsDirectory = Path.Combine(Directory, "Mods"),
            WorkshopDirectory = Path.Combine(Directory, "Workshop"), TargetVersionOverride = "1.7" };
        internal string Data => Path.Combine(Directory, "DataRoot");
        internal DuplicateCopyBackups Store => new(Data, Paths) { GameRunning = () => false };
        internal AutomaticCopySelection Selection => new(Data, Paths, Version) { GameRunning = () => false };
        internal ModInstallation Mod(string id, string folder, ModSource source = ModSource.Local,
            string release = "1.0", bool broken = false, string[]? dependencies = null)
        {
            var directory = Path.Combine(source == ModSource.Workshop ? Paths.WorkshopDirectory : Paths.LocalModsDirectory, folder);
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "About"));
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "Defs"));
            var dependencyXml = string.Join("", (dependencies ?? []).Select(d => $"<li><packageId>{d}</packageId></li>"));
            File.WriteAllText(Path.Combine(directory, "About", "About.xml"), $"<ModMetaData><name>样例 {id}</name>"
                + $"<packageId>{id}</packageId><modVersion>{release}</modVersion><supportedVersions><li>1.7</li></supportedVersions>"
                + $"<modDependencies>{dependencyXml}</modDependencies></ModMetaData>");
            File.WriteAllText(Path.Combine(directory, "Defs", "test.xml"), broken ? "<Defs>" : "<Defs/>");
            var metadata = new ModMetadataReader().Read(Path.Combine(directory, "About", "About.xml"), directory, Version);
            return new(directory, folder, source, source == ModSource.Workshop ? folder : "", metadata, []);
        }
        internal ModCatalogue Scan() { System.IO.Directory.CreateDirectory(Paths.WorkshopDirectory);
            return new ModScanner().Scan(Paths, inspectContent: false).Catalogue; }
    }

    internal static void Run(Action<string, Action> check)
    {
        check("Sorting drops confirmed missing entries, reports them and leaves survivors in original order", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.a", "a"); var b = f.Mod("test.b", "b");
            var selection = f.Selection;
            var result = selection.Prepare(new([a, b]), ["test.b", "gone.mod", "test.a"], default);
            Assert(result.Order.SequenceEqual(["test.b", "test.a"]) && result.RemovedIds.SequenceEqual(["gone.mod"]));
            Assert(selection.Messages.Any(m => m.Contains("未安装") && m.Contains("gone.mod")) && f.Store.List().Count == 0);
        });
        check("Missing active prerequisite removes its dependent closure without re-adding missing items", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.a", "a", dependencies: ["gone.mod"]);
            var b = f.Mod("test.b", "b", dependencies: ["test.a"]); var c = f.Mod("test.c", "c");
            var result = f.Selection.Prepare(new([a, b, c]), ["gone.mod", "test.a", "test.b", "test.c"], default);
            Assert(result.Order.SequenceEqual(["test.c"]) && result.RemovedIds.Length == 3
                && result.Messages.Count(m => m.Contains("连带")) == 2);
        });
        check("Official missing content is protected and never silently stripped", () =>
        {
            var f = new Fixture();
            try { f.Selection.Prepare(new([]), ["ludeon.rimworld"], default); throw new Exception("Removed official content"); }
            catch (InvalidDataException) { }
        });
        check("Workshop source wins over a higher local release, with actual reversible cross-root isolation", () =>
        {
            var f = new Fixture(); var local = f.Mod("test.copy", "local", release: "99.0");
            var workshop = f.Mod("test.copy", "100", ModSource.Workshop, "1.2");
            var selection = f.Selection;
            var result = selection.Prepare(new([local, workshop]), ["test.copy", "test.copy_steam"], default);
            var journal = f.Store.List().Single();
            Assert(result.Order.SequenceEqual(["test.copy"]) && journal.Selected == workshop.Directory
                && !System.IO.Directory.Exists(local.Directory) && System.IO.Directory.Exists(journal.Moves.Single().Backup)
                && f.Scan().Resolve("test.copy", Version).UniqueInstallation?.Directory == workshop.Directory);
            Assert(result.IdentifierMap["test.copy_steam"] == "test.copy" && selection.FallbackPackages.Contains("test.copy"));
            f.Store.Restore(journal.Id, default);
            Assert(System.IO.Directory.Exists(local.Directory) && System.IO.Directory.Exists(workshop.Directory));
        });
        check("Workshop numeric mod release outranks ID, game version and enumeration order", () =>
        {
            var f = new Fixture(); var old = f.Mod("test.copy", "101", ModSource.Workshop, "v1.9");
            var latest = f.Mod("test.copy", "999", ModSource.Workshop, "1.10.0");
            f.Selection.Prepare(new([old, latest]), ["test.copy"], default);
            Assert(f.Store.List().Single().Selected == latest.Directory);
        });
        check("Same latest version skips structurally broken A and selects usable B without game or AI", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "101", ModSource.Workshop, "2.0", true);
            var b = f.Mod("test.copy", "102", ModSource.Workshop, "2.0");
            var selection = f.Selection; var result = selection.Prepare(new([a, b]), ["test.copy"], default);
            Assert(result.Order.SequenceEqual(["test.copy"]) && f.Store.List().Single().Selected == b.Directory
                && selection.Messages.Any(m => m.Contains("跳过")));
        });
        check("All structurally unusable copies remove only the order and leave installation files intact", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "101", ModSource.Workshop, "2.0", true);
            var b = f.Mod("test.copy", "102", ModSource.Workshop, "2.0", true);
            var selection = f.Selection; var result = selection.Prepare(new([a, b]), ["test.copy"], default);
            Assert(result.Order.Length == 0 && result.RemovedIds.Single() == "test.copy"
                && System.IO.Directory.Exists(a.Directory) && System.IO.Directory.Exists(b.Directory)
                && f.Store.List().Count == 0 && selection.FallbackPackages.Count == 0);
        });
        check("A usable local copy remains a fallback when all Workshop candidates have file errors", () =>
        {
            var f = new Fixture(); var local = f.Mod("test.copy", "local");
            var bad = f.Mod("test.copy", "101", ModSource.Workshop, "99.0", true);
            var result = f.Selection.Prepare(new([bad, local]), ["test.copy_steam"], default);
            Assert(result.Order.SequenceEqual(["test.copy"]) && f.Store.List().Single().Selected == local.Directory);
        });
        check("Deep missing prerequisite chains are consumed iteratively without stack recursion or repeated whole-list passes", () =>
        {
            const int count = 10000;
            var mods = Enumerable.Range(0, count).Select(i => new ModInstallation(Path.Combine(Root, "deep", i.ToString()), i.ToString(),
                ModSource.Local, "", new("节点" + i, "test", "test." + i, ["1.7"], "", new([new("test." + (i + 1), "next")], [], [], []), false), [])).ToArray();
            var catalogue = new ModCatalogue(mods);
            var result = SortingInstallationPolicy.Normalize(catalogue, catalogue, ["test.0"], Version,
                new HashSet<string>(["test.10000"], StringComparer.OrdinalIgnoreCase));
            Assert(result.Order.Length == 0 && result.RemovedIds.Single() == "test.0");
        });
        check("Runtime A to B is finite, persistent and excludes only after every usable candidate fails", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "101", ModSource.Workshop, "2.0");
            var b = f.Mod("test.copy", "102", ModSource.Workshop, "2.0"); var other = f.Mod("test.other", "other");
            var selection = f.Selection; selection.Prepare(new([a, b, other]), ["test.copy", "test.other"], default);
            var result = selection.Retry(f.Scan(), ["test.copy", "test.other"], "test.copy", default);
            Assert(result.Order.SequenceEqual(["test.copy", "test.other"]) && f.Store.List().Single().Selected == b.Directory
                && f.Store.List().Single().RejectedDirectories.SequenceEqual([a.Directory]));
            // Process restart resumes the selected B, never repeats the already failed A.
            selection = f.Selection; selection.Prepare(f.Scan(), result.Order, default);
            Assert(f.Store.List().Single().Selected == b.Directory);
            result = selection.Retry(f.Scan(), result.Order, "test.copy", default);
            Assert(result.Order.SequenceEqual(["test.other"]) && f.Store.List().Single().RejectedDirectories.Length == 2
                && !selection.FallbackPackages.Contains("test.copy"));
            var again = f.Selection.Prepare(f.Scan(), ["test.copy", "test.other"], default);
            Assert(again.Order.SequenceEqual(["test.other"]) && f.Store.List().Single().RejectedDirectories.Length == 2);
            var record = f.Store.List().Single(); f.Store.Restore(record.Id, default);
            Assert(System.IO.Directory.Exists(a.Directory) && System.IO.Directory.Exists(b.Directory));
        });
        check("Priority duplicate dependency is selected without changing the first test's active order", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.child", "child", dependencies: ["test.dep"]);
            var old = f.Mod("test.dep", "old", release: "1.0"); var latest = f.Mod("test.dep", "latest", release: "2.0");
            var result = f.Selection.Prepare(new([a, old, latest]), ["test.child"], default);
            Assert(result.Order.SequenceEqual(["test.child"]) && f.Store.List().Single().Selected == latest.Directory);
        });
        check("Cancelled uncommitted selection restores mixed-source directories and the original steam alias", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "local"); var b = f.Mod("test.copy", "102", ModSource.Workshop);
            var selection = f.Selection; selection.Prepare(new([a, b]), ["test.copy_steam"], default);
            selection.Rollback(default);
            Assert(System.IO.Directory.Exists(a.Directory) && System.IO.Directory.Exists(b.Directory)
                && f.Scan().Resolve("test.copy_steam", Version).UniqueInstallation?.Directory == b.Directory && f.Store.List().Count == 0);
        });
        check("Failed comparison restores the selection that existed before this run, retaining new failure evidence", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "101", ModSource.Workshop); var b = f.Mod("test.copy", "102", ModSource.Workshop);
            f.Selection.Prepare(new([a, b]), ["test.copy"], default);
            var selection = f.Selection;
            selection.AttachForRecovery(f.Scan(), ["test.copy"], default);
            Assert(f.Store.List().Single().Selected == a.Directory);
            selection.Retry(f.Scan(), ["test.copy"], "test.copy", default);
            selection.Rollback(default);
            Assert(f.Store.List().Single().Selected == a.Directory && f.Store.List().Single().RejectedDirectories.Contains(a.Directory)
                && f.Scan().Resolve("test.copy", Version).UniqueInstallation?.Directory == a.Directory);
        });
        check("Read-only recovery attachment does not change the paid task's files", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "101", ModSource.Workshop); var b = f.Mod("test.copy", "102", ModSource.Workshop);
            f.Selection.Prepare(new([a, b]), ["test.copy"], default);
            var journal = JsonSerializer.Serialize(f.Store.List());
            var selection = f.Selection; selection.AttachForRecovery(f.Scan(), ["test.copy"], default);
            Assert(JsonSerializer.Serialize(f.Store.List()) == journal && selection.FallbackPackages.Contains("test.copy"));
        });
        check("Cancellation before preparation cannot move or delete a copy", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "a"); var b = f.Mod("test.copy", "b");
            try { f.Selection.Prepare(new([a, b]), ["test.copy"], new(true)); throw new Exception("Ignored cancel"); }
            catch (OperationCanceledException) { }
            Assert(System.IO.Directory.Exists(a.Directory) && System.IO.Directory.Exists(b.Directory) && f.Store.List().Count == 0);
        });
        check("Steam recreation blocks automatic fallback without overwriting the new folder", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "101", ModSource.Workshop);
            var b = f.Mod("test.copy", "102", ModSource.Workshop);
            var selection = f.Selection; selection.Prepare(new([a, b]), ["test.copy"], default);
            System.IO.Directory.CreateDirectory(b.Directory); File.WriteAllText(Path.Combine(b.Directory, "keep.txt"), "keep");
            try { selection.Retry(f.Scan(), ["test.copy"], "test.copy", default); throw new Exception("Overwrote Steam folder"); }
            catch (IOException) { }
            Assert(File.ReadAllText(Path.Combine(b.Directory, "keep.txt")) == "keep"
                && System.IO.Directory.Exists(f.Store.List().Single().Moves.Single().Backup));
        });
        check("Game-running guard remains enforced even for explicitly automatic selection", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "a"); var b = f.Mod("test.copy", "b");
            var selection = new AutomaticCopySelection(f.Data, f.Paths, Version) { GameRunning = () => true };
            try { selection.Prepare(new([a, b]), ["test.copy"], default); throw new Exception("Moved during game"); }
            catch (IOException) { }
            Assert(System.IO.Directory.Exists(a.Directory) && System.IO.Directory.Exists(b.Directory));
        });
        check("Automatic plans still reject paths outside both selected roots and official packages", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "a"); var b = f.Mod("test.copy", "b");
            foreach (var unsafeMod in new[] { b with { Directory = f.Directory }, b with { Source = ModSource.Game } })
            {
                try { f.Store.PlanAutomatic([a, unsafeMod], a); throw new Exception("Unsafe automatic plan"); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
        });
        check("Incomplete inspection is not proof of unusability", () =>
        {
            var f = new Fixture(); var a = f.Mod("test.copy", "a");
            var inspection = DuplicateCopyInspector.Inspect(a, Version, new HashSet<string>(), default) with { Limited = true };
            Assert(!inspection.HasFileErrors && DuplicateCopyInspector.Rank([inspection]).Single() == inspection);
        });
        check("Fallback remaining attempts include insertion control attempts and preserve token reservations", () =>
        {
            var state = new AiTaskCheckpoint(AiTaskKind.Maintenance, AiTaskPhase.Paused, new('A', 64), ["a"], ["a"], DateTimeOffset.UtcNow)
                { Attempts = 2, InsertionControlAttempts = 1, ReportedTokens = 100, ReservedTokens = 200 };
            Assert(DuplicateCopyRetry.Remaining(new(5, 1000), state) is { MaximumAttempts: 2, TokenBudget: 700 });
        });
        ControllerChecks(check);
    }

    private static void ControllerChecks(Action<string, Action> check)
    {
        string[] order = ["test.copy", "test.other"];
        var f = new Fixture(); var a = f.Mod(order[0], "a"); var b = f.Mod(order[1], "b"); var catalogue = new ModCatalogue([a, b]);
        var request = new AiRequest(AiSettings.Default with { Enabled = true, Model = "test", Endpoint = "http://127.0.0.1/v1/" },
            null, "1.7", order, order, [], []);
        MaintenanceGameReport Report(string owner) => new(true, ["synthetic error"]) { CanDiagnose = true,
            DiagnosticFacts = [new("GamePatchFailure", owner)] };
        MaintenanceResult Run(string name, MaintenanceGameReport report, Client client, string? resume = null)
            => new LevelOneMaintenance(client, new(Path.Combine(f.Directory, name)), new Session(report), resume,
                testOriginalFirst: true, copyFallbackPackages: new HashSet<string>([order[0]], StringComparer.OrdinalIgnoreCase))
                .RunAsync(request, catalogue, [], new() { AttemptLimitEnabled = false, TokenLimitEnabled = false }, null, default)
                .GetAwaiter().GetResult();
        check("Associated runtime error yields copy comparison only after one valid AI diagnosis, restoring original", () =>
        {
            var client = new Client(); var result = Run("related", Report(order[0]), client);
            Assert(result is { Outcome: MaintenanceOutcome.Stopped, CopyFallbackPackageId: "test.copy", OriginalRestored: true, ReportedTokens: 20 }
                && client.Calls == 1 && result.TrialExclusions.Count == 0, result.Message);
            // Resume an already paid response without another request.
            var restoredClient = new Client(); var restored = Run("related", Report(order[0]), restoredClient, result.TaskId);
            Assert(restored.CopyFallbackPackageId == order[0] && restoredClient.Calls == 0 && restored.ReportedTokens == 20);
        });
        check("Unrelated errors cannot trigger random duplicate fallback", () =>
        {
            var client = new Client(); var result = Run("unrelated", Report(order[1]), client);
            Assert(result.CopyFallbackPackageId is null && client.Calls == 1);
        });
        check("Service or structural response failure stops without switching or stripping copies", () =>
        {
            foreach (var failure in new[] { AiReplyFailure.ServiceFailure, AiReplyFailure.InvalidStructure })
            {
                var client = new Client { Failure = failure }; var result = Run("bad-ai-" + failure, Report(order[0]), client);
                Assert(result.CopyFallbackPackageId is null && result.ReplyFailure != AiReplyFailure.None && client.Calls == 1, result.Message);
            }
        });
        check("Clean verified run never switches copies or requests diagnosis", () =>
        {
            var client = new Client(); var result = Run("clean", new(true, []), client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.CopyFallbackPackageId is null && client.Calls == 0);
        });
        check("Incomplete diagnosis is not evidence to exhaust copies", () =>
        {
            var client = new Client(); var result = Run("incomplete", Report(order[0]) with { CanDiagnose = false }, client);
            Assert(result.CopyFallbackPackageId is null);
        });
    }

    private sealed class Client : IAiClient
    {
        internal int Calls; internal AiReplyFailure Failure;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new Exception("Unexpected preflight");
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new AiClientResult(Failure == AiReplyFailure.None, "synthetic", Failure == AiReplyFailure.None
                ? new(1, "No supported reorder", [], []) : null, null) { Usage = new(10, 10, 20), Failure = Failure });
        }
    }
    private sealed class Session(MaintenanceGameReport report) : ILevelOneGameSession
    {
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token) => Task.FromResult(report);
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token) => throw new Exception("Unexpected order mutation");
        public Task FinishAsync(bool acceptTestedOrder, CancellationToken token) => Task.CompletedTask;
    }
}
