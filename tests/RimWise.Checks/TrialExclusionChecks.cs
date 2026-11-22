using RimWise.Core;
using RimWise.Integration.AI;
using RimWise.Integration.Game;
using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>Controlled red-error experiments with synthetic game and provider adapters only.</summary>
internal static class TrialExclusionChecks
{
    private static readonly GameVersion Version = new(1, 7);
    private static readonly string[] Original = ["trial.a", "trial.b", "trial.c"];

    internal static void Run(string scratch, Action<string, Action> check)
    {
        string Root() => Path.Combine(scratch, "试排除 中文 &", Guid.NewGuid().ToString("N"));
        ModInstallation Mod(string id, string[]? deps = null) => new(Path.Combine(scratch, id), id, ModSource.Local, "",
            new(id, "fixture", id, ["1.7"], "", new((deps ?? []).Select(d => new ModDependency(d, d)).ToArray(), [], [], []), false), []);
        ModCatalogue catalogue = new(Original.Select(id => Mod(id)).Append(Mod("inactive.mod")).ToArray());
        AiRequest Request() => new(AiSettings.Default with { Enabled = true, Model = "trial-fixture" }, null,
            "1.7", Original, Original, [], []);
        MaintenanceResult Run(Session game, IAiClient client, string? root = null, string? resume = null, CancellationToken token = default,
            MaintenanceLimits? limits = null, string[]? retained = null) => new LevelOneMaintenance(client, new(root ?? Root()), game, resume)
                .RunAsync(Request() with { RetainedMods = retained ?? [] }, catalogue, [], limits ?? new(), null, token).GetAwaiter().GetResult();

        ModInstallation Unreadable(string id, string[]? deps = null) => Mod(id, deps) with
            { Diagnostics = [new("MetadataUnreadable", DiagnosticSeverity.Warning, "synthetic unreadable")] };
        ModCatalogue ReadCatalogue(params ModInstallation[] mods) => new(mods);
        IReadOnlyList<TrialExclusion> ReadCandidates(ModCatalogue c, MaintenanceGameReport report,
            string[]? order = null, string[]? retained = null) => MetadataReadFailureTrials.Candidates(
                report, c, order ?? Original, retained ?? [], Version, 1);
        MaintenanceResult ReadRun(ModCatalogue c, Session game, Client client, bool enabled = true,
            string? root = null, string? resume = null, MaintenanceLimits? limits = null) =>
            new LevelOneMaintenance(client, new(root ?? Root()), game, resume, testOriginalFirst: true)
                .RunAsync(Request(), c, [], limits ?? new() { ReadFailureTrialsEnabled = enabled }, null, default)
                .GetAwaiter().GetResult();

        check("read failure marker defaults preserve old JSON and authorization", () =>
        {
            var trial = TrialExclusionPlanning.Select(Error("trial.a"), catalogue, Original, [], Version, 1)!;
            Assert(!new MaintenanceLimits().ReadFailureTrialsEnabled && !trial.ReadFailureRemoval);
            Assert(!JsonSerializer.Serialize(trial).Contains("ReadFailureRemoval"));
            Assert(!JsonSerializer.Deserialize<MaintenanceLimits>("{}")!.ReadFailureTrialsEnabled);
            Assert(JsonSerializer.Deserialize<TrialExclusion>(JsonSerializer.Serialize(trial)) is { ReadFailureRemoval: false });
        });
        check("read failure requires this round attributed red facts and complete capture", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b"), Mod("trial.c"));
            foreach (var report in new[] { new MaintenanceGameReport(true, []), Error("trial.b"),
                Error("trial.a") with { Errors = [] }, Error("trial.a") with { DiagnosticFacts = [] },
                Error("trial.a") with { CanDiagnose = false },
                Error("trial.a") with { StartupFailure = new(new("ProbeCompleted", false, true, [new("GraphicsDriverUnavailable")]), []) },
                Error("trial.a") with { StartupFailure = new(new("ProbeCompleted", false, true, [new("TestOrderMismatch")]), []) } })
                Assert(ReadCandidates(c, report).Count == 0);
            Assert(ReadCandidates(c, Error("trial.a")).Single() is { ReadFailureRemoval: true, SelectedId: "trial.a" });
            Assert(ReadCandidates(catalogue, Error("trial.a")).Count == 0);
        });
        check("read failure full original precedes single directed unpaid retest", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b"), Mod("trial.c"));
            var game = new Session(c, Original) { Report = ids => ids.Contains("trial.a") ? Error("trial.a") : new(true, []) };
            var client = new Client(); var result = ReadRun(c, game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 0 && result.Attempts == 2
                && result.TrialExclusions.Single().ReadFailureRemoval && result.OriginalRestored && !game.Accepted);
            Assert(!result.Message.Contains("需由玩家确认") && result.Message.Contains("按已记录授权结算"));
            Assert(game.Tested[0].SequenceEqual(Original) && game.Tested[1].SequenceEqual(["trial.b", "trial.c"]));
            var trial = result.TrialExclusions.Single();
            Assert(TrialExclusionPlanning.Plan(Original, ["trial.b", "trial.c"], c, [], Version, [], [trial])
                .Notices.Single().Contains(MetadataReadFailureTrials.ReadFailureRemovalReason));
            Throws(() => TrialExclusionPlanning.Replay(catalogue, Original, [], Version, [trial]));
        });
        check("two read failures follow each round attribution without batching or paid AI", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Unreadable("trial.b"), Mod("trial.c"));
            var game = new Session(c, Original) { Report = ids => ids.Contains("trial.a") ? Error("trial.a")
                : ids.Contains("trial.b") ? Error("trial.b") : new(true, []) };
            var client = new Client(); var result = ReadRun(c, game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 0 && result.Attempts == 3
                && result.TrialExclusions.Count == 2 && result.TrialExclusions.All(t => t.ReadFailureRemoval));
            Assert(game.Tested[0].SequenceEqual(Original) && game.Tested[1].SequenceEqual(["trial.b", "trial.c"])
                && game.Tested[2].SequenceEqual(["trial.c"]));
            Assert(result.TrialExclusions[0].RemovedIds.SequenceEqual(["trial.a"])
                && result.TrialExclusions[1].RemovedIds.SequenceEqual(["trial.b"]));
            var first = result.TrialExclusions[0];
            Assert(TrialExclusionPlanning.Plan(Original, ["trial.b", "trial.c"], c, [], Version, [], [first])
                .Notices.Contains(MetadataReadFailureTrials.UnknownDependenciesNotice));
            Assert(ReadCandidates(c, Error("trial.a"), retained: ["trial.b"]).Count == 0);
        });
        foreach (var readFailureFirst in new[] { false, true })
            check("mixed retained trials " + (readFailureFirst ? "read failure then ordinary" : "ordinary then read failure")
                + " restore only ordinary dependents using complete historical authority", () =>
            {
                string[] original = ["trial.a", "trial.a.child", "trial.b", "trial.b.child", "trial.c", "trial.d"];
                var c = ReadCatalogue(Mod("trial.a"), Mod("trial.a.child", ["trial.a"]),
                    Unreadable("trial.b"), Mod("trial.b.child", ["trial.b"]), Mod("trial.c"), Mod("trial.d"));
                var current = original.ToArray();
                var history = new List<TrialExclusion>();
                for (var index = 0; index < 2; index++)
                {
                    var readFailure = (index == 0) == readFailureFirst;
                    var trial = readFailure
                        ? MetadataReadFailureTrials.Candidates(Error("trial.b"), c, current, [], Version, index + 1).Single()
                        : TrialExclusionPlanning.Candidates(Error("trial.a"), c, current, [], Version, index + 1).Single();
                    Assert(trial.ReadFailureRemoval == readFailure && trial.TestedOrder.SequenceEqual(current));
                    Assert(trial.RemovedIds.SequenceEqual(readFailure
                        ? new[] { "trial.b", "trial.b.child" } : ["trial.a", "trial.a.child"]));
                    history.Add(trial);
                    current = current.Except(trial.RemovedIds, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                Assert(TrialExclusionPlanning.Replay(c, original, [], Version, history).SequenceEqual(["trial.c", "trial.d"]));
                // Keep a valid survivor reorder too. Dropping a historical trial before Replay
                // breaks the second TestedOrder; reinserting permanent rows exhausts the queue.
                string[] tested = ["trial.d", "trial.c"];
                Assert(AiOrderPlanner.ConfirmedTrialOrder(original, tested, c, [], Version, [], [], history, true).CanApply);
                var result = AiOrderPlanner.RetainedTrialOrder(original, tested, c, [], Version, [], [], history);
                Assert(result.CanApply && result.Rejected.Count == 0 && result.Before.SequenceEqual(original),
                    string.Join("; ", result.Rejected));
                Assert(result.After.SequenceEqual(["trial.a", "trial.a.child", "trial.d", "trial.c"]));
                Assert(!result.After.Contains("trial.b") && !result.After.Contains("trial.b.child")
                    && result.After.Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.After.Count);
                Assert(result.Notices.Single().Contains(MetadataReadFailureTrials.ReadFailureRemovalReason));
                Assert(history.Count == 2 && history[0].TestedOrder.SequenceEqual(original)
                    && history[1].TestedOrder.SequenceEqual(original.Except(history[0].RemovedIds, StringComparer.OrdinalIgnoreCase)));
            });
        foreach (var readFailureFirst in new[] { false, true })
            check("mixed trial settlement wording " + (readFailureFirst ? "read failure first" : "ordinary first"), () =>
            {
                var c = ReadCatalogue(Mod("trial.a"), Unreadable("trial.b"), Mod("trial.c"));
                var game = new Session(c, Original) { Report = ids =>
                {
                    var first = readFailureFirst ? "trial.b" : "trial.a";
                    var second = readFailureFirst ? "trial.a" : "trial.b";
                    return ids.Contains(first) ? Error(first) : ids.Contains(second) ? Error(second) : new(true, []);
                } };
                var client = new Client();
                var result = new LevelOneMaintenance(client, new(Root()), game, testOriginalFirst: false)
                    .RunAsync(Request(), c, [], new() { ReadFailureTrialsEnabled = true }, null, default)
                    .GetAwaiter().GetResult();
                Assert(result.Outcome == MaintenanceOutcome.Passed && result.TrialExclusions.Count == 2
                    && result.TrialExclusions[0].ReadFailureRemoval == readFailureFirst
                    && result.TrialExclusions[1].ReadFailureRemoval != readFailureFirst && client.Calls == 1, result.Message);
                Assert(result.Message.Contains("需由玩家确认普通试排除项是否移出")
                    && result.Message.Contains("读取失败移出项按已记录授权结算"), result.Message);
            });
        check("read failure stopped settlement does not ask for ordinary trial confirmation", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b"), Mod("trial.c"));
            var game = new Session(c, Original) { Report = ids => ids.Contains("trial.a") ? Error("trial.a") : Error("trial.c") };
            var result = ReadRun(c, game, new Client(), limits: new(MaximumAttempts: 2) { ReadFailureTrialsEnabled = true });
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.TrialExclusions.Single().ReadFailureRemoval
                && result.Message.Contains("剩余组合尚未通过验证") && result.Message.Contains("按已记录授权结算")
                && !result.Message.Contains("需由玩家确认"), result.Message);
        });
        check("read failure no red errors stops before AI or removal", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b"), Mod("trial.c"));
            var game = new Session(c, Original); var client = new Client();
            var result = ReadRun(c, game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 0
                && game.Tested.Single().SequenceEqual(Original) && result.TrialExclusions.Count == 0);
        });
        check("read failure invalid unchanged remainder falls through to AI without local sorting", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b", ["trial.c"]), Mod("trial.c"));
            var game = new Session(c, Original) { Report = _ => Error("trial.a") }; var client = new Client();
            var result = ReadRun(c, game, client);
            Assert(client.Calls == 1 && result.TrialExclusions.Count == 0 && game.Tested.Count == 1
                && game.Tested[0].SequenceEqual(Original) && result.OriginalRestored, result.Message);
        });
        check("unrelated unreadable item does not block valid AI reorder", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b"), Mod("trial.c"));
            var game = new Session(c, Original) { Report = ids => ids.SequenceEqual(Original) ? Error("trial.b") : new(true, []) };
            var client = new Client { Actions = [new("reorder", "trial.c", 2, false, null, null)] };
            var result = ReadRun(c, game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 1 && result.TrialExclusions.Count == 0
                && game.Tested.Count == 2 && game.Tested[1].SequenceEqual(["trial.a", "trial.c", "trial.b"]), result.Message);
        });
        check("read failure opt in and retest attempt limits are not widened", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b"), Mod("trial.c"));
            foreach (var limits in new[] { new MaintenanceLimits(), new MaintenanceLimits(MaximumAttempts: 1)
                { ReadFailureTrialsEnabled = true } })
            {
                var game = new Session(c, Original) { Report = _ => Error("trial.a") }; var client = new Client();
                var result = ReadRun(c, game, client, limits: limits);
                Assert(result.TrialExclusions.Count == 0 && game.Tested.Count == 1);
            }
        });
        check("read failure known dependents retain original order and all protection", () =>
        {
            var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b", ["trial.a"]), Mod("trial.c"));
            var trial = ReadCandidates(c, Error("trial.a")).Single();
            Assert(trial.RemovedIds.SequenceEqual(["trial.a", "trial.b"]));
            Assert(ReadCandidates(c, Error("trial.a"), retained: ["trial.b"]).Count == 0);
            Assert(ReadCandidates(c, Error("trial.a"), retained: ["trial.a"]).Count == 0);
            var official = ReadCatalogue(Unreadable("trial.a") with { Source = ModSource.Game }, Mod("trial.b"), Mod("trial.c"));
            Assert(ReadCandidates(official, Error("trial.a")).Count == 0);
            var unknown = ReadCatalogue(Unreadable("trial.a"), Unreadable("trial.b") with { Metadata = null }, Mod("trial.c"));
            Assert(ReadCandidates(unknown, Error("trial.a"), retained: ["trial.b"]).Count == 0);
            Assert(ReadCandidates(unknown, Error("trial.a")).Single().SelectedId == "trial.a");
            var protectedThroughDependency = ReadCatalogue(Unreadable("trial.a"),
                Unreadable("trial.b"), Mod("trial.c", ["trial.b"]));
            Assert(ReadCandidates(protectedThroughDependency, Error("trial.a"), retained: ["trial.c"]).Count == 0);
            // Known dependent edges remain included even when the dependent also has a read failure.
            var affectedChild = ReadCatalogue(Unreadable("trial.a"), Unreadable("trial.b", ["trial.a"]), Mod("trial.c"));
            Assert(ReadCandidates(affectedChild, Error("trial.a")).Single().RemovedIds.SequenceEqual(["trial.a", "trial.b"]));
            var game = new Session(c, Original) { Report = ids => ids.Contains("trial.a") ? Error("trial.a") : new(true, []) };
            var client = new Client(); var result = ReadRun(c, game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && client.Calls == 0
                && game.Tested[1].SequenceEqual(["trial.c"]));
        });
        check("opaque read failure needs the identical token and revalidates on replay", () =>
        {
            string[] order = ["opaque_steam", "trial.b", "trial.c"];
            var c = new ModCatalogue([Mod("trial.b"), Mod("trial.c")], unverifiedOrderIdentifiers: ["opaque_steam"]);
            Assert(ReadCandidates(c, Error("opaque"), order).Count == 0);
            var trial = ReadCandidates(c, Error("opaque_steam"), order).Single();
            Assert(trial.ReadFailureRemoval && trial.RemovedIds.SequenceEqual(["opaque_steam"]));
            var dependent = new ModCatalogue([Mod("trial.b", ["opaque_steam"]), Mod("trial.c")],
                unverifiedOrderIdentifiers: ["opaque_steam"]);
            Assert(ReadCandidates(dependent, Error("opaque_steam"), order).Single().RemovedIds.SequenceEqual(["opaque_steam", "trial.b"]));
            Assert(ReadCandidates(dependent, Error("opaque_steam"), order, ["trial.b"]).Count == 0);
            Assert(TrialExclusionPlanning.Replay(c, order, [], Version, [trial]).SequenceEqual(["trial.b", "trial.c"]));
            Throws(() => TrialExclusionPlanning.Replay(new([Mod("trial.b"), Mod("trial.c")]), order, [], Version, [trial]));
        });
        foreach (var duplicate in new[] { false, true })
            check("stale opaque scope " + (duplicate ? "with duplicate installations" : "with readable installation")
                + " cannot authorize read failure removal or replay", () =>
            {
                var unresolved = new ModCatalogue([Mod("trial.b"), Mod("trial.c")],
                    unverifiedOrderIdentifiers: ["trial.a"]);
                var oldTrial = ReadCandidates(unresolved, Error("trial.a")).Single();
                Assert(oldTrial.ReadFailureRemoval);
                var readable = Mod("trial.a");
                ModInstallation[] installations = duplicate
                    ? [readable, readable with { Directory = Path.Combine(scratch, "duplicate.trial.a"), FolderName = "duplicate.trial.a" },
                        Mod("trial.b"), Mod("trial.c")]
                    : [readable, Mod("trial.b"), Mod("trial.c")];
                var stale = new ModCatalogue(installations, unverifiedOrderIdentifiers: ["trial.a"]);
                Assert(stale.IsUnverifiedOrderIdentifier("trial.a")
                    && stale.Resolve("trial.a", Version).Candidates.Count == (duplicate ? 2 : 1));
                Assert(!MetadataReadFailureTrials.IsAffected(stale, "trial.a", Version));
                Assert(ReadCandidates(stale, Error("trial.a")).Count == 0);
                Throws(() => TrialExclusionPlanning.Replay(stale, Original, [], Version, [oldTrial]));
            });
        foreach (var applied in new[] { false, true })
            check("read failure recovery " + (applied ? "after" : "before") + " apply reuses journal without AI", () =>
            {
                var c = ReadCatalogue(Unreadable("trial.a"), Mod("trial.b"), Mod("trial.c"));
                var limits = new MaintenanceLimits { ReadFailureTrialsEnabled = true };
                var root = Root(); var request = Request() with { OutputTokenLimit = limits.OutputTokensPerRequest };
                var trial = ReadCandidates(c, Error("trial.a")).Single();
                string[] current = applied ? ["trial.b", "trial.c"] : Original;
                var tested = new ModCatalogue(current.Select(id => c.Resolve(id, Version).UniqueInstallation!).ToArray());
                var feedback = MaintenanceFeedback.Capture(applied ? 2 : 1, current,
                    applied ? new(true, []) : Error("trial.a"), AiOrderPlanner.Fingerprint(current, tested, [], [], Version));
                var hash = AiTaskRecoveryStore.ContextHash(request, AiOrderPlanner.Fingerprint(Original, c, [], [], Version));
                string id;
                using (var task = new AiTaskRecoveryStore(root).Create(new(AiTaskKind.Maintenance, AiTaskPhase.Applying,
                    hash, Original, current, DateTimeOffset.UtcNow)
                    { Limits = limits, TestOriginalFirst = true, DeadlineUtc = DateTimeOffset.MaxValue,
                        Attempts = applied ? 2 : 1, TrialExclusions = [trial], Feedback = feedback,
                        SeenOrders = applied ? [Original, current] : [Original] })) id = task.Id;
                var game = new Session(c, Original); var client = new Client();
                var result = ReadRun(c, game, client, root: root, resume: id, limits: limits);
                Assert(result.Outcome == MaintenanceOutcome.Passed && result.TrialExclusions.Count == 1 && client.Calls == 0
                    && game.Tested.Count == (applied ? 0 : 1), result.Message);
            });

        check("helper IO failure does not veto independently owned mod errors or authorize unrelated exclusions", () =>
        {
            var data = Root(); Directory.CreateDirectory(data);
            var helperText = "ERROR UnauthorizedAccessException: Access is denied\n at RimWise.TestHelper.ProbeComponent.Progress()";
            var modText = "Exception from asynchronous event: Fixture.WorldGeneration failed trial.a";
            var helper = new GameLogSnapshot(Path.Combine(data, "workbench-helper-failure.txt"), DateTimeOffset.UtcNow,
                helperText, [new(helperText, 1, [], helperText)], false);
            var mod = new GameLogSnapshot(Path.Combine(data, "Player.log"), DateTimeOffset.UtcNow,
                modText, [new(modText, 1, ["trial.a"], modText)], false);
            var raw = new OrdinaryGameReport(42, 0, GameTestStopReason.ProbeCompleted, true, true, [helper, mod])
                { ProbeStage = "helper-failed", FailedPhase = "world-generation", MenuPassed = true, ProbeErrors = 1 };
            var feedback = OrdinaryGameTestSession.CreateFeedback(raw, new([], [], false), data);
            Assert(feedback.CanDiagnose && !feedback.StartupVerified && feedback.Errors.Count == 1
                && feedback.StartupFailure?.Context.EnvironmentFailure == false);
            var game = new Session(catalogue, Original) { Report = ids => ids.Contains("trial.a") ? feedback : new(true, [])
                { RuntimeContext = new([new("MissingCrossReference", "trial.b", 1)]) } };
            var client = new Client();
            var result = Run(game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && game.Tested.Count == 2 && client.Calls == 1
                && result.TrialExclusions.Single().SelectedId == "trial.a" && result.OriginalRestored && !game.Accepted
                && result.HasWarnings);
            var helperOnly = OrdinaryGameTestSession.CreateFeedback(raw with { Logs = [helper] }, new([], [], false), data);
            Assert(!helperOnly.CanDiagnose && !helperOnly.StartupVerified
                && TrialExclusionPlanning.Select(helperOnly, catalogue, Original, [], Version, 1) is null);
            foreach (var blocked in new[] { raw with { CaptureCompleted = false }, raw with { ProbeStage = "order-mismatch" },
                raw with { Logs = [mod with { Truncated = true }] } })
                Assert(!OrdinaryGameTestSession.CreateFeedback(blocked, new([], [], false), data).CanDiagnose);
        });

        check("deferred advisory cycle allows related trials and remaining red errors continue exclusion", () =>
        {
            var game = new Session(catalogue, Original)
            {
                Report = ids => ids.Contains("trial.b")
                    ? Error("trial.a") with { DiagnosticFacts = [new("GamePatchFailure", "trial.a"), new("GameLogError", "trial.b")] }
                    : new(true, [])
            };
            var client = new Client();
            var result = new LevelOneMaintenance(client, new(Root()), game).RunAsync(Request(), catalogue,
                [new("trial.b", "trial.c"), new("trial.c", "trial.b")], new(), null, default).GetAwaiter().GetResult();
            Assert(result.Outcome == MaintenanceOutcome.Passed && game.Tested.Count == 3 && client.Calls == 0
                && game.Tested[1].SequenceEqual(["trial.a", "trial.c", "trial.b"])
                && result.TrialExclusions.Select(t => t.SelectedId).SequenceEqual(["trial.b"])
                && result.OriginalRestored && !game.Accepted,
                $"{result.Message}; tests={game.Tested.Count}, calls={client.Calls}, exclusions={string.Join(",", result.TrialExclusions.Select(t => t.SelectedId))}");
            var kept = AiOrderPlanner.RetainedTrialOrder(Original, result.LastAttemptedOrder, catalogue, [], Version,
                [new("trial.b", "trial.c"), new("trial.c", "trial.b")], [], result.TrialExclusions);
            Assert(kept.Rejected.Count == 0 && kept.After.ToHashSet().SetEquals(Original)
                && kept.Notices.Any(n => n.Contains("循环")));
        });

        check("error subject owner outranks unrelated interceptor package in the same exception stack", () =>
        {
            var report = new MaintenanceGameReport(false, ["XML error Class=\"Fixture.BrokenDefinition\" failed"])
            {
                CanDiagnose = true,
                DiagnosticFacts = [new("GamePatchFailure", "trial.a"), new("GameLogError", "trial.b")],
                LogContext = new([], [new("Fixture.BrokenDefinition", "trial.b", "assembly-type")], false)
            };
            var trial = TrialExclusionPlanning.Select(report, catalogue, Original, [], Version, 1);
            Assert(trial?.SelectedId == "trial.b", "error subject must precede incidental patch frame");
            Assert(TrialExclusionPlanning.Select(report, catalogue, Original, ["trial.b"], Version, 1)?.SelectedId == "trial.a",
                "subject priority must not bypass retention");
        });
        check("declining trial removals keeps initial and later reorders and recursively added dependencies", () =>
        {
            var mods = new ModCatalogue([Mod("trial.a", ["dependency.mod"]), Mod("trial.b"), Mod("trial.c"), Mod("dependency.mod")]);
            var trial = new TrialExclusion("trial.a", ["trial.a"],
                ["dependency.mod", "trial.c", "trial.a", "trial.b"], ["GamePatchFailure"], 1);
            var plan = AiOrderPlanner.RetainedTrialOrder(Original, ["trial.b", "dependency.mod", "trial.c"],
                mods, [], Version, [], [], [trial]);
            Assert(plan.CanApply && plan.After.SequenceEqual(["trial.b", "dependency.mod", "trial.a", "trial.c"]));
            Assert(AiOrderPlanner.RetainedTrialOrder(Original, ["trial.b", "dependency.mod", "inactive.mod"],
                mods, [], Version, [], [], [trial]).Rejected.Count > 0);
        });
        check("declining cumulative trials restores every excluded mod with latest surviving order", () =>
        {
            var trials = new[] {
                new TrialExclusion("trial.a", ["trial.a"], ["trial.c", "trial.a", "trial.b"], ["GamePatchFailure"], 1),
                new TrialExclusion("trial.b", ["trial.b"], ["trial.b", "trial.c"], ["GamePatchFailure"], 2) };
            var plan = AiOrderPlanner.RetainedTrialOrder(Original, ["trial.c"], catalogue, [], Version, [], [], trials);
            Assert(plan.CanApply && plan.After.SequenceEqual(["trial.b", "trial.a", "trial.c"]));
            Throws(() => AiOrderPlanner.RetainedTrialOrder(Original, ["trial.c"], catalogue, ["trial.a"], Version, [], [], trials));
        });

        foreach (var failure in new[] { AiReplyFailure.InvalidJson, AiReplyFailure.InvalidStructure, AiReplyFailure.IncompleteResponse })
            check("paid " + failure + " after a valid reorder stops without more trials or requests", () =>
            {
                var game = new Session(catalogue, Original) { Report = order => order.Contains("trial.a")
                    ? Error("trial.a") with { Errors = [order[0] == "trial.a" ? "first synthetic error" : "different synthetic error"],
                        ErrorSignatures = [Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            Encoding.UTF8.GetBytes(order[0] == "trial.a" ? "first synthetic error" : "different synthetic error")))],
                        StartupFailure = new(new("ErrorDetected", true, true,
                        [new("TesterDetectedLogErrors")]), []) } : new(true, []) };
                using var handler = new ReplyHandler(call => Envelope(call == 1
                    ? "{\"actions\":[{\"kind\":\"reorder\",\"packageId\":\"trial.b\",\"targetIndex\":1}],"
                        + "\"startupDiagnosis\":{\"category\":\"mod\",\"reason\":\"synthetic reorder\",\"suggestions\":[]}}"
                    : failure == AiReplyFailure.InvalidJson ? "{"
                    : "{\"actions\":[],\"provider-secret-field\":true}",
                    call == 2 && failure == AiReplyFailure.IncompleteResponse ? "length" : "stop",
                    call == 1 ? 24095 : 29072));
                using var http = new HttpClient(handler); using var client = new AiClient(http);
                var result = Run(game, client, limits: new(TokenBudget: 200000));
                Assert(result.Outcome == MaintenanceOutcome.Stopped && game.Tested.Count == 2 && handler.Count == 2
                    && result.ReportedTokens == 53167 && result.TrialExclusions.Count == 0
                    && result.OriginalRestored && !game.Accepted && !result.TrialRemovalsApproved,
                    $"{failure}: {result.Outcome}: {result.Message}; tests={game.Tested.Count}, calls={handler.Count}");
                Assert(game.Tested[1].SequenceEqual(["trial.b", "trial.a", "trial.c"])
                    && result.ReplyFailure != AiReplyFailure.None && result.Message.Contains("已停止自动排序"));
            });

        foreach (var status in new[] { 400, 401, 403, 404, 405, 413, 422, 429, 500, 502, 503 })
            check("HTTP " + status + " stops maintenance and recovery without exclusions or repeated calls", () =>
            {
                var root = Root();
                var game = new Session(catalogue, Original) { Report = _ => Error("trial.a") };
                var client = new Client { Reply = _ => new(false, "private provider body", null, null)
                    { Failure = AiReplyFailure.ServiceFailure, ServiceError = new("http", status) { Provider = "siliconflow" } } };
                var result = Run(game, client, root);
                Assert(result.Outcome == MaintenanceOutcome.Stopped && client.Calls == 1 && game.Tested.Count == 1
                    && result.TrialExclusions.Count == 0 && result.OriginalRestored && result.HasUnknownUsage
                    && result.Message.Contains("HTTP " + status) && !result.Message.Contains("private provider body"));
                game = new(catalogue, Original); client = new();
                var resumed = Run(game, client, root, result.TaskId);
                Assert(resumed.Outcome == MaintenanceOutcome.Stopped && client.Calls == 0 && game.Tested.Count == 0
                    && resumed.TrialExclusions.Count == 0 && resumed.OriginalRestored
                    && resumed.Message.Contains("HTTP " + status));
            });

        foreach (var failure in Enum.GetValues<AiReplyFailure>().Where(f => f != AiReplyFailure.None))
        check("AI " + failure + " stops owned-error trials and recovery preserves usage", () =>
        {
            var root = Root();
            var game = new Session(catalogue, Original) { Report = _ => Error("trial.a") };
            var client = new Client { Reply = _ => Rejected() with { Failure = failure } };
            var result = Run(game, client, root);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == failure
                && game.Tested.Count == 1 && client.Calls == 1 && result.TrialExclusions.Count == 0
                && result.OriginalRestored && result.ReportedTokens == 120 && result.Message.Contains("已停止自动排序"));
            game = new(catalogue, Original); client = new();
            var resumed = Run(game, client, root, result.TaskId);
            Assert(resumed.Outcome == MaintenanceOutcome.Stopped && resumed.ReplyFailure == failure
                && client.Calls == 0 && game.Tested.Count == 0 && resumed.TrialExclusions.Count == 0
                && resumed.ReportedTokens == 120);
        });

        check("rejected paid reply persists a trusted reason and never retries an unowned error", () =>
        {
            var root = Root();
            var game = new Session(catalogue, Original) { Report = _ => Error("") };
            var client = new Client { Reply = _ => Rejected() };
            var result = Run(game, client, root);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.ReplyFailure == AiReplyFailure.InvalidStructure
                && result.Message.Contains("结构未通过校验") && !result.Message.Contains("secret") && client.Calls == 1
                && result.TrialExclusions.Count == 0 && result.OriginalRestored);
            using (var task = new AiTaskRecoveryStore(root).Open(result.TaskId!))
                Assert(task.Current.Feedback?.ReplyFailure == AiReplyFailure.InvalidStructure && task.Current.Feedback.ReplyReceived
                    && task.Current.ReportedTokens == 120 && !task.Current.MayApply);
            game = new(catalogue, Original);
            client = new();
            var resumed = Run(game, client, root, result.TaskId);
            Assert(resumed.Outcome == MaintenanceOutcome.Stopped && resumed.ReplyFailure == AiReplyFailure.InvalidStructure
                && client.Calls == 0 && game.Tested.Count == 0 && resumed.ReportedTokens == 120);
        });
        check("rejected paid reply still honors cancellation unknown usage budget output and final attempt limits", () =>
        {
            foreach (var limit in new[] { "cancel", "budget", "output", "attempt" })
            {
                using var cancel = new CancellationTokenSource();
                var game = new Session(catalogue, Original) { Report = _ => Error("trial.a") };
                var client = new Client { Reply = _ =>
                {
                    if (limit == "cancel") cancel.Cancel();
                    return Rejected() with { Usage = limit == "unknown" ? null
                        : limit == "budget" ? new(50000, 20, 50020)
                        : limit == "output" ? new(100, 4097, 4197) : new(100, 20, 120) };
                } };
                var limits = limit == "attempt" ? new MaintenanceLimits(MaximumAttempts: 1) : new();
                var result = Run(game, client, token: cancel.Token, limits: limits);
                Assert(result.Outcome == (limit == "cancel" ? MaintenanceOutcome.Cancelled : MaintenanceOutcome.Stopped)
                    && result.TrialExclusions.Count == 0 && game.Tested.Count == 1 && client.Calls == 1
                    && result.OriginalRestored && !game.Accepted && result.HasUnknownUsage == (limit == "unknown"), limit);
            }
        });

        check("rejected replies cannot bypass retained mods or local environment evidence", () =>
        {
            foreach (var environment in new[] { false, true })
            {
                var game = new Session(catalogue, Original) { Report = _ => environment
                    ? Error("trial.a") with { StartupFailure = new(new("Exited", true, true,
                        [new("GraphicsDriverUnavailable")]), []) } : Error("trial.a") };
                var client = new Client { Reply = _ => Rejected() };
                var result = Run(game, client, retained: environment ? [] : ["trial.a"]);
                Assert(result.Outcome == MaintenanceOutcome.Stopped && result.TrialExclusions.Count == 0
                    && result.OriginalRestored && game.Tested.Count == 1 && client.Calls == 1);
            }
        });

        foreach (var overOutputLimit in new[] { false, true })
        check("saved rejected reply " + (overOutputLimit ? "honors output limits" : "stops related trials") + " without rebilling", () =>
        {
            var root = Root(); var request = Request() with { OutputTokenLimit = new MaintenanceLimits().OutputTokensPerRequest };
            var context = AiOrderPlanner.Fingerprint(Original, new ModCatalogue(Original.Select(id =>
                catalogue.Resolve(id, Version).UniqueInstallation!).ToArray()), [], [], Version);
            var feedback = MaintenanceFeedback.Capture(1, Original, Error("trial.a"), context)
                with { ReplyReceived = true, ReplyFailure = AiReplyFailure.InvalidJson };
            var hash = AiTaskRecoveryStore.ContextHash(request, AiOrderPlanner.Fingerprint(Original, catalogue, [], [], Version));
            string id;
            using (var task = new AiTaskRecoveryStore(root).Create(new(AiTaskKind.Maintenance, AiTaskPhase.ReplySaved,
                hash, Original, Original, DateTimeOffset.UtcNow)
                { Limits = new(), DeadlineUtc = DateTimeOffset.MaxValue, Attempts = 1, Requests = 1,
                    ReportedTokens = overOutputLimit ? 4197 : 120,
                    LastUsage = overOutputLimit ? new(100, 4097, 4197) : new(100, 20, 120),
                    SeenOrders = [Original], Feedback = feedback })) id = task.Id;
            var game = new Session(catalogue, Original); var client = new Client();
            var result = Run(game, client, root, id);
            Assert(result.OriginalRestored && client.Calls == 0 && result.ReportedTokens == (overOutputLimit ? 4197 : 120));
            if (overOutputLimit)
                Assert(result.Outcome == MaintenanceOutcome.Stopped && result.TrialExclusions.Count == 0
                    && game.Tested.Count == 0 && result.Message.Contains("输出上限"));
            else
                Assert(result.Outcome == MaintenanceOutcome.Stopped && result.TrialExclusions.Count == 0
                    && game.Tested.Count == 0 && result.Message.Contains("已停止自动排序"));
        });

        check("map generation failure with unknown AI cause continues related trial and restores before approval", () =>
        {
            var game = new Session(catalogue, Original) { Report = order => order.Contains("trial.a")
                ? Error("trial.a") with { StartupFailure = new(new("ProbeCompleted", false, true,
                    [new("MapGenerationFailure"), new("MainMenuObserved"), new("FactionsObserved")]), []) }
                : new(true, []) };
            var client = new Client();
            var result = Run(game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 2 && client.Calls == 1
                && result.TrialExclusions.Single().SelectedId == "trial.a" && result.OriginalRestored
                && !result.TrialRemovalsApproved && !game.Accepted
                && game.Tested[1].SequenceEqual(["trial.b", "trial.c"]));
        });

        check("helper failure cannot authorize removing mods even when a local error names them", () =>
        {
            var game = new Session(catalogue, Original) { Report = _ => Error("trial.a") with
                { CanDiagnose = false, StartupFailure = new(new("ProbeCompleted", false, true,
                    [new("TestHelperFailure")]), []) } };
            var client = new Client();
            var result = Run(game, client);
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.Attempts == 1
                && result.TrialExclusions.Count == 0 && result.OriginalRestored && !game.Accepted);
        });

        check("unknown cause triggers cumulative related trial exclusions and restores before final approval", () =>
        {
            var game = new Session(catalogue, Original) { Report = order => order.Contains("trial.a") ? Error("trial.a")
                : order.Contains("trial.b") ? Error("trial.b") : new(true, []) };
            var client = new Client();
            var result = Run(game, client);
            Assert(result.Outcome == MaintenanceOutcome.Passed && result.Attempts == 3 && client.Calls == 2
                && result.ReportedTokens == 240 && result.TrialExclusions.Count == 2 && result.ConflictApprovals.Count == 0
                && result.OriginalRestored && !game.Accepted && !result.TrialRemovalsApproved
                && result.LastAttemptedOrder.SequenceEqual(["trial.c"]));
            Assert(game.Tested[1].SequenceEqual(["trial.b", "trial.c"]) && game.Tested[2].SequenceEqual(["trial.c"]));
            Assert(!AiOrderPlanner.PreparedOrder(Original, ["trial.c"], catalogue, [], Version, [], []).CanApply);
            Throws<InvalidOperationException>(() => AiOrderPlanner.ConfirmedTrialOrder(Original, ["trial.c"], catalogue, [], Version, [], [],
                result.TrialExclusions, false));
            Assert(AiOrderPlanner.ConfirmedTrialOrder(Original, ["trial.c"], catalogue, [], Version, [], [],
                result.TrialExclusions, true).CanApply);
        });
        check("trial dependency closure and protected colors aliases and official content remain enforced", () =>
        {
            var dependent = Mod("child", ["trial.a"]);
            var related = new ModCatalogue([.. catalogue.Installations, dependent]);
            string[] order = [.. Original, "child"];
            var trial = TrialExclusionPlanning.Select(Error("trial.a"), related, order, [], Version, 1)!;
            Assert(trial.RemovedIds.SequenceEqual(["trial.a", "child"]));
            Assert(TrialExclusionPlanning.Select(Error("trial.a"), related, order, ["child"], Version, 1) is null);
            Assert(TrialExclusionPlanning.Select(Error("trial.a"), related, order, ["trial.a"], Version, 1) is null);
            var workshop = Mod("trial.a") with { Source = ModSource.Workshop, WorkshopId = "123" };
            Assert(TrialExclusionPlanning.Select(Error("trial.a"), new([workshop, Mod("trial.b")]),
                ["trial.a_steam", "trial.b"], ["trial.a"], Version, 1) is null);
            Assert(TrialExclusionPlanning.Select(Error("trial.a"), new([Mod("trial.a") with { Source = ModSource.Game }]),
                ["trial.a"], [], Version, 1) is null);
            Throws(() => TrialExclusionPlanning.Replay(related, order, ["child"], Version, [trial]));
        });
        check("unowned errors inactive suspects and yellow-only observations do not authorize unrelated exclusions", () =>
        {
            Assert(TrialExclusionPlanning.Select(Error("inactive.mod"), catalogue, Original, [], Version, 1) is null);
            Assert(TrialExclusionPlanning.Select(Error("") with { DiagnosticFacts = [] }, catalogue, Original, [], Version, 1) is null);
            Assert(TrialExclusionPlanning.Select(Error("trial.a") with { Errors = [] }, catalogue, Original, [], Version, 1) is null);
            Assert(TrialExclusionPlanning.Select(Error("trial.a") with { CanDiagnose = false }, catalogue, Original, [], Version, 1) is null);
        });
        check("model removal or arbitrary path cannot select an unrelated trial candidate", () =>
        {
            foreach (var action in new[] { new AiAction("remove", "trial.b", null, false, null, null),
                new AiAction("reorder", "trial.b", 1, false, "C:\\Windows\\fixture", null) })
            {
                var game = new Session(catalogue, Original) { Report = order => order.Contains("trial.a") ? Error("trial.a") : new(true, []) };
                var client = new Client { Actions = [action] };
                var result = Run(game, client);
                Assert(result.Outcome == MaintenanceOutcome.Stopped && result.TrialExclusions.Count == 0
                    && result.ReplyFailure == AiReplyFailure.InvalidPlan && result.Message.Contains("不允许的动作")
                    && game.Tested.Count == 1 && client.Calls == 1 && result.OriginalRestored);
            }
        });
        check("DeepSeek normalizes only the same unique active Workshop installation", () =>
        {
            string[] start = ["trial.a", "trial.b_steam", "trial.c"];
            var workshop = Mod("trial.b") with { Source = ModSource.Workshop, WorkshopId = "123" };
            var unique = new ModCatalogue([Mod("trial.a"), workshop, Mod("trial.c")]);
            var collision = new ModCatalogue([.. unique.Installations, Mod("trial.b") with { Directory = Path.Combine(scratch, "second.b") }]);
            var settings = AiSettings.Default with { Enabled = true, Endpoint = "https://relay.invalid/v1/chat/completions", Model = "deepseek-v4.1-flash" };
            var request = Request() with { Settings = settings, ApiKey = "synthetic-key", CurrentOrder = start };
            foreach (var (catalog, index, accepted, issue) in new[] {
                (unique, 1, true, AiPlanIssue.None), (collision, 1, false, AiPlanIssue.Target), (unique, 0, false, AiPlanIssue.Position),
                (new ModCatalogue([Mod("trial.a", ["trial.c"]), workshop, Mod("trial.c")]), 1, false, AiPlanIssue.Constraints) })
            {
                var order = issue == AiPlanIssue.Constraints ? new[] { "trial.c", "trial.a", "trial.b_steam" } : start;
                request = request with { CurrentOrder = order };
                var root = Root();
                var game = new Session(catalog, order) { Report = ids => ids.SequenceEqual(order) ? Error("trial.a") : new(true, []) };
                var proposed = issue == AiPlanIssue.Constraints ? "trial.a" : "trial.b";
                var content = $$"""{"schemaVersion":"1","summary":"合成分析","actions":[{"kind":"reorder","packageId":"{{proposed}}","targetIndex":"{{index}}"}]}""";
                using var handler = new ReplyHandler(_ => Envelope(content, "stop", 120));
                using var http = new HttpClient(handler); using var client = new AiClient(http);
                var result = new LevelOneMaintenance(client, new(root), game).RunAsync(request, catalog, [], new(), null, default).GetAwaiter().GetResult();
                Assert(handler.Count == 1 && result.ReportedTokens == 120 && result.TrialExclusions.Count == 0,
                    result.Message);
                if (accepted)
                {
                    Assert(result.Outcome == MaintenanceOutcome.Passed && game.Accepted && game.Tested.Count == 2
                        && game.Tested[1].SequenceEqual(["trial.b_steam", "trial.a", "trial.c"]), result.Message);
                    continue;
                }
                Assert(result.Outcome == MaintenanceOutcome.Stopped && result.OriginalRestored && !game.Accepted
                    && result.ReplyFailure == AiReplyFailure.InvalidPlan
                    && game.Tested.Count == 1 && result.Message.Contains(AiReplyFailureText.Describe(AiReplyFailure.InvalidPlan, issue)), result.Message);
                using (var task = new AiTaskRecoveryStore(root).Open(result.TaskId!))
                    Assert(task.Current.Feedback?.PlanIssue == issue && !task.Current.MayApply && task.Current.Actions.Length == 0);
                var resumedGame = new Session(catalog, order);
                var resumed = new LevelOneMaintenance(client, new(root), resumedGame, result.TaskId).RunAsync(request, catalog, [], new(), null, default).GetAwaiter().GetResult();
                Assert(resumed.ReplyFailure == AiReplyFailure.InvalidPlan && resumed.ReportedTokens == 120
                    && resumedGame.Tested.Count == 0 && handler.Count == 1, resumed.Message);
            }
        });
        check("trial test limits cancellation and unknown usage restore without silent publication", () =>
        {
            var game = new Session(catalogue, Original) { Report = _ => Error("trial.a") };
            var result = Run(game, new Client(), limits: new(MaximumAttempts: 2));
            Assert(result.Outcome == MaintenanceOutcome.Stopped && result.TrialExclusions.Count == 1
                && result.OriginalRestored && !game.Accepted && game.Tested.Count == 2);
            using var cancel = new CancellationTokenSource();
            game = new(catalogue, Original) { Report = order =>
            {
                if (!order.Contains("trial.a")) cancel.Cancel();
                return Error("trial.a");
            } };
            result = Run(game, new Client(), token: cancel.Token);
            Assert(result.Outcome == MaintenanceOutcome.Cancelled && result.OriginalRestored && !game.Accepted);
            game = new(catalogue, Original) { Report = order => order.Contains("trial.a") ? Error("trial.a") : new(true, []) };
            result = Run(game, new Client { Usage = null });
            Assert(result.HasUnknownUsage && result.TrialExclusions.Count == 1 && game.Tested.Count == 2
                && result.OriginalRestored && result.Outcome == MaintenanceOutcome.Passed && !game.Accepted);
        });
        check("trial journal is append only and copied independently from consumers", () =>
        {
            var root = Root();
            var trial = TrialExclusionPlanning.Select(Error("trial.a"), catalogue, Original, [], Version, 1)!;
            using var task = new AiTaskRecoveryStore(root).Create(new(AiTaskKind.Maintenance, AiTaskPhase.Applying,
                new('a', 64), Original, Original, DateTimeOffset.UtcNow)
                { Limits = new(), DeadlineUtc = DateTimeOffset.MaxValue, Attempts = 1, TrialExclusions = [trial] });
            var copy = task.Current; copy.TrialExclusions[0].RemovedIds[0] = "tampered";
            Assert(task.Current.TrialExclusions[0].RemovedIds[0] == "trial.a");
            Throws(() => task.Save(task.Current with { TrialExclusions = [] }));
            Throws(() => task.Save(task.Current with { TrialExclusions = [trial with { DiagnosticCodes = ["GameLogError"] }] }));
            Throws(() => TrialExclusionPlanning.Replay(catalogue, Original, [], Version,
                [trial with { RemovedIds = ["trial.a", "trial.b"] }]));
        });
        foreach (var applied in new[] { false, true })
            check("trial recovery " + (applied ? "after" : "before") + " private apply reuses paid feedback without rebilling", () =>
            {
                var root = Root(); var request = Request() with { OutputTokenLimit = new MaintenanceLimits().OutputTokensPerRequest };
                var first = TrialExclusionPlanning.Select(Error("trial.a"), catalogue, Original, [], Version, 1)!;
                string[] current = applied ? ["trial.b", "trial.c"] : Original;
                var report = applied ? Error("trial.b") : Error("trial.a");
                var context = AiOrderPlanner.Fingerprint(current, new ModCatalogue(current.Select(id =>
                    catalogue.Resolve(id, Version).UniqueInstallation!).ToArray()), [], [], Version);
                var feedback = MaintenanceFeedback.Capture(applied ? 2 : 1, current, report, context) with { ReplyReceived = true };
                var hash = AiTaskRecoveryStore.ContextHash(request, AiOrderPlanner.Fingerprint(Original, catalogue, [], [], Version));
                string id;
                using (var task = new AiTaskRecoveryStore(root).Create(new(AiTaskKind.Maintenance, AiTaskPhase.Applying,
                    hash, Original, current, DateTimeOffset.UtcNow)
                    { Limits = new(), DeadlineUtc = DateTimeOffset.MaxValue, Attempts = applied ? 2 : 1,
                        Requests = 1, ReportedTokens = 120, LastUsage = new(100, 20, 120), TrialExclusions = [first],
                        SeenOrders = applied ? [Original, current] : [Original], Feedback = feedback })) id = task.Id;
                var game = new Session(catalogue, Original) { Report = _ => new(true, []) }; var client = new Client();
                var result = Run(game, client, root, id);
                Assert(result.Outcome == MaintenanceOutcome.Passed && result.OriginalRestored && client.Calls == 0
                    && result.ReportedTokens == 120 && result.TrialExclusions.Count == (applied ? 2 : 1)
                    && game.Tested.Single().SequenceEqual(applied ? ["trial.c"] : new[] { "trial.b", "trial.c" }),
                    $"{result.Outcome}: {result.Message}; calls={client.Calls}, trials={result.TrialExclusions.Count}, tests={game.Tested.Count}");
            });
    }

    private static MaintenanceGameReport Error(string id) => new(false, ["synthetic XML failure"])
        { CanDiagnose = true, DiagnosticFacts = [new("GamePatchFailure", id)] };

    private sealed class Client : IAiClient
    {
        internal int Calls;
        internal AiAction[] Actions = [];
        internal AiTokenUsage? Usage = new(100, 20, 120);
        internal Func<int, AiClientResult>? Reply;
        public Task<AiConnectionResult> TestConnectionAsync(AiSettings settings, string? key, CancellationToken token) => throw new NotSupportedException();
        public Task<AiClientResult> GetSuggestionAsync(AiRequest request, CancellationToken token)
        {
            Calls++;
            if (Reply is not null) return Task.FromResult(Reply(Calls));
            return Task.FromResult(new AiClientResult(true, "fixture", new(1, "cause not proven", Actions, []), null) { Usage = Usage });
        }
    }

    private static AiClientResult Rejected() => new(false, "provider-secret-field", null, null)
        { Failure = AiReplyFailure.InvalidStructure, Usage = new(100, 20, 120) };

    private static string Envelope(string content, string finish, int total) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content }, finish_reason = finish } },
        usage = new { prompt_tokens = total - 20, completion_tokens = 20, total_tokens = total }
    });

    private sealed class ReplyHandler(Func<int, string> reply) : HttpMessageHandler
    {
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(reply(++Count), Encoding.UTF8, "application/json") });
    }

    private sealed class Session(ModCatalogue catalogue, string[] original) : ILevelOneGameSession, IMaintenanceTrialSession
    {
        internal readonly List<string[]> Tested = [];
        internal Func<IReadOnlyList<string>, MaintenanceGameReport> Report = _ => new(true, []);
        internal bool Accepted;
        private string[] current = original.ToArray();
        private string[] allowed = original.ToArray();
        public Task<bool> VerifySafetyAsync(CancellationToken token) => Task.FromResult(true);
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MaintenanceGameReport> TestAsync(IReadOnlyList<string> order, CancellationToken token)
        {
            Assert(current.SequenceEqual(order)); Tested.Add(order.ToArray()); return Task.FromResult(Report(order));
        }
        public void AuthorizeTrialExclusions(IReadOnlyList<TrialExclusion> trials) =>
            allowed = TrialExclusionPlanning.Replay(catalogue, original, [], Version, trials);
        public Task ApplyAsync(AiOrderPlan plan, CancellationToken token)
        {
            Assert(plan.CanApply && plan.Before.SequenceEqual(current)
                && plan.After.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(allowed));
            current = plan.After.ToArray(); return Task.CompletedTask;
        }
        public Task FinishAsync(bool accept, CancellationToken token)
        { Accepted = accept; if (!accept) current = original.ToArray(); return Task.CompletedTask; }
    }
    private static void Assert(bool value, string message = "Trial exclusion assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
        => Throws<InvalidDataException>(action);
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Unsafe trial accepted.");
    }
}
